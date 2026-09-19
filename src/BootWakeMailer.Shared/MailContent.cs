using System.Globalization;

namespace BootWakeMailer.Shared;

/// <summary>
/// The fixed notification and test-mail text (FR-04, architecture.md §10).
/// </summary>
/// <remarks>
/// There is no template system and no template settings: every notification the
/// application produces is generated from this class.
/// </remarks>
public static class MailContent
{
    /// <summary>Prefix of every subject produced by BootWakeMailer.</summary>
    public const string SubjectPrefix = "BootWakeMailer";

    /// <summary>Subject of a test message sent from the configuration tool.</summary>
    public const string TestSubject = SubjectPrefix + ": Test email";

    /// <summary>Line break used inside message bodies, per the text/plain convention.</summary>
    private const string LineBreak = "\r\n";

    /// <summary>Timestamp format used inside message bodies: UTC, readable, unambiguous.</summary>
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss 'UTC'";

    /// <summary>
    /// Subject of a notification: <c>BootWakeMailer: Startup - PC</c> or
    /// <c>BootWakeMailer: ResumeAutomatic - PC</c> (architecture.md §10).
    /// </summary>
    public static string NotificationSubject(MailEventType eventType, string computerName) =>
        $"{SubjectPrefix}: {eventType} - {computerName}";

    /// <summary>
    /// Body of a notification. It identifies the event type, the computer and the
    /// event occurrence time (FR-04).
    /// </summary>
    public static string NotificationBody(PendingMailEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return string.Join(
            LineBreak,
            SubjectPrefix + " notification",
            string.Empty,
            "Event: " + item.EventType,
            "Computer: " + item.ComputerName,
            "Occurred at: " + FormatTimestamp(item.OccurredAtUtc),
            string.Empty,
            "This message was generated automatically by the " + SubjectPrefix + " Windows service.");
    }

    /// <summary>
    /// Body of a test message. It states that the message is a test so a recipient can
    /// tell it apart from a queued notification (architecture.md §12).
    /// </summary>
    public static string TestBody(DateTime sentAtUtc)
    {
        return string.Join(
            LineBreak,
            SubjectPrefix + " test message",
            string.Empty,
            "This is a test email sent from the " + SubjectPrefix + " configuration tool.",
            "Sent at: " + FormatTimestamp(sentAtUtc),
            string.Empty,
            "No queued notification was sent or removed, and this message is not retried.");
    }

    private static string FormatTimestamp(DateTime timestampUtc) =>
        timestampUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}
