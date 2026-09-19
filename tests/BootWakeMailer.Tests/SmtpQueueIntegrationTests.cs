using System.Net;
using System.Net.Sockets;
using BootWakeMailer.Service;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// The durable queue driven through the real MailKit sender and a real SMTP conversation
/// on a loopback port: with nothing faked between <c>queue.json</c> and the socket, these
/// are the FR-05 to FR-07 paths for an available network, an unreachable server and a
/// rejected login.
/// </summary>
public class SmtpQueueIntegrationTests
{
    private const string ComputerName = "ZEN-PC";
    private const string Password = "app-password";

    private static readonly DateTime OccurredAt = new(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc);

    /// <summary>Short enough that a failing attempt cannot stall the suite.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Stands in for the service's 60-second cadence.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task AnAcceptedNotificationIsRemovedFromTheQueueAndRecordedAsSent()
    {
        using var temp = new TempDirectory();
        using var server = new FakeSmtpServer(new FakeSmtpServerOptions { AdvertiseAuthentication = true });
        var paths = new AppPaths(temp.Root);
        TestConfig.Write(paths, TestConfig.ForServer(server, TestConfig.FromAddress, Password));

        using var processor = new QueueProcessor(paths, new SmtpMailSender(SendTimeout), RetryInterval, ComputerName);
        processor.Enqueue(MailEventType.Startup, OccurredAt);

        var result = await processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.SentCount);
        Assert.Empty(QueueStore.Load(paths.QueueFilePath).Items);
        Assert.NotNull(StatusStore.Load(paths.StatusFilePath).LastSuccessfulSendAtUtc);

        var received = Assert.Single(server.Messages);
        Assert.Equal(TestConfig.FromAddress, received.From);
        Assert.Equal([TestConfig.ToAddress], received.Recipients);
        Assert.Contains("BootWakeMailer: Startup - ZEN-PC", received.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServerKeepsTheTaskAndTheRetryLoopSendsItOnceTheServerIsBack()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Root);

        // Nothing is listening on this port yet, so the first attempt is a real refused
        // connection rather than a configured failure.
        var port = UnusedLoopbackPort();
        TestConfig.Write(
            paths,
            TestConfig.Create(host: "127.0.0.1", port: port, username: TestConfig.FromAddress, password: Password));

        using var processor = new QueueProcessor(paths, new SmtpMailSender(SendTimeout), RetryInterval, ComputerName);
        processor.Enqueue(MailEventType.Startup, OccurredAt);

        var failed = await processor.ProcessOnceAsync(CancellationToken.None);

        // The task stays pending with its attempt on record (requirements 6 and 9).
        Assert.Equal(QueueCycleOutcome.Failed, failed.Outcome);
        var pending = Assert.Single(QueueStore.Load(paths.QueueFilePath).Items);
        Assert.Equal(1, pending.AttemptCount);
        Assert.NotNull(pending.LastAttemptAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(pending.LastError));
        Assert.Equal("SmtpSend", StatusStore.Load(paths.StatusFilePath).LastError!.Operation);

        // The network comes back on the same address, and the automatic retry loop is the
        // only thing that sends the task (requirement 7).
        using var server = new FakeSmtpServer(new FakeSmtpServerOptions { AdvertiseAuthentication = true }, port);

        using var cancellation = new CancellationTokenSource();
        var loop = processor.RunAsync(cancellation.Token);

        await Wait.UntilAsync(
            () => QueueStore.Load(paths.QueueFilePath).Items.Count == 0,
            TimeSpan.FromSeconds(30));

        await cancellation.CancelAsync();
        await loop;

        var received = Assert.Single(server.Messages);
        Assert.Contains("BootWakeMailer: Startup - ZEN-PC", received.Data, StringComparison.Ordinal);
        Assert.NotNull(StatusStore.Load(paths.StatusFilePath).LastSuccessfulSendAtUtc);
    }

    [Fact]
    public async Task ARejectedLoginKeepsTheServiceProcessingAndTheTaskPending()
    {
        using var temp = new TempDirectory();
        using var rejecting = new FakeSmtpServer(
            new FakeSmtpServerOptions { AdvertiseAuthentication = true, AcceptAuthentication = false });
        var paths = new AppPaths(temp.Root);
        TestConfig.Write(paths, TestConfig.ForServer(rejecting, TestConfig.FromAddress, "wrong-password"));

        using var processor = new QueueProcessor(paths, new SmtpMailSender(SendTimeout), RetryInterval, ComputerName);
        processor.Enqueue(MailEventType.Startup, OccurredAt);

        using var cancellation = new CancellationTokenSource();
        var loop = processor.RunAsync(cancellation.Token);

        // The rejected login must not end the retry loop: it keeps attempting until the
        // service is stopped (requirement 10).
        await Wait.UntilAsync(
            () => QueueStore.Load(paths.QueueFilePath).Items.SingleOrDefault()?.AttemptCount >= 2,
            TimeSpan.FromSeconds(30));

        await cancellation.CancelAsync();
        await loop;

        // The notification is not lost, and the error the tool displays names the SMTP
        // operation without echoing the password.
        var pending = Assert.Single(QueueStore.Load(paths.QueueFilePath).Items);
        Assert.False(string.IsNullOrWhiteSpace(pending.LastError));
        Assert.Empty(rejecting.Messages);

        var error = Assert.IsType<StatusError>(StatusStore.Load(paths.StatusFilePath).LastError);
        Assert.Equal("SmtpSend", error.Operation);
        Assert.DoesNotContain("wrong-password", error.Message, StringComparison.Ordinal);

        // The same pending task goes out once the configuration is corrected, which also
        // shows that a later attempt uses the newly saved settings (architecture.md §12).
        using var accepting = new FakeSmtpServer(new FakeSmtpServerOptions { AdvertiseAuthentication = true });
        TestConfig.Write(paths, TestConfig.ForServer(accepting, TestConfig.FromAddress, Password));

        var sent = await processor.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(QueueCycleOutcome.Completed, sent.Outcome);
        Assert.Empty(QueueStore.Load(paths.QueueFilePath).Items);
        Assert.Single(accepting.Messages);
    }

    /// <summary>A loopback port that was just released, so nothing is listening on it.</summary>
    private static int UnusedLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
