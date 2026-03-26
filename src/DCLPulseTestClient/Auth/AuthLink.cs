namespace PulseTestClient.Auth;

[Serializable]
public struct AuthLink
{
    public AuthLinkType type;
    public string payload;
    public string? signature;
}