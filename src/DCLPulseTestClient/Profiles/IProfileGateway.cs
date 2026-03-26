namespace PulseTestClient.Profiles;

public interface IProfileGateway
{
    Task<Profile> GetAsync(string account, CancellationToken ct);
}