using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// A test message is sent directly through the shared sender: it is not queued, not
/// retried, and does not change the service status (architecture.md §12).
/// </summary>
public class TestMailTests
{
    [Fact]
    public async Task SendAsync_SendsOneTestMessageThroughTheSharedSender()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Root);
        var sender = new FakeMailSender();
        var sentAt = new DateTime(2026, 9, 19, 7, 30, 0, DateTimeKind.Utc);

        await TestMail.SendAsync(sender, TestConfig.Create(), sentAt);

        var message = Assert.Single(sender.Accepted);
        Assert.Equal(MailContent.TestSubject, message.Subject);
        Assert.Equal(sentAt, MailBody.SentAtUtc(message));
    }

    [Fact]
    public async Task SendAsync_LeavesTheQueueAndTheStatusUntouched()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Root);

        await TestMail.SendAsync(new FakeMailSender(), TestConfig.Create());

        Assert.False(File.Exists(paths.QueueFilePath));
        Assert.False(File.Exists(paths.StatusFilePath));
    }

    [Fact]
    public async Task SendAsync_ReportsTheSendFailureToTheCaller()
    {
        var sender = new FakeMailSender
        {
            FailureFactory = FakeMailSender.AlwaysFails(() => new InvalidOperationException("connection refused")),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestMail.SendAsync(sender, TestConfig.Create()));

        Assert.Equal("connection refused", exception.Message);
        Assert.Empty(sender.Accepted);
    }

    [Fact]
    public async Task SendAsync_RejectsAnInvalidConfiguration()
    {
        var sender = new FakeMailSender();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => TestMail.SendAsync(sender, null!));

        Assert.Equal(0, sender.AttemptCount);
    }
}
