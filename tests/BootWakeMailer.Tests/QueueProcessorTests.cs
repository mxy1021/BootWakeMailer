using System.Diagnostics;
using System.Text.Json;
using BootWakeMailer.Service;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// The durable queue: a task is persisted before it is sent, removed only after the SMTP
/// server accepts it, retried in FIFO order on a fixed interval, and never lost by a
/// failure, a concurrent event or a service restart (FR-05, FR-06).
/// </summary>
public class QueueProcessorTests
{
    private const string ComputerName = "ZEN-PC";

    private static readonly DateTime T0 = new(2026, 9, 19, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddMinutes(1);
    private static readonly DateTime T2 = T0.AddMinutes(2);

    /// <summary>A processor plus the state a test needs to inspect it.</summary>
    private sealed class Context : IDisposable
    {
        public required QueueProcessor Processor { get; init; }

        public required FakeMailSender Sender { get; init; }

        public required AppPaths Paths { get; init; }

        public void Dispose() => Processor.Dispose();
    }

    private static Context Create(TempDirectory temp, FakeMailSender? sender = null, TimeSpan? retryInterval = null)
    {
        var paths = new AppPaths(temp.Root);
        var mailSender = sender ?? new FakeMailSender();

        return new Context
        {
            Processor = new QueueProcessor(paths, mailSender, retryInterval, ComputerName),
            Sender = mailSender,
            Paths = paths,
        };
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static QueueDocument Queue(AppPaths paths) => QueueStore.Load(paths.QueueFilePath);

    // ---------------------------------------------------------------- enqueue before send

    [Fact]
    public void Enqueue_WritesTheTaskToTheQueueFileWithoutSendingAnything()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        var item = context.Processor.Enqueue(MailEventType.Startup, T0);

        var stored = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal(item.Id, stored.Id);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.LastAttemptAtUtc);
        Assert.Equal(0, context.Sender.AttemptCount);
    }

    [Fact]
    public void Enqueue_RecordsTheEventTypeTheComputerAndTheOccurrenceTime()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        context.Processor.Enqueue(MailEventType.ResumeAutomatic, T1);

