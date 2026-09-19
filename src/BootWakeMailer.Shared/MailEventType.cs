namespace BootWakeMailer.Shared;

/// <summary>
/// The notification triggers supported by the MVP (FR-01, FR-02).
/// Serialized by name, so the JSON values are exactly <c>"Startup"</c> and
/// <c>"ResumeAutomatic"</c>.
/// </summary>
public enum MailEventType
{
    /// <summary>Every Windows Service start is one Startup event (FR-01).</summary>
    Startup,

    /// <summary>A Windows power-resume notification (FR-02).</summary>
    ResumeAutomatic,
}
