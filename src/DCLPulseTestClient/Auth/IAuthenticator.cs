namespace PulseTestClient.Auth;

public interface IAuthenticator
{
    Task<string> LoginAsync(string account, CancellationToken ct);
}