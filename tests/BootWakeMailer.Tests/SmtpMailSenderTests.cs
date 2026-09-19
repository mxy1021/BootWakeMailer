using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BootWakeMailer.Shared;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace BootWakeMailer.Tests;

/// <summary>
/// Exercises the real MailKit send path against a loopback SMTP server, plus the
/// configuration and cancellation failures that must not need a server at all.
/// </summary>
public class SmtpMailSenderTests
{
    private static readonly TimeSpan GenerousTimeout = TimeSpan.FromSeconds(20);

    private static MimeMessage Notification(AppConfig config) =>
        MailMessageFactory.CreateNotification(
            config,
            PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC", new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc)));

    [Theory]
    [InlineData(SmtpSecurityMode.Auto, SecureSocketOptions.Auto)]
    [InlineData(SmtpSecurityMode.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(SmtpSecurityMode.SslOnConnect, SecureSocketOptions.SslOnConnect)]
    [InlineData(SmtpSecurityMode.None, SecureSocketOptions.None)]
    public void ToSecureSocketOptions_MapsTheConfiguredMode(SmtpSecurityMode mode, SecureSocketOptions expected)
    {
        Assert.Equal(expected, SmtpMailSender.ToSecureSocketOptions(mode));
    }

    [Fact]
    public void ToSecureSocketOptions_MapsEveryDeclaredMode()
    {
        // A new mode added to the enum without a mapping must fail here, not at send time.
        foreach (var mode in Enum.GetValues<SmtpSecurityMode>())
        {
            Assert.Null(Record.Exception(() => SmtpMailSender.ToSecureSocketOptions(mode)));
        }
    }

    [Fact]
    public async Task SendAsync_DeliversTheMessageWhenTheServerAcceptsIt()
    {
        using var server = new FakeSmtpServer(new FakeSmtpServerOptions { AdvertiseAuthentication = true });
        var config = TestConfig.ForServer(server, TestConfig.FromAddress, "app-password");
        var sender = new SmtpMailSender(GenerousTimeout);

        await sender.SendAsync(config, Notification(config), CancellationToken.None);

        var received = Assert.Single(server.Messages);
        Assert.Equal(TestConfig.FromAddress, received.From);
        Assert.Equal([TestConfig.ToAddress], received.Recipients);
        Assert.Contains("BootWakeMailer: Startup - ZEN-PC", received.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_AuthenticatesWithTheAccountFromTheConfiguration()
    {
        using var server = new FakeSmtpServer(new FakeSmtpServerOptions { AdvertiseAuthentication = true });
        var config = TestConfig.ForServer(server, "sender@example.com", "app-password");
        var sender = new SmtpMailSender(GenerousTimeout);

        await sender.SendAsync(config, Notification(config), CancellationToken.None);

        Assert.True(server.AuthenticationAttempted, "The sender must authenticate with the configured account.");
        Assert.Single(server.Messages);
    }

    [Fact]
    public async Task SendAsync_FailsWhenTheServerRejectsTheCredentials()
    {
        using var server = new FakeSmtpServer(
            new FakeSmtpServerOptions { AdvertiseAuthentication = true, AcceptAuthentication = false });
        var config = TestConfig.ForServer(server, "sender@example.com", "s3cret-app-password");
        var sender = new SmtpMailSender(GenerousTimeout);

        var exception = await Record.ExceptionAsync(
            () => sender.SendAsync(config, Notification(config), CancellationToken.None));

        Assert.NotNull(exception);
        Assert.Empty(server.Messages);

        // The password must never reach an error message that is persisted to status.json
        // or shown in the tool (architecture.md §14.10).
        Assert.DoesNotContain("s3cret-app-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_TimesOutWhenTheServerNeverAnswers()
    {
        using var server = new FakeSmtpServer(new FakeSmtpServerOptions { StaySilent = true });
        var config = TestConfig.ForServer(server, TestConfig.FromAddress, "app-password");
        var sender = new SmtpMailSender(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(
            () => sender.SendAsync(config, Notification(config), CancellationToken.None));
        stopwatch.Stop();

        Assert.NotNull(exception);
        Assert.False(
            exception is OperationCanceledException,
            "No caller token was cancelled, so the attempt must report a timeout or a network error, not a cancellation.");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"The attempt must give up within the configured timeout, but took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task SendAsync_FailsQuicklyWhenNothingIsListening()
    {
        var port = UnusedLoopbackPort();
        var config = TestConfig.Create(host: "127.0.0.1", port: port, username: TestConfig.FromAddress);
        var sender = new SmtpMailSender(TimeSpan.FromSeconds(5));

        var stopwatch = Stopwatch.StartNew();
        var exception = await Record.ExceptionAsync(
            () => sender.SendAsync(config, Notification(config), CancellationToken.None));
        stopwatch.Stop();

        Assert.NotNull(exception);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"A refused connection must fail fast, but took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task SendAsync_ReportsCancellationAsCancellation()
    {
        using var server = new FakeSmtpServer(new FakeSmtpServerOptions { StaySilent = true });
        var config = TestConfig.ForServer(server, TestConfig.FromAddress, "app-password");
        var sender = new SmtpMailSender(GenerousTimeout);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // A cancelled attempt is a shutdown, not a timeout, so the queue keeps the task
        // without recording a send error (architecture.md §14.9).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(config, Notification(config), cancellation.Token));
    }

    [Fact]
    public async Task SendAsync_RejectsAMissingConfiguration()
    {
        var sender = new SmtpMailSender(GenerousTimeout);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sender.SendAsync(null!, MimeMessage(), CancellationToken.None));
    }

    [Fact]
    public async Task SendAsync_RejectsAnEmptyHostWithoutConnecting()
    {
        var config = TestConfig.Create(host: " ", username: TestConfig.FromAddress);
        var sender = new SmtpMailSender(GenerousTimeout);

        var exception = await Assert.ThrowsAsync<MailConfigurationException>(
            () => sender.SendAsync(config, Notification(config), CancellationToken.None));

        Assert.Contains("SMTP host is required.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_RejectsAnInvalidRecipientWithoutConnecting()
    {
        var config = TestConfig.Create(username: TestConfig.FromAddress, toAddress: "not-an-address");
        var sender = new SmtpMailSender(GenerousTimeout);

        var exception = await Assert.ThrowsAsync<MailConfigurationException>(
            () => sender.SendAsync(config, Notification(config), CancellationToken.None));

        Assert.Contains("Recipient address is not a valid email address.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_RejectsAPasswordThatCannotBeDecrypted()
    {
        var config = TestConfig.Create(username: TestConfig.FromAddress);
        config.Smtp.EncryptedPassword = Convert.ToBase64String([1, 2, 3, 4, 5]);
        var sender = new SmtpMailSender(GenerousTimeout);

        var exception = await Assert.ThrowsAsync<MailConfigurationException>(
            () => sender.SendAsync(config, Notification(config), CancellationToken.None));

        Assert.Contains("password cannot be decrypted", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(config.Smtp.EncryptedPassword, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveTimeout(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SmtpMailSender(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Constructor_UsesTheConfiguredDefaultTimeout()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(AppConstants.SmtpSendTimeoutSeconds),
            new SmtpMailSender().Timeout);
    }

    private static MimeMessage MimeMessage() =>
        new() { Subject = "unused", Body = new TextPart(TextFormat.Plain) { Text = "unused" } };

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
