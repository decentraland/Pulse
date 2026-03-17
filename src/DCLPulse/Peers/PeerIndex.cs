namespace Pulse.Peers;

/// <summary>
///     Zero-based index into fixed peer array (allocated at host creation with maxPeers).
///     It's up to the Transport Service implementation to ensure Indexing (instead of non-sequential IDs).
///     Always in the range [0, maxPeers). Safe to use as a direct array index.
/// </summary>
public readonly struct PeerIndex(uint value) : IEquatable<PeerIndex>
{
    public readonly uint Value = value;

    public static implicit operator uint(PeerIndex id) =>
        id.Value;

    public bool Equals(PeerIndex other) =>
        Value == other.Value;

    public override bool Equals(object? obj) =>
        obj is PeerIndex other && Equals(other);

    public override int GetHashCode() =>
        (int)Value;

    public override string ToString() =>
        Value.ToString();

    public static bool operator ==(PeerIndex left, PeerIndex right) =>
        left.Equals(right);

    public static bool operator !=(PeerIndex left, PeerIndex right) =>
        !(left == right);
}