        var stored = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal(MailEventType.ResumeAutomatic, stored.EventType);
        Assert.Equal(ComputerName, stored.ComputerName);
        Assert.Equal(T1, stored.OccurredAtUtc);
        Assert.Equal(AppConstants.SchemaVersion, Queue(context.Paths).SchemaVersion);
    }

    [Fact]
    public void Enqueue_ThrowsAndKeepsAnExistingCorruptQueueInsteadOfReplacingIt()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        const string damaged = "{ \"items\": [";
        File.WriteAllText(context.Paths.QueueFilePath, damaged);

        Assert.Throws<InvalidDataException>(() => context.Processor.Enqueue(MailEventType.Startup, T0));

        // A damaged queue must never be silently discarded (architecture.md §14.5).
        Assert.Equal(damaged, File.ReadAllText(context.Paths.QueueFilePath));
        Assert.Equal(0, context.Sender.AttemptCount);

        // The failed event is visible in status.json instead of vanishing (architecture.md §14.3).
        var error = Assert.IsType<StatusError>(StatusStore.Load(context.Paths.StatusFilePath).LastError);
        Assert.Equal("QueueFile", error.Operation);
        Assert.Equal("InvalidDataException", error.Type);
    }

    // ------------------------------------------------------------------------- success

    [Fact]
    public async Task ProcessOnceAsync_RemovesAnAcceptedTaskAndRecordsTheSuccessfulSendTime()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Processor.Enqueue(MailEventType.Startup, T0);
        var before = DateTime.UtcNow;

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.SentCount);
        Assert.Empty(Queue(context.Paths).Items);
        Assert.Equal("BootWakeMailer: Startup - ZEN-PC", Assert.Single(context.Sender.AcceptedSubjects));

        var status = StatusStore.Load(context.Paths.StatusFilePath);
        Assert.NotNull(status.LastSuccessfulSendAtUtc);
        Assert.InRange(status.LastSuccessfulSendAtUtc!.Value, before, DateTime.UtcNow);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task ProcessOnceAsync_DoesNothingWhenTheQueueIsEmpty()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.SentCount);
        Assert.Equal(0, context.Sender.AttemptCount);
        Assert.False(File.Exists(context.Paths.QueueFilePath));
    }

    [Fact]
    public async Task ProcessOnceAsync_PersistsTheAttemptBeforeTheSendStarts()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        var hold = Gate();
        context.Sender.HoldFirstSend = hold;
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var cycle = context.Processor.ProcessOnceAsync(CancellationToken.None);
        await context.Sender.SendStarted.Task;

        // While the SMTP attempt is still running, the task is on disk with its attempt
        // counter already advanced (architecture.md §11.4).
        var inFlight = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal(1, inFlight.AttemptCount);
        Assert.NotNull(inFlight.LastAttemptAtUtc);

        hold.SetResult();
        Assert.Equal(QueueCycleOutcome.Completed, (await cycle).Outcome);
    }

    [Fact]
    public async Task ProcessOnceAsync_SendsPendingTasksInFifoOrder()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());

        // Enqueued out of order; the queue must still drain oldest first (FR-06).
        context.Processor.Enqueue(MailEventType.Startup, T2);
        context.Processor.Enqueue(MailEventType.ResumeAutomatic, T0);
        context.Processor.Enqueue(MailEventType.Startup, T1);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(3, result.SentCount);
        Assert.Equal(
            [T0, T1, T2],
            context.Sender.Accepted.Select(MailBody.OccurredAtUtc).ToArray());
    }

    [Fact]
    public async Task ProcessOnceAsync_KeepsInsertionOrderForTasksRecordedAtTheSameInstant()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());

        context.Processor.Enqueue(MailEventType.Startup, T0);
        context.Processor.Enqueue(MailEventType.ResumeAutomatic, T0);

        await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(
            ["BootWakeMailer: Startup - ZEN-PC", "BootWakeMailer: ResumeAutomatic - ZEN-PC"],
            context.Sender.AcceptedSubjects);
    }

    [Fact]
    public async Task ProcessOnceAsync_ReadsTheConfigurationAgainForEveryAttempt()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        context.Sender.FailureFactory = FakeMailSender.FailsThenSucceeds(1, () => new IOException("offline"));
        TestConfig.Write(context.Paths, TestConfig.Create(toAddress: "first@example.com"));
        context.Processor.Enqueue(MailEventType.Startup, T0);

        await context.Processor.ProcessOnceAsync(CancellationToken.None);

        // The configuration saved while the task was pending is used by the next attempt
        // (architecture.md §5, §12).
        TestConfig.Write(context.Paths, TestConfig.Create(toAddress: "second@example.com"));
        await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(2, context.Sender.Configs.Count);
        Assert.Equal("first@example.com", context.Sender.Configs[0].ToAddress);
        Assert.Equal("second@example.com", context.Sender.Configs[1].ToAddress);
        Assert.Equal("second@example.com", Assert.Single(context.Sender.Accepted).To[0].ToString());
    }

    // ------------------------------------------------------------------------- failure

    [Fact]
    public async Task ProcessOnceAsync_KeepsAFailedTaskQueuedAndRecordsTheErrorOnTheTask()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("SMTP connection failed"));
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.SentCount);
        Assert.Equal("SMTP connection failed", result.Error);

        // The task is not deleted (FR-05).
        var stored = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal(1, stored.AttemptCount);
        Assert.NotNull(stored.LastAttemptAtUtc);
        Assert.Equal("SMTP connection failed", stored.LastError);
        Assert.Empty(context.Sender.Accepted);
    }

    [Fact]
    public async Task ProcessOnceAsync_RecordsTheNewestErrorInTheStatusFile()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("SMTP connection failed"));
        context.Processor.Enqueue(MailEventType.Startup, T0);

        await context.Processor.ProcessOnceAsync(CancellationToken.None);

        var status = StatusStore.Load(context.Paths.StatusFilePath);
        var error = Assert.IsType<StatusError>(status.LastError);
        Assert.Equal("SmtpSend", error.Operation);
        Assert.Equal("IOException", error.Type);
        Assert.Equal("SMTP connection failed", error.Message);
        Assert.Null(status.LastSuccessfulSendAtUtc);
    }

    [Fact]
    public async Task ProcessOnceAsync_RetriesAFailedTaskOnTheNextCycle()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.FailsThenSucceeds(1, () => new IOException("offline"));
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var first = await context.Processor.ProcessOnceAsync(CancellationToken.None);
        var second = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Failed, first.Outcome);
        Assert.Equal(QueueCycleOutcome.Completed, second.Outcome);
        Assert.Equal(2, context.Sender.AttemptCount);
        Assert.Empty(Queue(context.Paths).Items);
    }

    [Fact]
    public async Task ProcessOnceAsync_DoesNotLoseTheTasksBehindAFailure()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("offline"));
        context.Processor.Enqueue(MailEventType.Startup, T0);
        context.Processor.Enqueue(MailEventType.ResumeAutomatic, T1);
        context.Processor.Enqueue(MailEventType.Startup, T2);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        // The cycle stops on the first failure (architecture.md §11.8), but the tasks
        // behind it are untouched and still pending (FR-05, architecture.md §11.7).
        Assert.Equal(QueueCycleOutcome.Failed, result.Outcome);
        Assert.Equal(1, context.Sender.AttemptCount);

        var items = Queue(context.Paths).Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items.Single(item => item.OccurredAtUtc == T0).AttemptCount);
        Assert.All(items.Where(item => item.OccurredAtUtc != T0), item => Assert.Equal(0, item.AttemptCount));
    }

    [Fact]
    public async Task ProcessOnceAsync_KeepsTheEarlierErrorAfterALaterSuccess()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.FailsThenSucceeds(1, () => new IOException("offline"));
        context.Processor.Enqueue(MailEventType.Startup, T0);

        await context.Processor.ProcessOnceAsync(CancellationToken.None);
        await context.Processor.ProcessOnceAsync(CancellationToken.None);

        // A later success does not erase the error (architecture.md §7).
        var status = StatusStore.Load(context.Paths.StatusFilePath);
        Assert.NotNull(status.LastSuccessfulSendAtUtc);
        Assert.Equal("offline", status.LastError!.Message);
    }

    [Fact]
    public async Task ProcessOnceAsync_ReportsAMissingConfigurationAndNeverCallsTheSender()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Failed, result.Outcome);
        Assert.Equal(0, context.Sender.AttemptCount);

        var stored = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal("Configuration file is missing or unreadable.", stored.LastError);

        var error = Assert.IsType<StatusError>(StatusStore.Load(context.Paths.StatusFilePath).LastError);
        Assert.Equal("MailConfigurationException", error.Type);
        Assert.Equal("SmtpSend", error.Operation);
    }

    [Fact]
    public async Task ProcessOnceAsync_ReportsACorruptConfigurationAndKeepsTheTask()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        File.WriteAllText(context.Paths.ConfigFilePath, "not json at all");
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Failed, result.Outcome);
        Assert.Equal(0, context.Sender.AttemptCount);

        var stored = Assert.Single(Queue(context.Paths).Items);
        Assert.Contains("cannot be read", stored.LastError, StringComparison.Ordinal);
        Assert.Equal("MailConfigurationException", StatusStore.Load(context.Paths.StatusFilePath).LastError!.Type);
    }

    [Fact]
    public async Task ProcessOnceAsync_ReportsACorruptQueueWithoutOverwritingIt()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        const string damaged = "{ \"items\": [";
        File.WriteAllText(context.Paths.QueueFilePath, damaged);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Failed, result.Outcome);
        Assert.Equal(damaged, File.ReadAllText(context.Paths.QueueFilePath));
        Assert.Equal(0, context.Sender.AttemptCount);
        Assert.Equal("QueueFile", StatusStore.Load(context.Paths.StatusFilePath).LastError!.Operation);
    }

    // --------------------------------------------------------------------- concurrency

    [Fact]
    public async Task ProcessOnceAsync_DoesNotStartASecondCycleWhileOneIsRunning()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        var hold = Gate();
        context.Sender.HoldFirstSend = hold;
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var running = context.Processor.ProcessOnceAsync(CancellationToken.None);
        await context.Sender.SendStarted.Task;

        var concurrent = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.AlreadyRunning, concurrent.Outcome);

        hold.SetResult();
        Assert.Equal(QueueCycleOutcome.Completed, (await running).Outcome);
        Assert.Equal(1, context.Sender.AttemptCount);
    }

    [Fact]
    public async Task Enqueue_DuringAnInFlightSendIsNotLostByTheRemoval()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        var hold = Gate();
        context.Sender.HoldFirstSend = hold;
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var cycle = context.Processor.ProcessOnceAsync(CancellationToken.None);
        await context.Sender.SendStarted.Task;

        // A ResumeAutomatic event arrives while the Startup send is waiting on the server.
        context.Processor.Enqueue(MailEventType.ResumeAutomatic, T1);

        hold.SetResult();
        Assert.Equal(QueueCycleOutcome.Completed, (await cycle).Outcome);

        // Both messages were sent: the later enqueue was not clobbered by the earlier
        // task's removal.
        Assert.Equal(
            ["BootWakeMailer: Startup - ZEN-PC", "BootWakeMailer: ResumeAutomatic - ZEN-PC"],
            context.Sender.AcceptedSubjects);
        Assert.Empty(Queue(context.Paths).Items);
    }

    [Fact]
    public async Task Enqueue_FromManyThreadsWhileTheLoopRunsKeepsEveryTask()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        // Every send fails, so nothing is removed and the queue file must hold every task.
        context.Sender.FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("offline"));

        using var cancellation = new CancellationTokenSource();
        var loop = context.Processor.RunAsync(cancellation.Token);

        const int count = 40;
        var enqueues = Enumerable.Range(0, count)
            .Select(index => Task.Run(() => context.Processor.Enqueue(
                index % 2 == 0 ? MailEventType.Startup : MailEventType.ResumeAutomatic,
                T0.AddSeconds(index))))
            .ToArray();

        await Task.WhenAll(enqueues);
        await cancellation.CancelAsync();
        await loop;

        var items = Queue(context.Paths).Items;
        Assert.Equal(count, items.Count);
        Assert.Equal(count, items.Select(item => item.Id).Distinct().Count());
    }

    // -------------------------------------------------- cancellation and service restart

    [Fact]
    public async Task ProcessOnceAsync_ReportsCancellationAndLeavesTheTaskPending()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        var hold = Gate();
        context.Sender.HoldFirstSend = hold;
        context.Processor.Enqueue(MailEventType.Startup, T0);

        using var cancellation = new CancellationTokenSource();
        var cycle = context.Processor.ProcessOnceAsync(cancellation.Token);
        await context.Sender.SendStarted.Task;

        // The service is stopping while the SMTP attempt is in flight.
        await cancellation.CancelAsync();
        hold.SetResult();

        var result = await cycle;

        // The task is still pending, its attempt counter is on disk, and the shutdown is
        // not recorded as an SMTP error (architecture.md §14.9, FR-05).
        Assert.Equal(QueueCycleOutcome.Canceled, result.Outcome);
        Assert.Equal(0, result.SentCount);
        Assert.Equal(1, Assert.Single(Queue(context.Paths).Items).AttemptCount);
        Assert.Null(Assert.Single(Queue(context.Paths).Items).LastError);

        var status = StatusStore.Load(context.Paths.StatusFilePath);
        Assert.Null(status.LastError);
        Assert.Null(status.LastSuccessfulSendAtUtc);
    }

    [Fact]
    public async Task PendingTasksAndTheirAttemptCountsSurviveAProcessorRestart()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Root);
        TestConfig.Write(paths, TestConfig.Create());

        var firstSender = new FakeMailSender
        {
            FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("offline")),
        };

        using (var stopping = new QueueProcessor(paths, firstSender, retryInterval: null, ComputerName))
        {
            stopping.Enqueue(MailEventType.Startup, T0);
            await stopping.ProcessOnceAsync(CancellationToken.None);

            Assert.Equal(1, Assert.Single(Queue(paths).Items).AttemptCount);
        }

        // The service was stopped and started again, as after a Windows shutdown.
        var hold = Gate();
        var secondSender = new FakeMailSender { HoldFirstSend = hold };

        using var restarted = new QueueProcessor(paths, secondSender, retryInterval: null, ComputerName);
        var cycle = restarted.ProcessOnceAsync(CancellationToken.None);
        await secondSender.SendStarted.Task;

        // The attempt counter written before the shutdown is still there.
        Assert.Equal(2, Assert.Single(Queue(paths).Items).AttemptCount);

        hold.SetResult();
        Assert.Equal(QueueCycleOutcome.Completed, (await cycle).Outcome);
        Assert.Empty(Queue(paths).Items);
        Assert.Single(secondSender.Accepted);
        Assert.NotNull(StatusStore.Load(paths.StatusFilePath).LastSuccessfulSendAtUtc);
    }

    // -------------------------------------------------------------------- retry loop

    [Fact]
    public void RetryInterval_IsSixtySecondsByDefault()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        Assert.Equal(60, AppConstants.RetryIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(60), context.Processor.RetryInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), new QueueProcessor(context.Paths, new FakeMailSender()).RetryInterval);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveRetryInterval()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new QueueProcessor(new AppPaths(temp.Root), new FakeMailSender(), TimeSpan.Zero, ComputerName));
    }

    [Fact]
    public async Task Enqueue_StartsProcessingWithoutWaitingForTheRetryInterval()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp, retryInterval: TimeSpan.FromHours(1));
        TestConfig.Write(context.Paths, TestConfig.Create());

        using var cancellation = new CancellationTokenSource();
        var loop = context.Processor.RunAsync(cancellation.Token);

        // Let the loop finish its first, empty cycle and start waiting for a retry.
        await Task.Delay(250);
        context.Processor.Enqueue(MailEventType.Startup, T0);

        // An hour-long interval means an accepted task this quickly can only come from the
        // immediate-processing request raised by Enqueue.
        await Wait.UntilAsync(() => context.Sender.AttemptCount == 1, TimeSpan.FromSeconds(15));

        await cancellation.CancelAsync();
        await loop;

        Assert.Single(context.Sender.Accepted);
        Assert.Empty(Queue(context.Paths).Items);
    }

    [Fact]
    public async Task RunAsync_RetriesOnAFixedCadenceThatDoesNotSlipByTheCycleDuration()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp, retryInterval: TimeSpan.FromSeconds(1));
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("offline"));
        context.Sender.SendDelay = TimeSpan.FromMilliseconds(700);

        // Placed directly, not through Enqueue, so no immediate-processing request is
        // pending and the measurement sees only the retry cadence.
        QueueStore.Save(
            context.Paths.QueueFilePath,
            new QueueDocument { Items = [PendingMailEvent.Create(MailEventType.Startup, ComputerName, T0)] });

        using var cancellation = new CancellationTokenSource();
        var loop = context.Processor.RunAsync(cancellation.Token);

        try
        {
            // With a 1 s cadence and a 700 ms cycle, the third attempt starts at about
            // 2.0 s. Waiting a whole interval after each cycle would put it at 3.4 s.
            await Wait.UntilAsync(() => context.Sender.AttemptCount >= 3, TimeSpan.FromMilliseconds(2600));
        }
        finally
        {
            await cancellation.CancelAsync();
            await loop;
        }

        Assert.True(context.Sender.AttemptCount >= 3);
    }

    [Fact]
    public async Task RunAsync_KeepsTheRetryScheduleWhenAnImmediateRequestCutsTheWaitShort()
    {
        using var temp = new TempDirectory();
        // A two-second cadence separates the two possible schedules clearly: the third
        // attempt is due one interval after the loop started, while a schedule that also
        // advanced for the interrupted wait would put it two intervals out.
        using var context = Create(temp, retryInterval: TimeSpan.FromSeconds(2));
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.AlwaysFails(() => new IOException("offline"));

        // Placed directly, not through Enqueue, so the first cycle consumes its own tick
        // with no immediate-processing request pending.
        QueueStore.Save(
            context.Paths.QueueFilePath,
            new QueueDocument { Items = [PendingMailEvent.Create(MailEventType.Startup, ComputerName, T0)] });

        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var loop = context.Processor.RunAsync(cancellation.Token);

        try
        {
            await Wait.UntilAsync(() => context.Sender.AttemptCount >= 1);

            // New work arrives while the loop is waiting for its next tick. It must be
            // processed at once, without pushing the tick it interrupted a whole interval
            // further out (FR-06).
            context.Processor.Enqueue(MailEventType.ResumeAutomatic, T1);

            await Wait.UntilAsync(() => context.Sender.AttemptCount >= 3, TimeSpan.FromSeconds(10));
            stopwatch.Stop();
        }
        finally
        {
            await cancellation.CancelAsync();
            await loop;
        }

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"Three attempts took {stopwatch.Elapsed}, so the retry schedule slipped past its tick.");
    }

    [Fact]
    public async Task ProcessOnceAsync_DoesNotSendAnAcceptedTaskAgainWhenItsRemovalFailed()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());
        var hold = Gate();
        context.Sender.HoldFirstSend = hold;
        context.Processor.Enqueue(MailEventType.Startup, T0);

        var cycle = context.Processor.ProcessOnceAsync(CancellationToken.None);
        await context.Sender.SendStarted.Task;

        QueueCycleResult result;

        // Make queue.json unwritable while the message is being accepted, so the removal
        // that follows acceptance has to fail.
        using (new FileStream(context.Paths.QueueFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            hold.SetResult();
            result = await cycle;
        }

        Assert.Equal(QueueCycleOutcome.Failed, result.Outcome);
        Assert.Single(context.Sender.Accepted);
        Assert.Single(Queue(context.Paths).Items);
        Assert.Equal("QueueFile", StatusStore.Load(context.Paths.StatusFilePath).LastError!.Operation);

        // Once the queue file can be written again, the accepted task is removed and the
        // notification is not mailed a second time (architecture.md §11 allows one
        // duplicate after a crash, not one per retry cycle).
        var next = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Completed, next.Outcome);
        Assert.Empty(Queue(context.Paths).Items);
        Assert.Equal(1, context.Sender.AttemptCount);
    }

    [Fact]
    public async Task ProcessOnceAsync_RemovesAnAcceptedTaskWhenTheStatusFileCannotBeUsed()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());

        // A directory where status.json belongs makes every status read and write fail.
        Directory.CreateDirectory(context.Paths.StatusFilePath);
        // The setup must really leave the status document unusable, or this test proves
        // nothing: the read fails before any write is attempted.
        Assert.ThrowsAny<Exception>(() => StatusStore.Load(context.Paths.StatusFilePath));

        context.Processor.Enqueue(MailEventType.Startup, T0);

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        // The SMTP server accepted the message, so the notification is done. Status is
        // advisory: a status file that cannot be used must not turn an accepted notification
        // back into a pending task (architecture.md §10.7, §14.9).
        Assert.Equal(QueueCycleOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.SentCount);
        Assert.Empty(Queue(context.Paths).Items);
        Assert.Single(context.Sender.Accepted);
    }

    [Fact]
    public async Task ProcessOnceAsync_IgnoresANullEntryInTheQueueFile()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);
        TestConfig.Write(context.Paths, TestConfig.Create());

        var item = PendingMailEvent.Create(MailEventType.Startup, ComputerName, T0);

        // A hand-edited queue can contain a null entry. It describes no notification, and it
        // must not stop every other task from being sent.
        File.WriteAllText(
            context.Paths.QueueFilePath,
            JsonSerializer.Serialize(
                new QueueDocument { Items = [null!, item] },
                JsonSettings.SerializerOptions));

        var result = await context.Processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.SentCount);
        Assert.Single(context.Sender.Accepted);
    }

    [Fact]
    public async Task RunAsync_RetriesUntilTheTaskIsAccepted()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp, retryInterval: TimeSpan.FromMilliseconds(50));
        TestConfig.Write(context.Paths, TestConfig.Create());
        context.Sender.FailureFactory = FakeMailSender.FailsThenSucceeds(2, () => new IOException("offline"));
        context.Processor.Enqueue(MailEventType.Startup, T0);

        using var cancellation = new CancellationTokenSource();
        var loop = context.Processor.RunAsync(cancellation.Token);

        await Wait.UntilAsync(() => Queue(context.Paths).Items.Count == 0, TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        await loop;

        // Two failures and then an acceptance, all driven by the retry loop.
        Assert.Equal(3, context.Sender.AttemptCount);
        Assert.Single(context.Sender.Accepted);
        Assert.Equal("offline", StatusStore.Load(context.Paths.StatusFilePath).LastError!.Message);
    }

    [Fact]
    public async Task RunAsync_EndsWithoutThrowingWhenTheServiceStops()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp, retryInterval: TimeSpan.FromMilliseconds(50));

        using var cancellation = new CancellationTokenSource();
        var loop = context.Processor.RunAsync(cancellation.Token);
        await cancellation.CancelAsync();

        // A stopping service must not surface an exception from the retry loop
        // (architecture.md §14.1).
        await loop;
    }

    [Fact]
    public void RequestImmediateProcessing_IsSafeToCallRepeatedly()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        // The pending request is capped at one, so repeated requests must not overflow it.
        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 5; i++)
            {
                context.Processor.RequestImmediateProcessing();
            }
        });

        Assert.Null(exception);
    }
}
