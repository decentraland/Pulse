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
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(path))
            return new BotBehaviorSettings();

        string json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("BotBehavior", out JsonElement section))
            return new BotBehaviorSettings();

        return JsonSerializer.Deserialize(section.GetRawText(), BotBehaviorJsonContext.Default.BotBehaviorSettings)
               ?? new BotBehaviorSettings();
    }
}

[JsonSerializable(typeof(BotBehaviorSettings))]
internal partial class BotBehaviorJsonContext : JsonSerializerContext;
