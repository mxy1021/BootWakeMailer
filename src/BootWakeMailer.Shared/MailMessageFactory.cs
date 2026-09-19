using MimeKit;
using MimeKit.Text;

namespace BootWakeMailer.Shared;

/// <summary>
/// Builds the two messages this application can send: a queued notification and a
/// configuration test message (architecture.md §10, §12).
/// </summary>
/// <remarks>
/// The sender and recipient always come from the configuration, so there is exactly one
/// sender account and one recipient (FR-03).
/// </remarks>
public static class MailMessageFactory
{
    /// <summary>
    /// Builds the notification for one queued task using the currently saved
    /// configuration.
    /// </summary>
    /// <exception cref="MimeKit.ParseException">
    /// A configured address is not a valid mailbox. Callers validate the configuration
    /// with <see cref="ConfigValidator"/> first, which reports this as a usable message.
    /// </exception>
    public static MimeMessage CreateNotification(AppConfig config, PendingMailEvent item)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(item);

        return Create(
            config,
            MailContent.NotificationSubject(item.EventType, item.ComputerName),
            MailContent.NotificationBody(item),
            item.OccurredAtUtc);
    }

    /// <summary>Builds a test message for the configuration tool.</summary>
    public static MimeMessage CreateTest(AppConfig config, DateTime sentAtUtc)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Create(
            config,
            MailContent.TestSubject,
            MailContent.TestBody(sentAtUtc),
            sentAtUtc);
    }

    private static MimeMessage Create(AppConfig config, string subject, string body, DateTime dateUtc)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = ToOffset(dateUtc),
            Body = new TextPart(TextFormat.Plain) { Text = body },
        };

        message.From.Add(MailboxAddress.Parse(config.FromAddress));
        message.To.Add(MailboxAddress.Parse(config.ToAddress));

        return message;
    }

    private static DateTimeOffset ToOffset(DateTime timestampUtc)
    {
        var utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        return new DateTimeOffset(utc);
    }
}
