using System.Text.Json;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// Locks the persisted document shape described in architecture.md §5-§7, so a
/// later refactor cannot silently change the on-disk format.
/// </summary>
public class ModelSerializationTests
{
    [Theory]
    [InlineData(MailEventType.Startup, "\"Startup\"")]
    [InlineData(MailEventType.ResumeAutomatic, "\"ResumeAutomatic\"")]
    public void EventType_SerializesByName(MailEventType eventType, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(eventType, JsonSettings.SerializerOptions));
    }

    [Theory]
    [InlineData(SmtpSecurityMode.Auto, "\"Auto\"")]
    [InlineData(SmtpSecurityMode.StartTls, "\"StartTls\"")]
    [InlineData(SmtpSecurityMode.SslOnConnect, "\"SslOnConnect\"")]
    [InlineData(SmtpSecurityMode.None, "\"None\"")]
    public void SecurityMode_SerializesByName(SmtpSecurityMode mode, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Serialize(mode, JsonSettings.SerializerOptions));
    }

    [Fact]
    public void EventType_DeserializesFromName()
    {
        Assert.Equal(
            MailEventType.ResumeAutomatic,
            JsonSerializer.Deserialize<MailEventType>("\"ResumeAutomatic\"", JsonSettings.SerializerOptions));
    }

    [Fact]
    public void EventType_RejectsAValueOutsideTheTwoSupportedTriggers()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<MailEventType>("\"ResumeSuspend\"", JsonSettings.SerializerOptions));
    }

    [Fact]
    public void Documents_UseCamelCasePropertyNames()
    {
        var config = new AppConfig { Smtp = new SmtpSettings { Host = "smtp.example.com" } };

        var json = JsonSerializer.Serialize(config, JsonSettings.SerializerOptions);

        Assert.Contains("\"schemaVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"encryptedPassword\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fromAddress\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Documents_StartAtSchemaVersionOne()
    {
        Assert.Equal(1, new AppConfig().SchemaVersion);
        Assert.Equal(1, new QueueDocument().SchemaVersion);
        Assert.Equal(1, new StatusDocument().SchemaVersion);
        Assert.Equal(1, AppConstants.SchemaVersion);
    }

    [Fact]
    public void QueueDocument_StartsEmptyRatherThanNull()
    {
        Assert.NotNull(new QueueDocument().Items);
        Assert.NotNull(new AppConfig().Smtp);
    }

    [Fact]
    public void SharedConstants_MatchTheArchitecture()
    {
        Assert.Equal("BootWakeMailer", AppConstants.ServiceName);

        // WinForms requests an immediate retry with this custom command (architecture.md §12).
        Assert.Equal(128, AppConstants.ImmediateRetryCommand);
    }

    [Fact]
    public void Timestamps_AreWrittenAsUtcIso8601WithZSuffix()
    {
        var status = new StatusDocument
        {
            LastSuccessfulSendAtUtc = new DateTime(2026, 9, 19, 6, 30, 2, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(status, JsonSettings.SerializerOptions);

        Assert.Contains("\"2026-09-19T06:30:02Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamps_RoundTripBackToUtcKind()
    {
        var json = JsonSerializer.Serialize(
            new StatusDocument { LastSuccessfulSendAtUtc = new DateTime(2026, 9, 19, 6, 30, 2, DateTimeKind.Utc) },
            JsonSettings.SerializerOptions);

        var loaded = JsonSerializer.Deserialize<StatusDocument>(json, JsonSettings.SerializerOptions);

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.LastSuccessfulSendAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.LastSuccessfulSendAtUtc.Value.Kind);
    }

    [Fact]
    public void Documents_PrintNonAsciiTextReadably()
    {
        var queue = new QueueDocument
        {
            Items = [PendingMailEvent.Create(MailEventType.Startup, "电脑-01")],
        };

        var json = JsonSerializer.Serialize(queue, JsonSettings.SerializerOptions);

        Assert.Contains("电脑-01", json, StringComparison.Ordinal);
    }
}
