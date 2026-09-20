using System.Reflection;
using System.ServiceProcess;
using BootWakeMailer.Service;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// The Windows Service host: the SCM wiring that can be asserted without a Service
/// Control Manager, and the two event sources the callbacks feed (FR-01, FR-02,
/// architecture.md §12).
/// </summary>
/// <remarks>
/// <see cref="ServiceBase"/> exposes its callbacks as protected members and the SCM is
/// the only production caller, so the tests invoke them through reflection. The power and
/// custom-command cases deliberately never start the worker: with no background loop
/// running, the queue file shows exactly what the callback recorded, with no race.
/// </remarks>
public sealed class BootWakeMailerServiceTests
{
    /// <summary>A host plus the state a test needs to inspect it.</summary>
    private sealed class Context : IDisposable
    {
        public required BootWakeMailerService Service { get; init; }

        public required FakeMailSender Sender { get; init; }

        public required AppPaths Paths { get; init; }

        public void Dispose() => Service.Dispose();
    }

    private static Context Create(TempDirectory temp)
    {
        var paths = new AppPaths(temp.Root);
        var sender = new FakeMailSender();

        return new Context
        {
            Service = new BootWakeMailerService(paths, sender),
            Sender = sender,
            Paths = paths,
        };
    }

    private static QueueDocument Queue(AppPaths paths) => QueueStore.Load(paths.QueueFilePath);

    // ------------------------------------------------------------- SCM wiring

    [Fact]
    public void ServiceName_IsBootWakeMailer()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        Assert.Equal("BootWakeMailer", context.Service.ServiceName);
        Assert.Equal(AppConstants.ServiceName, context.Service.ServiceName);
    }

    [Fact]
    public void PowerEventsAreHandled_AndSessionsAreNot()
    {
        // FR-02 requires CanHandlePowerEvent; session-change events are explicitly out of
        // scope.
        using var temp = new TempDirectory();
        using var context = Create(temp);

        Assert.True(context.Service.CanHandlePowerEvent);
        Assert.True(context.Service.CanStop);
        Assert.True(context.Service.CanShutdown);
        Assert.False(context.Service.CanPauseAndContinue);
        Assert.False(context.Service.CanHandleSessionChangeEvent);
    }

    [Fact]
    public void RetryInterval_IsTheAutomaticRetryInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), BootWakeMailerService.RetryInterval);
        Assert.Equal(
            TimeSpan.FromSeconds(AppConstants.RetryIntervalSeconds),
            BootWakeMailerService.RetryInterval);
    }

    [Fact]
    public void ImmediateRetryCommand_MatchesTheDocumentedValue()
    {
        Assert.Equal(128, AppConstants.ImmediateRetryCommand);
    }

    // ------------------------------------------------------------- power events

    [Fact]
    public void ResumeAutomatic_RecordsOneResumeNotification()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        // Always true: a power callback must not veto the transition (architecture.md §9).
        Assert.True(PowerEvent(context.Service, PowerBroadcastStatus.ResumeAutomatic));

        var item = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal(MailEventType.ResumeAutomatic, item.EventType);
        Assert.Equal(0, item.AttemptCount);
    }

    [Theory]
    [InlineData(PowerBroadcastStatus.QuerySuspend)]
    [InlineData(PowerBroadcastStatus.QuerySuspendFailed)]
    [InlineData(PowerBroadcastStatus.Suspend)]
    [InlineData(PowerBroadcastStatus.ResumeSuspend)]
    [InlineData(PowerBroadcastStatus.ResumeCritical)]
    [InlineData(PowerBroadcastStatus.BatteryLow)]
    [InlineData(PowerBroadcastStatus.PowerStatusChange)]
    [InlineData(PowerBroadcastStatus.OemEvent)]
    public void EveryOtherPowerStatus_RecordsNothing(PowerBroadcastStatus status)
    {
        // Only a resume is a notification (FR-02).
        using var temp = new TempDirectory();
        using var context = Create(temp);

        Assert.True(PowerEvent(context.Service, status));

        Assert.Empty(Queue(context.Paths).Items);
    }

    // -------------------------------------------------------- custom commands

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(129)]
    [InlineData(255)]
    public void CustomCommandsOtherThanTheImmediateRetry_DoNothing(int command)
    {
        // architecture.md §12: only command 128 is this service's.
        using var temp = new TempDirectory();
        using var context = Create(temp);

        CustomCommand(context.Service, command);

        Assert.Empty(Queue(context.Paths).Items);
        Assert.Equal(0, context.Sender.AttemptCount);
    }

    [Fact]
    public void TheImmediateRetryCommand_DoesNotPerformSmtpOnTheScmThread()
    {
        // architecture.md §12: the command only wakes the worker. With no worker started
        // here, nothing is sent and nothing is queued.
        using var temp = new TempDirectory();
        using var context = Create(temp);

        CustomCommand(context.Service, AppConstants.ImmediateRetryCommand);

        Assert.Equal(0, context.Sender.AttemptCount);
        Assert.Empty(Queue(context.Paths).Items);
    }

    // ------------------------------------------------------------ startup path

    [Fact]
    public async Task OnStart_RecordsAStartupEventThatTheWorkerSends()
    {
        using var temp = new TempDirectory();
        using var context = Create(temp);

        // The worker reads the configuration on every attempt, so a valid one is needed
        // for the send to get past validation.
        TestConfig.Write(context.Paths, TestConfig.Create());

        Start(context.Service);

        await Wait.UntilAsync(() => context.Sender.Accepted.Count == 1);
        await Wait.UntilAsync(() => Queue(context.Paths).Items.Count == 0);

        // A manual restart is a Startup event exactly like a real boot (FR-01).
        Assert.Equal(
            MailContent.NotificationSubject(MailEventType.Startup, Environment.MachineName),
            Assert.Single(context.Sender.AcceptedSubjects));

        Stop(context.Service);
        Stop(context.Service); // OnStop and OnShutdown can both arrive for one shutdown.
    }

    [Fact]
    public async Task OnStart_WithoutAUsableConfiguration_LeavesTheEventQueued()
    {
        // No config.json at all: the notification must stay pending rather than be dropped
        // (architecture.md §14.6).
        using var temp = new TempDirectory();
        using var context = Create(temp);

        Start(context.Service);

        await Wait.UntilAsync(() => Queue(context.Paths).Items.Count == 1);

        Stop(context.Service);

        var item = Assert.Single(Queue(context.Paths).Items);
        Assert.Equal(MailEventType.Startup, item.EventType);
        Assert.Equal(0, context.Sender.AttemptCount);
    }

    // ------------------------------------------------------------ reflection glue

    private static void Start(BootWakeMailerService service) =>
        Invoke(service, "OnStart", [Array.Empty<string>()]);

    private static void Stop(BootWakeMailerService service) =>
        Invoke(service, "OnStop", []);

    private static bool PowerEvent(BootWakeMailerService service, PowerBroadcastStatus status) =>
        (bool)Invoke(service, "OnPowerEvent", [status])!;

    private static void CustomCommand(BootWakeMailerService service, int command) =>
        Invoke(service, "OnCustomCommand", [command]);

    private static object? Invoke(BootWakeMailerService service, string name, object?[] arguments) =>
        typeof(BootWakeMailerService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, arguments);
}
