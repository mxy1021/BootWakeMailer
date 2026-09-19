namespace BootWakeMailer.Shared;

/// <summary>
/// Fixed values shared by the Windows Service and the configuration tool
/// (architecture.md §3.1).
/// </summary>
public static class AppConstants
{
    /// <summary>Registered Windows Service name (architecture.md §13).</summary>
    public const string ServiceName = "BootWakeMailer";

    /// <summary>
    /// Windows Service custom command that requests an immediate retry of pending
    /// tasks (architecture.md §12).
    /// </summary>
    public const int ImmediateRetryCommand = 128;

    /// <summary>Schema version written to config.json, queue.json and status.json.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Name of the application data directory under %ProgramData%.</summary>
    public const string DataDirectoryName = "BootWakeMailer";

    /// <summary>
    /// Fixed automatic retry interval for pending tasks (FR-06). Retries continue
    /// without a maximum attempt count.
    /// </summary>
    public const int RetryIntervalSeconds = 60;

    /// <summary>
    /// Upper bound for one complete SMTP send attempt, so a hung or unreachable server
    /// cannot block queue processing indefinitely.
    /// </summary>
    public const int SmtpSendTimeoutSeconds = 30;
}
