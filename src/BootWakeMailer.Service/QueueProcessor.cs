using BootWakeMailer.Shared;

namespace BootWakeMailer.Service;

/// <summary>
/// Owns <c>queue.json</c> and <c>status.json</c> for the running service: it persists
/// new notification tasks, drains the queue in FIFO order over the shared MailKit
/// sender, and retries at a fixed interval (FR-05, FR-06, architecture.md §11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Persist before send.</b> <see cref="Enqueue"/> writes the task to disk before it
/// raises the processing request, and a processing cycle increments the attempt counter
/// on disk before it touches the network. A task therefore survives a crash, a service
/// stop or a Windows shutdown at any point (FR-05, requirement 14).
/// </para>
/// <para>
/// <b>At least once.</b> A task is removed only after the SMTP server has accepted the
/// message. If the process dies between acceptance and removal, the task is sent again
/// after the next start. That narrow duplicate is accepted by design; there is no
/// exactly-once machinery (architecture.md §11, §14).
/// </para>
/// <para>
/// <b>Concurrency.</b> Every load-mutate-save sequence of both files runs inside one
/// in-process gate that is never held across SMTP I/O, and at most one processing cycle
/// runs at a time. This service is the only writer of these files (requirements.md §5),
/// and each write replaces the file atomically (architecture.md §4).
/// </para>
/// <para>
/// <b>Not in this phase.</b> The Windows Service host, the power-event source and the
/// custom retry command are not implemented here. <see cref="Enqueue"/> is the entry
/// point a Startup or ResumeAutomatic event will call, and
/// <see cref="RequestImmediateProcessing"/> is the entry point command 128 will call.
/// </para>
/// </remarks>
public sealed class QueueProcessor : IDisposable
{
    private readonly AppPaths _paths;
    private readonly IMailSender _mailSender;
    private readonly TimeSpan _retryInterval;
    private readonly string _computerName;

    /// <summary>
    /// Guards every load-mutate-save sequence of <c>queue.json</c> and <c>status.json</c>
    /// inside this process, so two writers cannot lose each other's change. Held only for
    /// the duration of a file operation, never across an SMTP attempt.
    /// </summary>
    private readonly Lock _stateGate = new();

    /// <summary>Allows only one processing cycle at a time (requirement 11).</summary>
    private readonly SemaphoreSlim _cycleGate = new(1, 1);

    /// <summary>
    /// Cuts the retry wait short when new work arrives. Bounded to one pending request,
    /// because the loop only needs to know that it should run again.
    /// </summary>
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    /// <summary>
    /// Tasks whose message the SMTP server already accepted but whose removal from
    /// <c>queue.json</c> could not be written at the time, for example because the disk was
    /// full. They stay on disk, but this process never sends them again; only the removal
    /// is retried. Guarded by <see cref="_stateGate"/>.
    /// </summary>
    private readonly HashSet<string> _acceptedWithoutRemoval = [];

    private bool _disposed;

