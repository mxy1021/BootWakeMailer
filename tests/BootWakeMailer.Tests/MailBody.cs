using System.Globalization;
using MimeKit;

namespace BootWakeMailer.Tests;

/// <summary>
/// Reads the fixed application-generated fields out of a notification or test body.
/// </summary>
internal static class MailBody
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss 'UTC'";

    public static string Text(MimeMessage message) => message.TextBody ?? string.Empty;

    /// <summary>Value of the <c>Label: value</c> line, or an empty string when absent.</summary>
    public static string Value(MimeMessage message, string label)
    {
        var prefix = label + ": ";

        foreach (var line in Text(message).Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line[prefix.Length..].TrimEnd('\r');
            }
        }

        return string.Empty;
    }

    /// <summary>The event occurrence time a notification body reports.</summary>
    public static DateTime OccurredAtUtc(MimeMessage message) =>
        ParseTimestamp(Value(message, "Occurred at"));

    /// <summary>The time a test message body reports.</summary>
    public static DateTime SentAtUtc(MimeMessage message) => ParseTimestamp(Value(message, "Sent at"));

    private static DateTime ParseTimestamp(string value) =>
        DateTime.ParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
