namespace DCL.Auth;

public static class SignedFetch
{
    /// <summary>
    /// Per Signed Fetch spec: &lt;method&gt;:&lt;path&gt;:&lt;timestamp&gt;:&lt;metadata&gt; (lower-case).  [oai_citation:14‡docs.decentraland.org](https://docs.decentraland.org/contributor/authentication/signed-fetch)
    /// </summary>
    public static string BuildSignedFetchPayload(string method, string path, string timestamp, string metadata)
        => $"{method.ToLowerInvariant()}:{path.ToLowerInvariant()}:{timestamp}:{metadata}";
}
