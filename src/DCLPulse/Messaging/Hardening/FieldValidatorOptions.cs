namespace Pulse.Messaging.Hardening;

public sealed class FieldValidatorOptions
{
    public const string SECTION_NAME = "Messaging:Hardening:FieldValidator";

    /// <summary>
    ///     Maximum length of <c>Realm</c> strings in <c>TeleportRequest</c> messages, in UTF-16
    ///     code units. Per ADR-144 the realm string may be a DCL World subdomain
    ///     (<c>name.dcl.eth</c>, capped at 23 chars by the 15-char DCL claimable-name rule), an
    ///     ENS name (up to 255 chars per ENS-label spec), a DAO catalyst friendly name, or a
    ///     full catalyst URL. 255 covers all legitimate inputs while still bounding the field
    ///     as a sanity check. Zero disables the check.
    /// </summary>
    public int MaxRealmLength { get; set; } = 255;

    /// <summary>
    ///     Maximum accepted one-shot emote duration, in milliseconds. Looping emotes are ended
    ///     by the client sending <c>EmoteStop</c>, so this cap only affects the client-declared
    ///     duration field. Zero disables the check.
    /// </summary>
    public uint MaxEmoteDurationMs { get; set; } = 60_000;
}
