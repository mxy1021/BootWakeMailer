using MimeKit;

namespace BootWakeMailer.Shared;

/// <summary>
/// Sends one already built message using the supplied configuration.
/// </summary>
/// <remarks>
/// The Windows Service and the configuration tool share the single MailKit
/// implementation <see cref="SmtpMailSender"/> (architecture.md §16). This interface
/// exists only so the durable queue logic can be tested without an SMTP server; it is
/// not a plugin point.
/// </remarks>
public interface IMailSender
{
    /// <summary>
    /// Connects to the configured SMTP server, authenticates, and hands the message to
    /// the server.
    /// </summary>
    /// <param name="config">The SMTP settings to use for this attempt.</param>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancels the attempt, for example when the service stops.</param>
    /// <returns>
    /// A task that completes once the SMTP server has accepted the message (FR-07).
    /// Final inbox delivery is not verified.
    /// </returns>
    /// <exception cref="MailConfigurationException">The configuration cannot be used.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task SendAsync(AppConfig config, MimeMessage message, CancellationToken cancellationToken);
}
