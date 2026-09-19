using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class StatusStoreTests
{
    [Fact]
    public void Load_ReturnsEmptyStatusWhenTheFileDoesNotExist()
    {
        using var temp = new TempDirectory();

        var status = StatusStore.Load(temp.File("status.json"));

        Assert.Equal(1, status.SchemaVersion);
        Assert.Null(status.LastSuccessfulSendAtUtc);
        Assert.Null(status.LastError);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSuccessTimeAndLastError()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");

        var original = new StatusDocument
        {
            LastSuccessfulSendAtUtc = new DateTime(2026, 9, 19, 6, 30, 2, DateTimeKind.Utc),
            LastError = new StatusError
            {
                OccurredAtUtc = new DateTime(2026, 9, 19, 6, 29, 40, DateTimeKind.Utc),
                Operation = "SmtpSend",
                Type = "SmtpCommandException",
                Message = "SMTP server rejected the operation",
            },
        };

        StatusStore.Save(path, original);
        var loaded = StatusStore.Load(path);

        Assert.Equal(original.LastSuccessfulSendAtUtc, loaded.LastSuccessfulSendAtUtc);
        Assert.NotNull(loaded.LastError);
        Assert.Equal("SmtpSend", loaded.LastError.Operation);
        Assert.Equal("SmtpCommandException", loaded.LastError.Type);
        Assert.Equal("SMTP server rejected the operation", loaded.LastError.Message);
        Assert.Equal(original.LastError.OccurredAtUtc, loaded.LastError.OccurredAtUtc);
    }

    [Fact]
    public void Save_KeepsTheLastErrorWhenALaterSuccessIsRecorded()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");

        StatusStore.Save(path, new StatusDocument
        {
            LastError = new StatusError
            {
                OccurredAtUtc = new DateTime(2026, 9, 19, 6, 29, 40, DateTimeKind.Utc),
                Operation = "SmtpSend",
                Type = "SmtpCommandException",
                Message = "rejected",
            },
        });

        // A later success sets the success time but does not erase the error (architecture.md §7).
        var status = StatusStore.Load(path);
        status.LastSuccessfulSendAtUtc = new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc);
        StatusStore.Save(path, status);

        var reloaded = StatusStore.Load(path);

        Assert.NotNull(reloaded.LastSuccessfulSendAtUtc);
        Assert.NotNull(reloaded.LastError);
    }

    [Fact]
    public void Save_WritesUtcTimestampsInIso8601WithZSuffix()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");

        StatusStore.Save(path, new StatusDocument
        {
            LastSuccessfulSendAtUtc = new DateTime(2026, 9, 19, 6, 30, 2, DateTimeKind.Utc),
        });

        Assert.Contains(
            "\"lastSuccessfulSendAtUtc\": \"2026-09-19T06:30:02Z\"",
            File.ReadAllText(path),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsWhenTheFileIsCorrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");
        File.WriteAllText(path, "not json at all");

        Assert.Throws<InvalidDataException>(() => StatusStore.Load(path));
    }

    [Fact]
    public void Save_RejectsNullStatus()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentNullException>(() => StatusStore.Save(temp.File("status.json"), null!));
    }
}
