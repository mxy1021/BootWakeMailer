namespace BootWakeMailer.Shared;

/// <summary>
/// SMTP connection security. Maps directly to the limited MailKit connection mode
/// exposed in the configuration UI (architecture.md §5).
/// Serialized by name, so the JSON values match this enum exactly.
/// </summary>
public enum SmtpSecurityMode
{
    /// <summary>Let MailKit choose based on the server capabilities.</summary>
    Auto,

    /// <summary>Connect in the clear, then upgrade with STARTTLS.</summary>
    StartTls,

    /// <summary>Use TLS from the first byte (implicit TLS).</summary>
    SslOnConnect,

    /// <summary>No transport security. Only sensible for a local relay.</summary>
    None,
}