    /// <param name="paths">Application data directory holding the three JSON files.</param>
    /// <param name="mailSender">The shared MailKit sender, or a test double.</param>
    /// <param name="retryInterval">
    /// Automatic retry interval; defaults to the fixed
    /// <see cref="AppConstants.RetryIntervalSeconds"/> (FR-06). Tests pass a short value.
    /// </param>
    /// <param name="computerName">
    /// Computer name recorded in notifications; defaults to <see cref="Environment.MachineName"/>.
    /// </param>
    public QueueProcessor(
        AppPaths paths,
        IMailSender mailSender,
        TimeSpan? retryInterval = null,
        string? computerName = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _mailSender = mailSender ?? throw new ArgumentNullException(nameof(mailSender));
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(AppConstants.RetryIntervalSeconds);

        if (_retryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryInterval), retryInterval, "The retry interval must be positive.");
        }

        _computerName = string.IsNullOrWhiteSpace(computerName) ? Environment.MachineName : computerName;
    }

    /// <summary>Automatic retry interval used by <see cref="RunAsync"/>.</summary>
    public TimeSpan RetryInterval => _retryInterval;

    /// <summary>
    /// Creates a Startup or ResumeAutomatic task, writes it to <c>queue.json</c> and only
    /// then requests queue processing (FR-05, architecture.md §8).
    /// </summary>
    /// <param name="eventType">The trigger that produced this task.</param>
    /// <param name="occurredAtUtc">Event time; defaults to the current UTC time.</param>
    /// <returns>The persisted task.</returns>
    /// <exception cref="InvalidDataException">
    /// <c>queue.json</c> exists but is corrupt. It is not replaced with an empty queue, so
    /// the caller sees the failure instead of losing the tasks already on disk
    /// (architecture.md §14.5). No notification is sent in that case.
    /// </exception>
    /// <exception cref="IOException">
    /// The data directory cannot be written. The task is not persisted, so nothing is sent
    /// and the caller reports the failure instead of losing the event silently.
    /// </exception>
    public PendingMailEvent Enqueue(MailEventType eventType, DateTime? occurredAtUtc = null)
    {
        ThrowIfDisposed();

        var item = PendingMailEvent.Create(eventType, _computerName, occurredAtUtc);

        try
        {
            lock (_stateGate)
            {
                var queue = QueueStore.Load(_paths.QueueFilePath);
                queue.Items.Add(item);
                SaveQueue(queue);
            }
        }
        catch (Exception exception)
        {
            // The event could not be made durable, so it is reported rather than sent or
            // dropped in silence (architecture.md §14.3). The caller sees the exception.
            RecordStatusError(ErrorText.Operations.QueueFile, exception);
            throw;
        }

        // Raised after the file write returned, so a task is never sent before it is durable.
        RequestImmediateProcessing();

        return item;
    }

    /// <summary>
    /// Asks the running <see cref="RunAsync"/> loop to process the queue now instead of
    /// waiting for the next retry tick.
    /// </summary>
    /// <remarks>
    /// Safe to call from any thread and at any time; requests that arrive while a cycle
    /// runs are honoured by the cycle that follows, and only one request is remembered.
    /// </remarks>
    public void RequestImmediateProcessing()
    {
        ThrowIfDisposed();

        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A request is already pending; the loop will run again either way.
        }
    }

    /// <summary>
    /// Runs one processing cycle: sends pending tasks oldest first until the queue is
    /// empty or a task fails (architecture.md §11).
    /// </summary>
    /// <returns>
    /// <see cref="QueueCycleOutcome.AlreadyRunning"/> when another cycle already owns the
    /// queue. A cycle never throws for an SMTP, configuration, file or network error; the
    /// error is recorded and reported through the result.
    /// </returns>
    public async Task<QueueCycleResult> ProcessOnceAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Requirement 11: the cycle that is already running owns the queue, so a second
        // request has nothing useful to do and must not race it.
        if (!_cycleGate.Wait(0))
        {
            return QueueCycleResult.AlreadyRunning;
        }

        try
        {
            return await RunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// Processes the queue immediately, then retries on a fixed <see cref="RetryInterval"/>
    /// cadence until <paramref name="cancellationToken"/> is cancelled (FR-06).
    /// </summary>
    /// <remarks>
    /// This is the retry loop. It returns instead of throwing when the token is
    /// cancelled, so a stopping service ends the loop without an exception
    /// (architecture.md §14.1).
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var nextTickUtc = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            // The loop waits for the queue instead of skipping a cycle, so a request that
            // woke it is never dropped because someone else held the gate at that instant.
            await ProcessCycleAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            nextTickUtc += _retryInterval;

            if (nextTickUtc <= now)
            {
                // The cycle outlasted its own tick. Wait a whole interval from here rather
                // than starting the next attempt immediately, so the retry cadence never
                // turns into back-to-back attempts.
                nextTickUtc = now + _retryInterval;
            }

            await WaitUntilTickAsync(nextTickUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the synchronization primitives.
    /// </summary>
    /// <remarks>
    /// Call this only once the retry loop has ended; disposing while
    /// <see cref="RunAsync"/> is still running would fault that loop.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cycleGate.Dispose();
        _wakeSignal.Dispose();
    }

    /// <summary>
    /// Runs one cycle, waiting for the queue when another cycle already owns it.
    /// </summary>
    /// <remarks>
    /// Used by the retry loop, which must not lose a cycle: unlike
    /// <see cref="ProcessOnceAsync"/> it queues up behind a running cycle instead of
    /// returning <see cref="QueueCycleOutcome.AlreadyRunning"/>.
    /// </remarks>
    private async Task<QueueCycleResult> ProcessCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The service is stopping while another cycle still owns the queue.
            return QueueCycleResult.Canceled(0);
        }

        try
        {
            return await RunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    /// <summary>
    /// Sends pending tasks in FIFO order until the queue is empty, a task fails, or the
    /// service stops.
    /// </summary>
    private async Task<QueueCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        var sentCount = 0;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return QueueCycleResult.Canceled(sentCount);
            }

            PendingMailEvent attempt;

            try
            {
                var next = BeginOldestAttempt();

                if (next is null)
                {
                    return QueueCycleResult.Completed(sentCount);
                }

                attempt = next;
            }
            catch (Exception exception)
            {
                // A queue that cannot be read or written is never silently replaced by an
                // empty one (architecture.md §14.5); the tasks on disk stay untouched and
                // the next cycle tries again.
                RecordStatusError(ErrorText.Operations.QueueFile, exception);
                return QueueCycleResult.Failed(sentCount, ErrorText.Describe(exception));
            }

            // The state gate is not held here, so a Startup event arriving during the SMTP
            // attempt is persisted immediately instead of waiting for the network.
            try
            {
                await SendAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The service is stopping. The attempt counter was already persisted, and
                // the task stays pending until the next service start (requirement 14).
                return QueueCycleResult.Canceled(sentCount);
            }
            catch (Exception exception)
            {
                // Failure keeps the task queued and does not touch the tasks behind it
                // (FR-05, requirement 13).
                TryRecordAttemptFailure(attempt.Id, exception);
                RecordStatusError(ErrorText.Operations.SmtpSend, exception);
                return QueueCycleResult.Failed(sentCount, ErrorText.Describe(exception));
            }

            // The SMTP server accepted the message, so the task is done (FR-07).
            try
            {
                if (!RemoveItem(attempt.Id))
                {
                    // Cannot happen while this service is the only writer, but looping
                    // would resend the same message forever, so stop instead.
                    var error = new InvalidDataException(
                        $"Queue item '{attempt.Id}' disappeared before it could be removed.");
                    RecordStatusError(ErrorText.Operations.QueueFile, error);
                    return QueueCycleResult.Failed(sentCount + 1, ErrorText.Describe(error));
                }
            }
            catch (Exception exception)
            {
                // The message was accepted but cannot be removed. The task stays on disk
                // and only its removal is retried, so a queue file that cannot be written
                // does not mail the same notification once per cycle (architecture.md §11).
                // Stopping also avoids a resend loop inside this cycle.
                RememberAcceptedWithoutRemoval(attempt.Id);
                RecordStatusError(ErrorText.Operations.QueueFile, exception);
                return QueueCycleResult.Failed(sentCount + 1, ErrorText.Describe(exception));
            }

            sentCount++;
            RecordSuccessfulSend();
        }
    }

    /// <summary>
    /// Loads the configuration for this attempt and hands the notification to the sender.
    /// </summary>
    private async Task SendAttemptAsync(PendingMailEvent attempt, CancellationToken cancellationToken)
    {
        // Reading the configuration per attempt means a pending task automatically uses
        // the settings saved most recently (architecture.md §5, §12).
        AppConfig? config;

        try
        {
            config = ConfigStore.Load(_paths.ConfigFilePath);
        }
        catch (InvalidDataException exception)
        {
            throw new MailConfigurationException(
                $"'{AppPaths.ConfigFileName}' cannot be read: {ErrorText.Describe(exception)}",
                exception);
        }

        var problems = ConfigValidator.Validate(config);

        if (problems.Count > 0)
        {
            // Missing or invalid configuration is a send failure, never a reason to drop
            // the task (architecture.md §14.6).
            throw new MailConfigurationException(string.Join(" ", problems));
        }

        var message = MailMessageFactory.CreateNotification(config!, attempt);

        await _mailSender.SendAsync(config!, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Selects the oldest pending task, increments its attempt counter and persists that
    /// change before the SMTP attempt starts (architecture.md §11.4).
    /// </summary>
    /// <returns>The task to attempt, or <c>null</c> when nothing is left to attempt.</returns>
    private PendingMailEvent? BeginOldestAttempt()
    {
        lock (_stateGate)
        {
            var queue = LoadQueue();
            CompleteAcceptedRemovals(queue);

            // OrderBy is a stable sort, so tasks created at the same instant keep their
            // insertion order and the queue stays FIFO (FR-06). Tasks whose message was
            // already accepted are skipped: their send is done, only their removal is still
            // pending.
            var oldest = queue.Items
                .Where(item => !_acceptedWithoutRemoval.Contains(item.Id))
                .OrderBy(item => item.OccurredAtUtc)
                .FirstOrDefault();

            if (oldest is null)
            {
                return null;
            }

            oldest.AttemptCount++;
            oldest.LastAttemptAtUtc = DateTime.UtcNow;
            SaveQueue(queue);

            return oldest;
        }
    }

    /// <summary>
    /// Removes the tasks whose message the SMTP server already accepted but whose removal
    /// could not be written when it was accepted.
    /// </summary>
    /// <remarks>
    /// Retrying the send instead would mail the same notification once per retry cycle for
    /// as long as the queue file stays unwritable. architecture.md §11 allows a single
    /// duplicate after a crash, not a duplicate per cycle, so the send is never repeated
    /// while this process lives; only the bookkeeping is.
    /// </remarks>
    private void CompleteAcceptedRemovals(QueueDocument queue)
    {
        if (_acceptedWithoutRemoval.Count == 0)
        {
            return;
        }

        if (queue.Items.RemoveAll(item => _acceptedWithoutRemoval.Contains(item.Id)) == 0)
        {
            return;
        }

        SaveQueue(queue);

        // Forgotten only once the removal is on disk.
        _acceptedWithoutRemoval.RemoveWhere(id => queue.Items.All(item => item.Id != id));
    }

    /// <summary>
    /// Removes an accepted task from <c>queue.json</c> (FR-05, requirement 8).
    /// </summary>
    /// <returns><c>false</c> when the task was no longer present.</returns>
    private bool RemoveItem(string id)
    {
        lock (_stateGate)
        {
            var queue = LoadQueue();
            var removed = queue.Items.RemoveAll(item => item.Id == id) > 0;

            if (removed)
            {
                SaveQueue(queue);

                // The accepted message is gone; it must never be treated as pending again.
                _acceptedWithoutRemoval.Remove(id);
            }

            return removed;
        }
    }

    /// <summary>
    /// Remembers that a message the server accepted still has to be removed from the queue.
    /// </summary>
    private void RememberAcceptedWithoutRemoval(string id)
    {
        lock (_stateGate)
        {
            _acceptedWithoutRemoval.Add(id);
        }
    }

    /// <summary>
    /// Records the failure on the task itself, so the retry keeps the same error
    /// (architecture.md §14.4).
    /// </summary>
    private void TryRecordAttemptFailure(string id, Exception exception)
    {
        try
        {
            lock (_stateGate)
            {
                var queue = LoadQueue();
                var item = queue.Items.FirstOrDefault(candidate => candidate.Id == id);

                if (item is null)
                {
                    return;
                }

                item.LastError = ErrorText.Describe(exception);
                SaveQueue(queue);
            }
        }
        catch (Exception)
        {
            // The status document carries the error instead. A failure to annotate the
            // task must not break the cycle: the task itself is still pending on disk.
        }
    }

    /// <summary>
    /// Sets the latest successful send time. A later success never clears the previous
    /// error (architecture.md §7).
    /// </summary>
    private void RecordSuccessfulSend()
    {
        lock (_stateGate)
        {
            try
            {
                var status = LoadStatus();
                status.LastSuccessfulSendAtUtc = DateTime.UtcNow;
                StatusStore.Save(_paths.StatusFilePath, status);
            }
            catch (Exception)
            {
                // The server already accepted the message and queue.json no longer lists
                // it. A status write failure must not turn that into a pending task
                // (architecture.md §10.7, §14.9). Status is advisory.
            }
        }
    }

    /// <summary>
    /// Records the newest operational error in <c>status.json</c> (architecture.md §14.3).
    /// </summary>
    private void RecordStatusError(string operation, Exception exception)
    {
        lock (_stateGate)
        {
            try
            {
                var status = LoadStatus();
                status.LastError = ErrorText.ToStatusError(operation, exception);
                StatusStore.Save(_paths.StatusFilePath, status);
            }
            catch (Exception)
            {
                // Status is advisory; failing to record an error must not break the cycle
                // or terminate the service (architecture.md §14.1).
            }
        }
    }

    /// <summary>
    /// Loads <c>status.json</c>, tolerating an unreadable file.
    /// </summary>
    /// <remarks>
    /// The "never silently replace corrupt state" rule covers <c>queue.json</c>, where a
    /// lost file means lost notifications (architecture.md §14.5). Status is derived
    /// information, so an unreadable status file is rebuilt instead of blocking the queue.
    /// </remarks>
    private StatusDocument LoadStatus()
    {
        try
        {
            return StatusStore.Load(_paths.StatusFilePath);
        }
        catch (InvalidDataException)
        {
            return new StatusDocument();
        }
    }

    private void SaveQueue(QueueDocument queue)
    {
        // Writing the whole document atomically is what makes an interrupted write safe
        // (requirements.md §5).
        QueueStore.Save(_paths.QueueFilePath, queue);
    }

    /// <summary>
    /// Loads <c>queue.json</c> for this process, dropping <c>null</c> entries.
    /// </summary>
    /// <remarks>
    /// A null entry can only come from hand-editing, and it describes no notification at
    /// all. Skipping it keeps every real task flowing. All other damage is still reported
    /// rather than repaired (architecture.md §14.5).
    /// </remarks>
    private QueueDocument LoadQueue()
    {
        var queue = QueueStore.Load(_paths.QueueFilePath);
        queue.Items.RemoveAll(item => item is null);
        return queue;
    }

    /// <summary>
    /// Waits until <paramref name="tickUtc"/>, or returns early when new work was requested.
    /// </summary>
    private async Task WaitUntilTickAsync(DateTime tickUtc, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var remaining = tickUtc - DateTime.UtcNow;
        var retryDelay = remaining > TimeSpan.Zero
            ? Task.Delay(remaining, linked.Token)
            : Task.CompletedTask;
        var wakeRequest = _wakeSignal.WaitAsync(linked.Token);

        await Task.WhenAny(retryDelay, wakeRequest).ConfigureAwait(false);

        // Cancel the loser. Without this, a stale delay or semaphore waiter would survive
        // this call and swallow the next immediate-processing request.
        linked.Cancel();
        await IgnoreCancellationAsync(retryDelay).ConfigureAwait(false);
        await IgnoreCancellationAsync(wakeRequest).ConfigureAwait(false);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
