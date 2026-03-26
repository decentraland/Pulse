using System.Text.RegularExpressions;

namespace PulseTestClient.Profiles;

public class MetaForgeProfileGateway : IProfileGateway
{
    public async Task<Profile> GetAsync(string account, CancellationToken ct)
    {
        var raw = await MetaForge.RunCommandAsync($"account info {account}", ct);
        var output = Regex.Replace(raw, @"\x1B\[[0-9;]*m", "");

        var address = Regex.Match(output, @"Address:\s+(0x[0-9a-fA-F]+)").Groups[1].Value;
        var version = int.Parse(Regex.Match(output, @"Version\s+(\d+)").Groups[1].Value);

        // Match emote rows from the right-side table (lines ending with │ <item> │  │)
        var emotes = Regex.Matches(output, @"│\s+(\w+)\s+│\s+│\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(v => v != "Item")
            .ToArray();

        return new Profile(new Web3Address(address), version, emotes);
    }
}