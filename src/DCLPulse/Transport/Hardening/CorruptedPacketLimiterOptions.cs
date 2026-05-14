namespace Pulse.Transport.Hardening;

public sealed class CorruptedPacketLimiterOptions
{
    public const string SECTION_NAME = "Transport:Hardening:CorruptedPacket";

    /// <summary>
    ///     Sustained number of corrupted packets a peer may send per minute before the
    ///     transport disconnects it with <see cref="DisconnectReason.PACKET_CORRUPTED" />.
    ///     Counts both oversized packets (larger than <c>Transport.BufferSize</c>) and packets
    ///     that fail protobuf parsing. Default 5 — well-formed clients produce zero corrupt
    ///     packets, so this is strict enough to terminate fuzzers quickly while still
    ///     tolerating rare middlebox reassembly anomalies. Zero disables the limit
    ///     (dev/load tests).
    /// </summary>
    public int MaxPerMinute { get; set; } = 5;

    /// <summary>
    ///     Maximum corrupt packets a peer can fire in a burst before refilling at
    ///     <see cref="MaxPerMinute" />. Default 5 matches the sustained rate — a peer can
    ///     hit five corrupt packets back-to-back without a refill, after which it must wait
    ///     <c>60000 / MaxPerMinute</c> ms for each subsequent token. Stored as a byte per
    ///     peer, so values above 255 are clamped on startup. Zero disables the limit.
    /// </summary>
    public int BurstCapacity { get; set; } = 5;
}
