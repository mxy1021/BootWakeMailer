using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// The notification content is fixed by the application and identifies the event type,
/// the computer and the occurrence time (FR-04, architecture.md §10).
/// </summary>
public class MailContentTests
{
    private static PendingMailEvent Item(MailEventType eventType = MailEventType.Startup) =>
        PendingMailEvent.Create(eventType, "ZEN-PC", new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData(MailEventType.Startup, "BootWakeMailer: Startup - ZEN-PC")]
    [InlineData(MailEventType.ResumeAutomatic, "BootWakeMailer: ResumeAutomatic - ZEN-PC")]
    public void NotificationSubject_NamesTheEventTypeAndComputer(MailEventType eventType, string expected)
    {
        Assert.Equal(expected, MailContent.NotificationSubject(eventType, "ZEN-PC"));
    }

    [Fact]
    public void NotificationBody_ContainsTheEventTypeTheComputerAndTheOccurrenceTime()
    {
        var body = MailContent.NotificationBody(Item(MailEventType.ResumeAutomatic));

        Assert.Contains("ResumeAutomatic", body, StringComparison.Ordinal);
        Assert.Contains("ZEN-PC", body, StringComparison.Ordinal);
        Assert.Contains("2026-09-19 06:30:00 UTC", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationBody_ConvertsANonUtcTimestampToUtc()
    {
        var local = new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Local);
        var item = PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC", local);

        var body = MailContent.NotificationBody(item);

        Assert.Contains(
            local.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            body,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TestBody_StatesThatTheMessageIsATest()
    {
        var body = MailContent.TestBody(new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc));

        Assert.Contains("test", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-09-19 07:00:00 UTC", body, StringComparison.Ordinal);
        Assert.Contains("not retried", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TestSubject_IsDistinctFromANotificationSubject()
    {
        Assert.Equal("BootWakeMailer: Test email", MailContent.TestSubject);
        Assert.NotEqual(
            MailContent.TestSubject,
            MailContent.NotificationSubject(MailEventType.Startup, "ZEN-PC"));
    }

    [Fact]
    public void NotificationBody_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => MailContent.NotificationBody(null!));
    }
}
