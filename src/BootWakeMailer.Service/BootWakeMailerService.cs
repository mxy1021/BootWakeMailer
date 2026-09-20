using System.ServiceProcess;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Service;

/// <summary>
/// The BootWakeMailer Windows Service (architecture.md §3.2).
/// </summary>
/// <remarks>
/// <para>
/// Event capture is intentionally trivial: every service start is one Startup event
/// (FR-01) and every <see cref="PowerBroadcastStatus.ResumeAutomatic"/> notification is
/// one resume event (FR-02). Both are persisted to the durable queue before any SMTP
/// work happens (FR-05).
/// </para>
/// <para>
/// No callback on this class performs SMTP. <c>OnStart</c>, <c>OnPowerEvent</c> and
/// <c>OnCustomCommand</c> only record an event or wake the background worker, so none of
/// them can block on network I/O (architecture.md §8.6, §9.5, §12).
/// </para>
/// <para>
/// There is no retry timer here: <see cref="QueueProcessor.RunAsync"/> owns both the
/// 60-second cadence and the wake signal that cuts a wait short (FR-06), so this class is
/// only the SCM-facing shell around the background worker.
/// </para>
/// </remarks>
public sealed class BootWakeMailerService : ServiceBase
{
    /// <summary>How often pending tasks are retried (FR-06).</summary>
    public static TimeSpan RetryInterval => TimeSpan.FromSeconds(AppConstants.RetryIntervalSeconds);

    /// <summary>Upper bound <c>OnStop</c> waits for an in-flight send to unwind.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly QueueProcessor _processor;
    private readonly CancellationTokenSource _stopping = new();

    private Task? _worker;
    private int _stopStarted;

    /// <param name="paths">Application data location holding the three JSON files.</param>
    /// <param name="mailSender">SMTP implementation used by the background worker.</param>
    public BootWakeMailerService(AppPaths paths, IMailSender mailSender)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mailSender);

        _processor = new QueueProcessor(paths, mailSender);

        ServiceName = AppConstants.ServiceName;

        // Power notifications are the only way a resume is observed (FR-02, FR-05).
        CanHandlePowerEvent = true;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        CanHandleSessionChangeEvent = false;
    }

    /// <summary>
    /// Starts the background worker and records the Startup event (architecture.md §8).
    /// </summary>
    /// <remarks>
    /// There is no boot-versus-restart detection: a manual restart produces a Startup
    /// event exactly like a real Windows boot (FR-01).
    /// </remarks>
    protected override void OnStart(string[] args)
    {
        try
        {
            // Started before the first event is recorded, so a queue file that cannot be
            // written still leaves a running worker to drain whatever is already on disk.
            // The loop's first cycle is immediate, so it also picks up anything a previous
            // run left in queue.json (FR-05).
            _worker = Task.Run(RunWorkerAsync);

            // Persists the task and wakes the worker. A failure here is recorded in
            // status.json by Enqueue itself and never escapes (architecture.md §14.1).
            _processor.Enqueue(MailEventType.Startup);
        }
        catch (Exception)
        {
            // A failing OnStart must not take the process down (architecture.md §14.1),
            // and the cause is already in status.json (§14.3).
        }
    }

    /// <summary>
    /// Records a resume notification for <see cref="PowerBroadcastStatus.ResumeAutomatic"/>
    /// and ignores every other status (FR-02, architecture.md §9).
    /// </summary>
    /// <returns>
    /// Always <c>true</c>. Returning <c>false</c> for a query or suspend notification would
    /// veto the power transition, which is not this service's decision; the MVP only
    /// decides which statuses create a notification.
    /// </returns>
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            if (powerStatus == PowerBroadcastStatus.ResumeAutomatic)
            {
                // Records the task and wakes the worker. No SMTP happens on this thread
                // (architecture.md §9.3).
                _processor.Enqueue(MailEventType.ResumeAutomatic);
            }
        }
        catch (Exception)
        {
            // Already recorded in status.json by Enqueue. A power callback must still
            // answer, so the failure is reported there rather than thrown (architecture.md §14.1).
        }

        return true;
    }

    /// <summary>
    /// Handles the immediate-retry command and ignores all other custom commands
    /// (architecture.md §12).
    /// </summary>
    protected override void OnCustomCommand(int command)
    {
        try
        {
            if (command == AppConstants.ImmediateRetryCommand)
            {
                // Only wakes the worker; the SMTP work is done by the background loop
                // (architecture.md §12).
                _processor.RequestImmediateProcessing();
            }
        }
        catch (Exception)
        {
            // The SCM callback must not throw; the loop retries on its own cadence
            // regardless (architecture.md §14.1).
        }
    }

    protected override void OnStop() => StopProcessing();

    protected override void OnShutdown() => StopProcessing();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _processor.Dispose();
            _stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Runs the retry loop for the lifetime of the service.
    /// </summary>
    /// <remarks>
    /// <see cref="QueueProcessor.RunAsync"/> already records every SMTP, configuration and
    /// file error in status.json, and returns instead of throwing when the token is
    /// cancelled (architecture.md §14.1). Only a genuinely unexpected fault reaches the
    /// handler below, and that is swallowed for the same reason: a background thread must
    /// not take the process down. The fault itself is already in status.json, so the
    /// configuration tool shows that processing stopped instead of the failure being
    /// invisible (architecture.md §14.3).
    /// </remarks>
    private async Task RunWorkerAsync()
    {
        try
        {
            await _processor.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal stop: the token fired while a cycle was waiting for the queue.
        }
        catch (Exception)
        {
            // Recorded by RunAsync before it rethrew; swallowing here only protects the
            // process from a background-thread fault (architecture.md §14.1).
        }
    }

    /// <summary>
    /// Cancels the worker token and waits briefly for an in-flight send to unwind.
    /// </summary>
    private void StopProcessing()
    {
        // OnStop and OnShutdown can both arrive for the same shutdown.
        if (Interlocked.Exchange(ref _stopStarted, 1) == 1)
        {
            return;
        }

        try
        {
            _stopping.Cancel();

            // Bounded: the SCM waits for OnStop to return, and queue writes are atomic, so
            // an unfinished attempt can safely be left for the next service start.
            _worker?.Wait(StopTimeout);
        }
        catch (Exception)
        {
            // The attempt counter is already on disk, so the pending task survives to the
            // next start (FR-05).
        }
    }
}
