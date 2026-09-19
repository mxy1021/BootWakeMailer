using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// Both messages must use the single sender and the single recipient from the
/// configuration (FR-03, architecture.md §10).
/// </summary>
public class MailMessageFactoryTests
{
    private static readonly DateTime OccurredAt = new(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void CreateNotification_UsesTheConfiguredSenderAndRecipient()
    {
        var config = TestConfig.Create();
        var item = PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC", OccurredAt);

        var message = MailMessageFactory.CreateNotification(config, item);

        Assert.Equal(TestConfig.FromAddress, Assert.Single(message.From).ToString());
        Assert.Equal(TestConfig.ToAddress, Assert.Single(message.To).ToString());
    }

    [Fact]
    public void CreateNotification_UsesTheFixedSubjectAndBody()
    {
        var config = TestConfig.Create();
        var item = PendingMailEvent.Create(MailEventType.ResumeAutomatic, "ZEN-PC", OccurredAt);

        var message = MailMessageFactory.CreateNotification(config, item);

        Assert.Equal("BootWakeMailer: ResumeAutomatic - ZEN-PC", message.Subject);
        Assert.Equal(OccurredAt, MailBody.OccurredAtUtc(message));
        Assert.Equal("ResumeAutomatic", MailBody.Value(message, "Event"));
        Assert.Equal("ZEN-PC", MailBody.Value(message, "Computer"));
    }

    [Fact]
    public void CreateNotification_DatesTheMessageWithTheOccurrenceTime()
    {
        var item = PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC", OccurredAt);

        var message = MailMessageFactory.CreateNotification(TestConfig.Create(), item);

        Assert.Equal(new DateTimeOffset(OccurredAt), message.Date);
    }

    [Fact]
    public void CreateTest_MarksTheMessageAsATestAndUsesTheConfiguredAddresses()
    {
        var config = TestConfig.Create();
        var sentAt = new DateTime(2026, 9, 19, 8, 15, 0, DateTimeKind.Utc);

        var message = MailMessageFactory.CreateTest(config, sentAt);

        Assert.Equal(MailContent.TestSubject, message.Subject);
        Assert.Equal(sentAt, MailBody.SentAtUtc(message));
        Assert.Equal(TestConfig.FromAddress, Assert.Single(message.From).ToString());
        Assert.Equal(TestConfig.ToAddress, Assert.Single(message.To).ToString());
    }

    [Fact]
    public void CreateNotification_RejectsAnUnusableSenderAddress()
    {
        var config = TestConfig.Create(fromAddress: "not an address");
        var item = PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC", OccurredAt);

        // ConfigValidator reports this first, so the parse failure is only a backstop.
        Assert.ThrowsAny<Exception>(() => MailMessageFactory.CreateNotification(config, item));
    }
}
