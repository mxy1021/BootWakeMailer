namespace BootWakeMailer.Shared;

/// <summary>
/// Sends a single test message using the current configuration (FR-08, architecture.md §12).
/// </summary>
/// <remarks>
/// A test message is sent directly through the shared MailKit sender. It is never
/// inserted into <c>queue.json</c>, it is never retried, and it does not update
/// <c>lastSuccessfulSendAtUtc</c>, because that field describes queued service
/// notifications. The caller shows the result of this call in its own UI.
/// </remarks>
public static class TestMail
{
    /// <summary>
    /// Sends one test message and returns when the SMTP server has accepted it.
    /// </summary>
    /// <param name="sender">The shared MailKit sender.</param>
    /// <param name="config">The configuration to use; typically the values currently shown in the tool.</param>
    /// <param name="sentAtUtc">Test time recorded in the body; defaults to the current UTC time.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <exception cref="MailConfigurationException">The configuration cannot be used.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static Task SendAsync(
        IMailSender sender,
        AppConfig config,
        DateTime? sentAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(config);

        var message = MailMessageFactory.CreateTest(config, sentAtUtc ?? DateTime.UtcNow);

        return sender.SendAsync(config, message, cancellationToken);
    }
}
