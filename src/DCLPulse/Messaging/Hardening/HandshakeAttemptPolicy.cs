using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;
using Pulse.Transport;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Limits repeated handshake attempts from the same peer. Prevents a malicious or buggy
///     client from burning server CPU on ECDSA recovery by replaying HandshakeRequest packets.
///     The per-peer counter lives on <see cref="PeerTransportState.HandshakeAttempts" />; on
///     overflow the peer is disconnected with <see cref="DisconnectReason.AUTH_FAILED" />.
/// </summary>
public sealed class HandshakeAttemptPolicy(
    IOptions<HandshakeAttemptPolicyOptions> options,
    ITransport transport)
    : PeerDefense(transport, PulseMetrics.Hardening.HANDSHAKE_ATTEMPTS_EXCEEDED)
{
    private readonly byte maxAttempts = options.Value.MaxAttempts;

    public bool IsEnabled => maxAttempts > 0;

    /// <summary>
    ///     Records a handshake attempt. Returns <c>true</c> if the caller should continue
    ///     validating; <c>false</c> means the peer has already been disconnected.
    /// </summary>
    public bool TryRecordAttempt(PeerIndex from, PeerState state)
    {
        if (!IsEnabled) return true;

        byte attempts = state.TransportState.HandshakeAttempts;

        if (attempts >= maxAttempts)
            return Reject(from, state, DisconnectReason.AUTH_FAILED);

        state.TransportState = state.TransportState with { HandshakeAttempts = (byte)(attempts + 1) };
        return true;
    }
}
