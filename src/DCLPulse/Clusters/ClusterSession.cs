namespace Pulse.Clusters;

/// <summary>
///     The session a published assignment belongs to, and the session it displaced when it is the
///     first publish of a new session for a wallet whose previous session still had a retained
///     assignment. Both displaced fields are null on every other publish.
/// </summary>
/// <param name="Session">Lower-cased session key of the peer being published.</param>
/// <param name="DisplacedSession">Session key the wallet's retained assignment belonged to, when different.</param>
/// <param name="DisplacedClusterId">Cluster that retained assignment named.</param>
public readonly record struct ClusterSession(string Session, string? DisplacedSession, string? DisplacedClusterId);
