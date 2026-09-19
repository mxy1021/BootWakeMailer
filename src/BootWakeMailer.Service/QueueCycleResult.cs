namespace BootWakeMailer.Service;

/// <summary>How one queue-processing cycle ended.</summary>
public enum QueueCycleOutcome
{
    /// <summary>No pending task is left; every task found by this cycle was accepted by SMTP.</summary>
    Completed,

    /// <summary>The cycle stopped after a failure; the failed task is still pending.</summary>
    Failed,

    /// <summary>The cancellation token fired, normally because the service is stopping.</summary>
    Canceled,

    /// <summary>Another cycle already owns the queue, so this request did nothing.</summary>
    AlreadyRunning,
}

/// <summary>
/// Outcome of a single <see cref="QueueProcessor.ProcessOnceAsync"/> call, for logging
/// and for tests.
/// </summary>
/// <param name="Outcome">How the cycle ended.</param>
/// <param name="SentCount">Number of tasks the SMTP server accepted during this cycle.</param>
/// <param name="Error">Short failure text when <see cref="Outcome"/> is Failed.</param>
public sealed record QueueCycleResult(QueueCycleOutcome Outcome, int SentCount, string? Error)
{
    /// <summary>Every pending task was accepted and removed.</summary>
    public static QueueCycleResult Completed(int sentCount) =>
        new(QueueCycleOutcome.Completed, sentCount, null);

    /// <summary>The cycle stopped on a failure that left the task pending.</summary>
    public static QueueCycleResult Failed(int sentCount, string error) =>
        new(QueueCycleOutcome.Failed, sentCount, error);

    /// <summary>The cycle stopped because the service is stopping.</summary>
    public static QueueCycleResult Canceled(int sentCount) =>
        new(QueueCycleOutcome.Canceled, sentCount, null);

    /// <summary>No work was done because another cycle was already running.</summary>
    public static QueueCycleResult AlreadyRunning { get; } =
        new(QueueCycleOutcome.AlreadyRunning, 0, null);
}
