using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class QueueStoreTests
{
    [Fact]
    public void Load_ReturnsAnEmptyQueueWhenTheFileDoesNotExist()
    {
        using var temp = new TempDirectory();

        var queue = QueueStore.Load(temp.File("queue.json"));

        Assert.Empty(queue.Items);
        Assert.Equal(1, queue.SchemaVersion);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        var item = PendingMailEvent.Create(
            MailEventType.ResumeAutomatic,
            "ZEN-PC",
            new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc));
        item.AttemptCount = 1;
        item.LastAttemptAtUtc = new DateTime(2026, 9, 19, 6, 30, 1, DateTimeKind.Utc);
        item.LastError = "SMTP connection failed";

        QueueStore.Save(path, new QueueDocument { Items = [item] });
        var loaded = QueueStore.Load(path);

        var reloaded = Assert.Single(loaded.Items);
        Assert.Equal(item.Id, reloaded.Id);
        Assert.Equal(MailEventType.ResumeAutomatic, reloaded.EventType);
        Assert.Equal("ZEN-PC", reloaded.ComputerName);
        Assert.Equal(item.OccurredAtUtc, reloaded.OccurredAtUtc);
        Assert.Equal(1, reloaded.AttemptCount);
        Assert.Equal(item.LastAttemptAtUtc, reloaded.LastAttemptAtUtc);
        Assert.Equal("SMTP connection failed", reloaded.LastError);
    }

    [Fact]
    public void SaveThenLoad_PreservesItemOrder()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        var first = PendingMailEvent.Create(MailEventType.Startup, "PC", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = PendingMailEvent.Create(MailEventType.ResumeAutomatic, "PC", new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));

        QueueStore.Save(path, new QueueDocument { Items = [first, second] });
        var loaded = QueueStore.Load(path);

        Assert.Equal([first.Id, second.Id], loaded.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Load_ThrowsWhenTheQueueIsCorruptAndLeavesTheFileUntouched()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        const string damaged = "{ \"items\": [ { \"id\": ";
        File.WriteAllText(path, damaged);

        // A damaged queue must be reported, never silently replaced by an empty one.
        Assert.Throws<InvalidDataException>(() => QueueStore.Load(path));
        Assert.Equal(damaged, File.ReadAllText(path));
    }

    [Fact]
    public void Load_TreatsAnExplicitNullItemsArrayAsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 1, \"items\": null }");

        var queue = QueueStore.Load(path);

        Assert.NotNull(queue.Items);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void Save_WritesUtcTimestampsInIso8601WithZSuffix()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        var item = PendingMailEvent.Create(
            MailEventType.Startup,
            "ZEN-PC",
            new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc));

        QueueStore.Save(path, new QueueDocument { Items = [item] });
        var text = File.ReadAllText(path);

        Assert.Contains("\"occurredAtUtc\": \"2026-09-19T06:30:00Z\"", text, StringComparison.Ordinal);
        Assert.Contains("\"eventType\": \"Startup\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_WritesAttemptFieldsAsNullBeforeTheFirstAttempt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        QueueStore.Save(path, new QueueDocument { Items = [PendingMailEvent.Create(MailEventType.Startup, "PC")] });
        var loaded = QueueStore.Load(path);

        var reloaded = Assert.Single(loaded.Items);
        Assert.Equal(0, reloaded.AttemptCount);
        Assert.Null(reloaded.LastAttemptAtUtc);
        Assert.Null(reloaded.LastError);
    }

    [Fact]
    public void Save_RejectsNullQueue()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentNullException>(() => QueueStore.Save(temp.File("queue.json"), null!));
    }
}
