namespace BootWakeMailer.Shared;

/// <summary>
/// The single SMTP connection and account stored in <c>config.json</c> (FR-03).
/// </summary>
public sealed class SmtpSettings
{
    /// <summary>SMTP server host name.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP server port.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Transport security mode.</summary>
    public SmtpSecurityMode SecurityMode { get; set; } = SmtpSecurityMode.StartTls;

    /// <summary>Authentication user name (the single sender account).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Base64 text containing the DPAPI-protected password. The plaintext password
    /// is never stored (FR-03); use <see cref="SecretProtector"/> at the edges.
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;
}
