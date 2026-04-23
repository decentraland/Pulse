namespace Pulse.Messaging.Hardening;

public sealed class FieldValidatorOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:FieldValidator";

    /// <summary>
    ///     Maximum length of <c>EmoteId</c> strings in <c>EmoteStart</c> messages, in UTF-16
    ///     code units. Decentraland emote URNs are typically under 40 chars; 64 leaves margin.
    ///     Zero disables the check.
    /// </summary>
    public int MaxEmoteIdLength { get; set; } = 64;

    /// <summary>
    ///     Maximum length of <c>Realm</c> strings in <c>TeleportRequest</c> messages, in UTF-16
    ///     code units. Zero disables the check.
    /// </summary>
    public int MaxRealmLength { get; set; } = 128;

    /// <summary>
    ///     Maximum accepted one-shot emote duration, in milliseconds. Looping emotes are ended
    ///     by the client sending <c>EmoteStop</c>, so this cap only affects the client-declared
    ///     duration field. Zero disables the check.
    /// </summary>
    public uint MaxEmoteDurationMs { get; set; } = 60_000;
}
