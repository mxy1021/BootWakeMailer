namespace BootWakeMailer.Shared;

/// <summary>
/// One pending notification task in <c>queue.json</c> (architecture.md §6).
/// A task is written to the queue before its first SMTP attempt and stays there
/// until the SMTP server accepts the message (FR-05).
/// </summary>
public sealed class PendingMailEvent
{
    /// <summary>Stable identifier of the task; also useful for diagnosing duplicates.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Which notification trigger created this task.</summary>
    public MailEventType EventType { get; set; }

    /// <summary>Event occurrence time, always UTC (requirements.md §5).</summary>
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Local computer name at the time of the event.</summary>
    public string ComputerName { get; set; } = string.Empty;

    /// <summary>Number of SMTP attempts made so far.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Time of the most recent attempt, or <c>null</c> when never attempted.</summary>
    public DateTime? LastAttemptAtUtc { get; set; }

    /// <summary>Error text of the most recent attempt, or <c>null</c> when it succeeded.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Creates a new pending task with a fresh identifier.
    /// Callers should not pass a timestamp; the overload exists for tests.
    /// </summary>
    /// <param name="eventType">Trigger that produced the task.</param>
    /// <param name="computerName">Computer name to record in the notification.</param>
    /// <param name="occurredAtUtc">Event time; defaults to the current UTC time.</param>
    public static PendingMailEvent Create(
        MailEventType eventType,
        string computerName,
        DateTime? occurredAtUtc = null)
    {
        var occurred = occurredAtUtc ?? DateTime.UtcNow;

        return new PendingMailEvent
        {
            Id = Guid.NewGuid().ToString(),
            EventType = eventType,
            OccurredAtUtc = occurred.Kind == DateTimeKind.Utc ? occurred : occurred.ToUniversalTime(),
            ComputerName = computerName,
        };
    }
}
