using Microsoft.Extensions.Options;
using Pulse.Metrics;
using Pulse.Peers;

namespace Pulse.Messaging.Hardening;

/// <summary>
///     Limits repeated handshake attempts from the same peer. Prevents a malicious or buggy
///     client from burning server CPU on ECDSA recovery by replaying HandshakeRequest packets.
///     The per-peer counter lives on <see cref="PeerTransportState.HandshakeAttempts" /> — this
///     class is stateless and safe for singleton registration.
/// </summary>
public sealed class HandshakeAttemptPolicy(IOptions<HandshakeAttemptPolicyOptions> options)
{
    private readonly byte maxAttempts = options.Value.MaxAttempts;

    public bool IsEnabled => maxAttempts > 0;

    /// <summary>
    ///     Increments the peer's handshake-attempt counter. Returns <c>false</c> if the peer has
    ///     already exceeded the limit — caller should disconnect without further processing.
    /// </summary>
    public bool TryRecordAttempt(PeerState state)
    {
        if (!IsEnabled) return true;

        byte attempts = state.TransportState.HandshakeAttempts;

        if (attempts >= maxAttempts)
        {
            PulseMetrics.Hardening.HANDSHAKE_ATTEMPTS_EXCEEDED.Add(1);
            return false;
        }

        state.TransportState = state.TransportState with { HandshakeAttempts = (byte)(attempts + 1) };
        return true;
    }
}
