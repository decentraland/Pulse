namespace Pulse.Transport.Hardening;

/// <summary>
///     Budget a connection is counted against by <see cref="IpLimiter" />. A scene listener is a
///     full peer — it draws a <see cref="Pulse.Peers.PeerIndex" /> and a transport slot exactly
///     like a player — but a listener fleet runs from few egress IPs and opens many connections,
///     so one shared per-IP cap would either throttle the fleet or have to be raised until it
///     protects nothing. Each class carries its own cap; the classes never borrow from each other.
///     <para />
///     The class is not known at connect: a peer announces itself a listener only when
///     <c>SCENE_LISTENER_HANDSHAKE</c> validates, on the worker thread. Every connection is
///     therefore acquired as <see cref="PLAYER" /> and moved with
///     <see cref="IpLimiter.TryReclassify" /> on promotion.
/// </summary>
public enum ConnectionClass : byte
{
    PLAYER = 0,
    SCENE_LISTENER = 1,
}

/// <summary>
///     Static tables over <see cref="ConnectionClass" />, both indexed by the enum value: the
///     Prometheus label per class and the width every class-indexed array is allocated at.
/// </summary>
public static class ConnectionClasses
{
    /// <summary>Prometheus <c>class</c> label per connection class — index matches the enum value.</summary>
    public static readonly string[] LABELS = ["player", "scene_listener"];

    /// <summary>
    ///     Length of an array indexed by <c>(int)ConnectionClass</c>, derived from
    ///     <see cref="LABELS" /> rather than written out again — a third hand-maintained number
    ///     could disagree with the other two silently. An enum member added without a label here
    ///     therefore leaves those arrays one short, which the per-class tag table's static
    ///     initializer turns into an <see cref="IndexOutOfRangeException" /> the first time a
    ///     hardening metric is recorded, rather than into a metric series nobody notices is gone.
    /// </summary>
    public static readonly int COUNT = LABELS.Length;
}
