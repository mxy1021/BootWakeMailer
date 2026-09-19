using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class PendingMailEventTests
{
    [Fact]
    public void Create_FillsIdComputerNameAndEventType()
    {
        var item = PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC");

        Assert.True(Guid.TryParse(item.Id, out _));
        Assert.Equal("ZEN-PC", item.ComputerName);
        Assert.Equal(MailEventType.Startup, item.EventType);
    }

    [Fact]
    public void Create_GivesEachItemAUniqueId()
    {
        var first = PendingMailEvent.Create(MailEventType.Startup, "PC");
        var second = PendingMailEvent.Create(MailEventType.Startup, "PC");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_LeavesATaskUnattempted()
    {
        var item = PendingMailEvent.Create(MailEventType.ResumeAutomatic, "PC");

        Assert.Equal(0, item.AttemptCount);
        Assert.Null(item.LastAttemptAtUtc);
        Assert.Null(item.LastError);
    }

    [Fact]
    public void Create_DefaultsOccurrenceToCurrentUtcTime()
    {
        var before = DateTime.UtcNow;

        var item = PendingMailEvent.Create(MailEventType.Startup, "PC");

        Assert.Equal(DateTimeKind.Utc, item.OccurredAtUtc.Kind);
        Assert.InRange(item.OccurredAtUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void Create_ConvertsALocalTimeToUtc()
    {
        var local = new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Local);

        var item = PendingMailEvent.Create(MailEventType.Startup, "PC", local);

        Assert.Equal(DateTimeKind.Utc, item.OccurredAtUtc.Kind);
        Assert.Equal(local.ToUniversalTime(), item.OccurredAtUtc);
    }

    [Fact]
    public void Create_KeepsAnAlreadyUtcTimeUnchanged()
    {
        var utc = new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc);

        var item = PendingMailEvent.Create(MailEventType.Startup, "PC", utc);

        Assert.Equal(utc, item.OccurredAtUtc);
    }
}
