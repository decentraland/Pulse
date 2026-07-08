using DCL.Auth;
using Decentraland.Pulse;
using Google.Protobuf;
using Pulse.Messaging.Hardening;
using Pulse.Peers;
using Pulse.Peers.Simulation;
using Pulse.Transport;
using Pulse.Transport.Hardening;

namespace Pulse.Messaging;

/// <summary>
///     Player handshake: authenticates via the shared <see cref="HandshakeHandlerBase" />
///     pipeline, optionally validating and seeding a client-asserted
///     <see cref="PlayerInitialState" />. The authenticated peer becomes a subject (snapshot
///     ring + spatial grid) so other observers can see it.
/// </summary>
public class HandshakeHandler(MessagePipe messagePipe,
    AuthChainValidator authChainValidator,
    PeerStateFactory peerStateFactory,
    SnapshotBoard snapshotBoard,
    IdentityBoard identityBoard,
    ITransport transport,
    HandshakeAttemptPolicy attemptPolicy,
    PreAuthAdmission preAuthAdmission,
    HandshakeReplayPolicy replayPolicy,
    BanList banList,
    FieldValidator fieldValidator,
    PeerSnapshotPublisher snapshotPublisher,
    ITimeProvider timeProvider,
    ILogger<HandshakeHandler> logger)
    : HandshakeHandlerBase(messagePipe, authChainValidator, peerStateFactory, identityBoard, transport,
        attemptPolicy, preAuthAdmission, replayPolicy, banList, logger)
{
    protected override string LogName => "Handshake";

    protected override ByteString GetAuthChain(ClientMessage message) => message.Handshake.AuthChain;

    protected override bool TryAuthorize(PeerIndex from, PeerState existingState, ClientMessage message, PeerState peer)
    {
        // Initial-state validation runs before the AUTHENTICATED transition is published: a
        // malformed asserted state aborts the handshake cleanly via FieldValidator.Reject (peer
        // disconnected with INVALID_HANDSHAKE_FIELD), so no half-validated state reaches the
        // snapshot ring. InitialState is optional — the legacy connect path skips it and sets
        // realm via a follow-up TeleportRequest.
        PlayerInitialState? initialState = message.Handshake.InitialState;

        return initialState == null
               || fieldValidator.ValidateHandshakeInitialState(from, existingState, initialState);
    }

    protected override void OnAuthenticated(PeerIndex from, PeerState peer, ClientMessage message)
    {
        snapshotBoard.SetActive(from);
        SeedInitialState(from, message.Handshake.InitialState);
    }

    protected override void LogAccepted(PeerIndex from, PeerState peer) =>
        logger.LogInformation("Peer handshake accepted with wallet {Wallet} - peerId {Peer}", peer.WalletId, from);

    /// <summary>
    ///     Apply the asserted starting state the client carried through authentication. The
    ///     client always sends <see cref="PlayerInitialState" /> on (re-)connect because it
    ///     can't observe whether the server still holds its prior session — but on the server
    ///     side this is unconditional: a freshly authenticated slot has no state of its own to
    ///     defer to (cross-slot preservation isn't part of the architecture), and the
    ///     authenticated state is whatever the client just signed for.
    ///     <para />
    ///     Realm is stamped onto the seed snapshot directly so AoI can place the peer
    ///     immediately — without it the reconnecting peer would be invisible to every observer
    ///     until the next <c>TeleportRequest</c>.
    /// </summary>
    private void SeedInitialState(PeerIndex from, PlayerInitialState? initialState)
    {
        if (initialState == null)
            return;

        PeerSnapshotPublisher.EmoteInput? emote = null;

        if (initialState.HasEmoteId && !string.IsNullOrEmpty(initialState.EmoteId))
        {
            uint now = timeProvider.MonotonicTime;
            uint offset = initialState.HasEmoteStartOffsetMs ? initialState.EmoteStartOffsetMs : 0u;

            // Backdate StartTick by the reported offset so observers scrub the animation
            // forward by the elapsed-since-real-start delta on the next EmoteStarted broadcast.
            // Underflow guard keeps the start tick from wrapping if the offset overstates now.
            uint startTick = offset > now ? 0u : now - offset;
            uint? duration = initialState.HasEmoteDurationMs ? initialState.EmoteDurationMs : null;
            int emoteMask = initialState.HasEmoteMask ? initialState.EmoteMask : 0;

            emote = new PeerSnapshotPublisher.EmoteInput(initialState.EmoteId, DurationMs: duration, StartTick: startTick, Mask: emoteMask);
        }

        snapshotPublisher.PublishFromPlayerState(from, initialState.State, emote, realm: initialState.Realm);

        logger.LogInformation("Seeded initial snapshot for peer {Peer} at parcel {Parcel} realm '{Realm}' (emote='{Emote}')",
            from, initialState.State.ParcelIndex, initialState.Realm, emote?.EmoteId ?? "<none>");
    }
}
