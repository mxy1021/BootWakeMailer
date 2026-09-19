namespace BootWakeMailer.Shared;

/// <summary>
/// A send could not be attempted because the SMTP configuration is missing, incomplete
/// or unreadable.
/// </summary>
/// <remarks>
/// Invalid configuration is a send failure, never a reason to delete a queued task
/// (architecture.md §14.6). The exception carries a distinct type so the status
/// document and the configuration tool can tell "not configured" apart from an actual
/// SMTP or network error.
/// </remarks>
public sealed class MailConfigurationException : Exception
{
    /// <param name="message">Concise, actionable text. Must never contain the SMTP password.</param>
    public MailConfigurationException(string message)
        : base(message)
    {
    }

    /// <param name="message">Concise, actionable text. Must never contain the SMTP password.</param>
    /// <param name="innerException">Underlying configuration or DPAPI failure.</param>
    public MailConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
