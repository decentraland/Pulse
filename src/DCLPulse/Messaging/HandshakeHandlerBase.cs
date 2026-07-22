using DCL.Auth;
using Decentraland.Pulse;
using Google.Protobuf;
using Pulse.Messaging.Hardening;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;
using System.Text.Json;

namespace Pulse.Messaging;

/// <summary>
///     Shared handshake pipeline for <see cref="HandshakeHandler" /> and
///     <see cref="SceneListenerHandshakeHandler" />. Owns the security-critical common flow —
///     presence + admission gate, attempt throttle, auth-chain validation, ban and replay
///     checks, and the PENDING_AUTH → AUTHENTICATED promotion (pre-auth release,
///     duplicate-session eviction, identity registration, response sending) — and defers the
///     parts that differ between a player and a scene-listener connection to template hooks.
///     <para />
///     Concrete handlers are DI singletons invoked from every worker thread, so this class holds
///     no per-call state: everything a hook produces (e.g. the listener's parcel set) is stamped
///     onto the peer passed through the pipeline, never onto an instance field.
/// </summary>
public abstract class HandshakeHandlerBase(
    MessagePipe messagePipe,
    AuthChainValidator authChainValidator,
    PeerStateFactory peerStateFactory,
    IdentityBoard identityBoard,
    ITransport transport,
    HandshakeAttemptPolicy attemptPolicy,
    PreAuthAdmission preAuthAdmission,
    HandshakeReplayPolicy replayPolicy,
    BanList banList,
    ILogger logger) : IMessageHandler
{
    public void Handle(Dictionary<PeerIndex, PeerState> peers, PeerIndex from, ClientMessage message)
    {
        // The router guarantees a Connected lifecycle event precedes the first message, so a
        // missing state means the peer is already being torn down — nothing to do.
        if (!peers.TryGetValue(from, out PeerState? existingState))
            return;

        // Handler-specific admission gate. Silent drop, per the pre-auth convention.
        if (!CanBeginHandshake(existingState))
            return;

        // Throttle before any parsing/crypto work — attempt counter is per-peer on PeerState.
        // Policy owns the disconnect on violation.
        if (!attemptPolicy.TryRecordAttempt(from, existingState))
            return;

        try
        {
            (string Wallet, string Timestamp)? auth = Authenticate(GetAuthChain(message));

            if (auth == null)
            {
                SendResponse(from, success: false, "Invalid auth chain JSON");
                logger.LogInformation("{Handshake} failed: cannot parse auth-chain", LogName);
                return;
            }

            (string wallet, string timestamp) = auth.Value;

            // Platform ban list — checked before the replay cache so a banned wallet doesn't
            // consume an anti-replay slot. The ban list is populated by BansPollingHttpService on
            // a background timer; when the poller is disabled (no moderator token) the list is
            // empty and this check is a constant-time no-op.
            if (banList.IsBanned(wallet))
            {
                RejectBanned(from, existingState, wallet);
                return;
            }

            // Replay guard — a valid signature alone isn't enough. Reject handshakes whose
            // (wallet, timestamp) pair has already been accepted within the anti-replay window.
            // The cache owns the disconnect; state flips to PENDING_DISCONNECT via PeerDefense.
            if (!replayPolicy.TryAdmit(from, existingState, wallet, timestamp))
                return;

            PeerState peer = peerStateFactory.Create();
            peer.WalletId = wallet;
            peer.ConnectionState = PeerConnectionState.AUTHENTICATED;

            // Handler-specific field validation + peer stamping, before the peer is published to
            // the dict. A validation failure rejects the peer via FieldValidator (disconnect with
            // a message-specific reason) and returns false, so no half-authenticated state
            // survives — the freshly built peer is discarded, never registered anywhere.
            if (!TryAuthorize(from, existingState, message, peer))
                return;

            peers[from] = peer;

            // Promotion out of PENDING_AUTH — frees both the global pre-auth budget and the
            // per-IP pre-auth slot in one call.
            preAuthAdmission.ReleaseOnPromotion(from);

            EvictDuplicateSession(from, wallet);

            identityBoard.Set(from, wallet);

            OnAuthenticated(from, peer, message);

            SendResponse(from, success: true, error: null);

            LogAccepted(from, peer);
        }
        catch (Exception e)
        {
            SendResponse(from, success: false, e.Message);
            logger.LogInformation("{Handshake} failed: {Error}", LogName, e.Message);
        }
    }

    // ── Template hooks ──────────────────────────────────────────────

    /// <summary>Human-readable handshake name, used only in log messages.</summary>
    protected abstract string LogName { get; }

    /// <summary>Extracts the signed-fetch auth chain from this handler's envelope variant.</summary>
    protected abstract ByteString GetAuthChain(ClientMessage message);

    /// <summary>
    ///     Whether a peer in the given state may begin this handshake. The base default admits
    ///     any state; both concrete handlers override it to require PENDING_AUTH, so an
    ///     already-authenticated peer can't re-key its session in place.
    /// </summary>
    protected virtual bool CanBeginHandshake(PeerState existingState) => true;

    /// <summary>
    ///     Validates handler-specific handshake fields against <paramref name="existingState" />
    ///     and stamps any handler-specific descriptor onto the freshly built
    ///     <paramref name="peer" /> (already carrying wallet + AUTHENTICATED). Returns false when
    ///     validation rejected the peer — the caller then aborts without publishing it.
    /// </summary>
    protected abstract bool TryAuthorize(PeerIndex from, PeerState existingState, ClientMessage message, PeerState peer);

    /// <summary>
    ///     Side effects to run once the peer is promoted and its identity registered, before the
    ///     success response is sent (e.g. seed the snapshot ring, bump a gauge). Default: none.
    /// </summary>
    protected virtual void OnAuthenticated(PeerIndex from, PeerState peer, ClientMessage message) { }

    /// <summary>Logs the accepted handshake, after the success response is queued.</summary>
    protected abstract void LogAccepted(PeerIndex from, PeerState peer);

    // ── Shared mechanics ────────────────────────────────────────────

    /// <summary>
    ///     Parses the signed-fetch headers JSON, rebuilds the expected connect payload, and
    ///     validates the ECDSA auth chain. Returns null when the JSON cannot be parsed; throws
    ///     (same exceptions as <see cref="AuthChainValidator.Validate" />) when the chain is
    ///     invalid — the caller's try/catch turns both into a handshake reject.
    /// </summary>
    private (string Wallet, string Timestamp)? Authenticate(ByteString authChain)
    {
        string authChainJson = authChain.ToStringUtf8();
        Dictionary<string, string>? headers = JsonSerializer.Deserialize(authChainJson, HandshakeJsonContext.Default.DictionaryStringString);

        if (headers == null)
            return null;

        IReadOnlyList<AuthLink> chain = AuthChainParser.ParseFromSignedFetchHeaders(headers);

        string timestamp = string.Empty;
        string metadata = string.Empty;

        foreach (KeyValuePair<string, string> kv in headers)
        {
            if (kv.Key.Equals("x-identity-timestamp", StringComparison.OrdinalIgnoreCase))
                timestamp = kv.Value;

            if (kv.Key.Equals("x-identity-metadata", StringComparison.OrdinalIgnoreCase))
                metadata = kv.Value;
        }

        string expectedPayload = SignedFetch.BuildSignedFetchPayload("connect", "/", timestamp, metadata);
        AuthChainValidationResult result = authChainValidator.Validate(chain, expectedPayload);

        return (result.UserAddress, timestamp);
    }

    private void EvictDuplicateSession(PeerIndex from, string wallet)
    {
        // Duplicate player_id: evict the existing session, accept the new one — avoids ghost
        // connections holding a slot for a wallet that has reconnected.
        if (identityBoard.TryGetPeerIndexByWallet(wallet, out PeerIndex duplicatedPeer) && duplicatedPeer != from)
        {
            transport.Disconnect(duplicatedPeer, DisconnectReason.DUPLICATE_SESSION);
            logger.LogInformation("Duplicated peer found {Wallet}, disconnecting peer {Peer}", wallet, duplicatedPeer);
        }
    }

    private void RejectBanned(PeerIndex from, PeerState state, string wallet)
    {
        SendResponse(from, success: false, "banned");
        state.ConnectionState = PeerConnectionState.PENDING_DISCONNECT;
        PulseMetrics.Hardening.BANNED_REFUSED.Add(1);
        transport.Disconnect(from, DisconnectReason.BANNED);
        logger.LogInformation("{Handshake} rejected: wallet {Wallet} is banned", LogName, wallet);
    }

    private void SendResponse(PeerIndex to, bool success, string? error)
    {
        var response = new HandshakeResponse { Success = success };

        if (error != null)
            response.Error = error;

        messagePipe.Send(new MessagePipe.OutgoingMessage(to, new ServerMessage
        {
            Handshake = response,
        }, PacketMode.RELIABLE));
    }
}
