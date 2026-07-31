namespace PulseTestClient.Comms;

/// <summary>
///     Turns a realm's advertised comms adapter into the WebSocket URL ws-connector is reached on.
/// </summary>
/// <remarks>
///     unity-explorer spreads this over six types and two interfaces
///     (<c>CurrentAdapterAddress</c> → <c>LogAdapterAddresses</c> → <c>RefinedAdapterAddresses</c> →
///     <c>ForkGlobalRealmRoom</c>), but the work is three string operations and no I/O — the rest is
///     Unity DI and decorator scaffolding this process has no use for.
///     <para>
///         Accepting the raw adapter form means a value copied straight out of a realm's
///         <c>/about</c> works unchanged, while a plain <c>ws://127.0.0.1:5000/ws</c> for the local
///         compose harness passes through untouched.
///     </para>
/// </remarks>
public static class AdapterAddress
{
    private const string ARCHIPELAGO_PREFIX = "archipelago:archipelago:";

    /// <summary>
    ///     Reduces an adapter string to a bare <c>ws://</c> or <c>wss://</c> URL.
    /// </summary>
    /// <param name="commsAdapter">
    ///     Either a full adapter string (<c>archipelago:archipelago:wss://host/ws</c>) or a URL already.
    /// </param>
    /// <exception cref="PulseException">The value does not reduce to a WebSocket URL.</exception>
    public static string Refine(string commsAdapter)
    {
        if (string.IsNullOrWhiteSpace(commsAdapter))
            throw new PulseException("No comms adapter given; pass --comms-url.");

        string refined = commsAdapter.Trim().Replace(ARCHIPELAGO_PREFIX, string.Empty);

        // Scheme-first, in explorer's order. A realm can prepend routing information the scheme is the
        // only reliable landmark inside, so the URL is taken to start at the first one that appears.
        refined = FromScheme(refined, "wss://");
        refined = FromScheme(refined, "ws://");

        // explorer instead routes a non-ws adapter to a different room type: `https://` means a fixed
        // LiveKit adapter and `offline:offline` means no room at all. Neither is reachable from this
        // harness, and quietly resolving to "no island ever arrives" is precisely the silent
        // no-delivery failure it exists to catch — so it is an error here rather than a branch.
        if (!refined.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !refined.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            throw new PulseException(
                $"'{commsAdapter}' is not a ws-connector adapter: expected a ws:// or wss:// URL, resolved to '{refined}'.");

        return refined;
    }

    private static string FromScheme(string url, string scheme)
    {
        int index = url.IndexOf(scheme, StringComparison.OrdinalIgnoreCase);
        return index <= 0 ? url : url[index..];
    }
}
