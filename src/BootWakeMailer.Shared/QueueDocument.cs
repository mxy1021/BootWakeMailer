namespace BootWakeMailer.Shared;

/// <summary>
/// Root document of <c>queue.json</c>. It contains only pending tasks: a task is
/// removed once the SMTP server accepts the message, so there is no history and
/// no dead-letter list (architecture.md §6, §11).
/// </summary>
public sealed class QueueDocument
{
    /// <summary>Schema version of this document.</summary>
    public int SchemaVersion { get; set; } = AppConstants.SchemaVersion;

    /// <summary>
    /// Pending tasks. Processed oldest first by <see cref="PendingMailEvent.OccurredAtUtc"/>
    /// (FR-06).
    /// </summary>
    public List<PendingMailEvent> Items { get; set; } = [];
}
