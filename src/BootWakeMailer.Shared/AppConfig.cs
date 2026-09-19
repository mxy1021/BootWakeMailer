namespace BootWakeMailer.Shared;

/// <summary>
/// Root document of <c>%ProgramData%\BootWakeMailer\config.json</c>
/// (architecture.md §5). Holds exactly one SMTP connection, one sender and one
/// recipient; no arrays and no templates.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Schema version of this document.</summary>
    public int SchemaVersion { get; set; } = AppConstants.SchemaVersion;

    /// <summary>The single SMTP connection.</summary>
    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>The single sender address.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>The single recipient address.</summary>
    public string ToAddress { get; set; } = string.Empty;
}
