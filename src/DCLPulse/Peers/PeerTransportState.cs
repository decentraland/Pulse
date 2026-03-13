namespace Pulse.Peers;

/// <summary>
///     Transport-related values detached from the transport implementation itself
/// </summary>
public readonly record struct PeerTransportState(uint? ConnectionTime, uint? DisconnectionTime) { }
