using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseTestClient;

public class BotBehaviorSettings
{
    public bool JumpEnabled { get; set; } = true;
    public float JumpMinInterval { get; set; } = 3f;
    public float JumpMaxInterval { get; set; } = 10f;
    public float JumpHeight { get; set; } = 2.5f;
    public float Gravity { get; set; } = 15f;

    /// <summary>
    ///     Initial upward velocity derived from desired jump height: v0 = sqrt(2 * g * h)
    /// </summary>
    public float JumpVelocity => MathF.Sqrt(2f * Gravity * JumpHeight);

    public static BotBehaviorSettings Load()
    {
        JsonElement? section = LoadSection("BotBehavior");

        if (section is null)
            return new BotBehaviorSettings();

        return JsonSerializer.Deserialize(section.Value.GetRawText(), BotBehaviorJsonContext.Default.BotBehaviorSettings)
               ?? new BotBehaviorSettings();
    }

    public static int LoadBotsPerProcess()
    {
        JsonElement? root = LoadRoot();

        if (root is null || !root.Value.TryGetProperty("BotsPerProcess", out JsonElement value))
            return 10;

        return value.GetInt32();
    }

    private static JsonElement? LoadRoot()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(path))
            return null;

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static JsonElement? LoadSection(string name)
    {
        JsonElement? root = LoadRoot();

        if (root is null || !root.Value.TryGetProperty(name, out JsonElement section))
            return null;

        return section;
    }
}

[JsonSerializable(typeof(BotBehaviorSettings))]
internal partial class BotBehaviorJsonContext : JsonSerializerContext;
