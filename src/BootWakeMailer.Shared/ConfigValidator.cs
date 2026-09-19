using System.Net.Mail;

namespace BootWakeMailer.Shared;

/// <summary>
/// Minimal configuration validation shared by the service and the configuration
/// tool (architecture.md §3.1).
/// </summary>
/// <remarks>
/// Invalid configuration is treated as a send failure, never as a reason to delete
/// a queued task (architecture.md §14.6). Messages deliberately never echo the
/// configured password.
/// </remarks>
public static class ConfigValidator
{
    /// <summary>
    /// Checks whether <paramref name="config"/> is complete enough to attempt an
    /// SMTP send.
    /// </summary>
    /// <returns>
    /// A list of human-readable problems; empty when the configuration is usable.
    /// </returns>
    public static IReadOnlyList<string> Validate(AppConfig? config)
    {
        if (config is null)
        {
            return ["Configuration file is missing or unreadable."];
        }

        var problems = new List<string>();

        if (config.Smtp is null)
        {
            problems.Add("SMTP settings are missing.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.Smtp.Host))
            {
                problems.Add("SMTP host is required.");
            }

            if (config.Smtp.Port is < 1 or > 65535)
            {
                problems.Add("SMTP port must be between 1 and 65535.");
            }

            if (!Enum.IsDefined(config.Smtp.SecurityMode))
            {
                problems.Add("SMTP security mode is not recognized.");
            }

            if (string.IsNullOrWhiteSpace(config.Smtp.Username))
            {
                problems.Add("SMTP user name is required.");
            }
        }

        if (!IsMailAddress(config.FromAddress))
        {
            problems.Add("Sender address is not a valid email address.");
        }

        if (!IsMailAddress(config.ToAddress))
        {
            problems.Add("Recipient address is not a valid email address.");
        }

        return problems;
    }

    /// <summary>Convenience wrapper over <see cref="Validate"/>.</summary>
    public static bool IsValid(AppConfig? config) => Validate(config).Count == 0;

    private static bool IsMailAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value, out _);
}
