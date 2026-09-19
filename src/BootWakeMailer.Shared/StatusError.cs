namespace BootWakeMailer.Shared;

/// <summary>
/// The most recent operational error recorded in <c>status.json</c>
/// (architecture.md §7). Must never contain the SMTP password or any other
/// secret (architecture.md §14.10).
/// </summary>
public sealed class StatusError
{
    /// <summary>When the error occurred, UTC.</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Short name of the failing operation, for example <c>SmtpSend</c>.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>Exception type name, for example <c>SmtpCommandException</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Concise, actionable message shown in the configuration tool.</summary>
    public string Message { get; set; } = string.Empty;
}
