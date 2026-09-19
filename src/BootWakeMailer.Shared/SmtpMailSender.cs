using System.Security.Cryptography;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BootWakeMailer.Shared;

/// <summary>
/// The single MailKit based SMTP implementation, shared by the Windows Service
/// notifications and the configuration tool's test mail (architecture.md §3.1, §16).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.Net.Mail.SmtpClient"/> is deliberately not used; MailKit is the
/// only SMTP client in this application.
/// </para>
/// <para>
/// The plaintext password only ever exists inside <see cref="SendAsync"/>: it is
/// unprotected from <see cref="SmtpSettings.EncryptedPassword"/> immediately before
/// authentication and is never copied into an exception message, the status document
/// or the queue file (architecture.md §14.10).
/// </para>
/// </remarks>
public sealed class SmtpMailSender : IMailSender
{
    /// <summary>How long the disconnect during cleanup may take before it is abandoned.</summary>
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    /// <param name="timeout">
    /// Upper bound for one complete send attempt; defaults to
    /// <see cref="AppConstants.SmtpSendTimeoutSeconds"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    public SmtpMailSender(TimeSpan? timeout = null)
    {
        Timeout = timeout ?? TimeSpan.FromSeconds(AppConstants.SmtpSendTimeoutSeconds);

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The SMTP timeout must be positive.");
        }
    }

    /// <summary>
    /// Upper bound applied both to each network operation and to the whole attempt.
    /// </summary>
    /// <remarks>
    /// MailKit's own <c>Timeout</c> covers a single connect/authenticate/send/command
    /// exchange. An additional overall deadline is applied as well, so a server that
    /// keeps a connection alive while making no progress still cannot hold the queue
    /// processor for longer than this value.
    /// </remarks>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Maps the configuration's security mode onto the MailKit connection mode
    /// (architecture.md §5).
    /// </summary>
    public static SecureSocketOptions ToSecureSocketOptions(SmtpSecurityMode securityMode) => securityMode switch
    {
        SmtpSecurityMode.Auto => SecureSocketOptions.Auto,
        SmtpSecurityMode.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurityMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurityMode.None => SecureSocketOptions.None,
        _ => throw new MailConfigurationException($"SMTP security mode '{securityMode}' is not supported."),
    };

    /// <inheritdoc />
    public async Task SendAsync(AppConfig config, MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(message);

        var problems = ConfigValidator.Validate(config);
        if (problems.Count > 0)
        {
            throw new MailConfigurationException(string.Join(" ", problems));
        }

        var password = UnprotectPassword(config.Smtp);

        // The linked source bounds the whole attempt; cancellationToken stays the
        // caller's token, so a shutdown can be told apart from a timeout below.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        // MailKit's per-operation timeout is expressed in milliseconds, so an
        // unreachable server cannot hold a connect, authenticate or send open.
        using var client = new SmtpClient
        {
            Timeout = (int)Math.Clamp(Timeout.TotalMilliseconds, 1, int.MaxValue),
        };

        try
        {
            await client
                .ConnectAsync(
                    config.Smtp.Host,
                    config.Smtp.Port,
                    ToSecureSocketOptions(config.Smtp.SecurityMode),
                    deadline.Token)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(config.Smtp.Username))
            {
                await client.AuthenticateAsync(config.Smtp.Username, password, deadline.Token).ConfigureAwait(false);
            }

            await client.SendAsync(message, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline fired while the caller's token is still healthy, so this is
            // an SMTP timeout, not a shutdown.
            throw new TimeoutException(
                $"The SMTP server did not accept the message within {Timeout.TotalSeconds:0.#} seconds.");
        }

        // A caller cancellation (OperationCanceledException) and every SMTP, network or
        // authentication error propagate to the caller, which keeps the task queued.
        finally
        {
            // The server may already have accepted the message. A failing QUIT or socket
            // teardown must not turn that accepted send into a failure
            // (architecture.md §10.7, §14.9), so cleanup is best effort.
            await TryDisconnectAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the DPAPI protected password for this attempt.
    /// </summary>
    /// <returns>
    /// The plaintext password, or an empty string when no password is stored.
    /// </returns>
    private static string UnprotectPassword(SmtpSettings settings)
    {
        try
        {
            return SecretProtector.Unprotect(settings.EncryptedPassword);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            // The message intentionally repeats neither the stored value nor the
            // password (architecture.md §14.10).
            throw new MailConfigurationException(
                "The stored SMTP password cannot be decrypted on this computer. Save the configuration again.",
                exception);
        }
    }

    private static async Task TryDisconnectAsync(SmtpClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        using var cleanup = new CancellationTokenSource(CleanupTimeout);

        try
        {
            await client.DisconnectAsync(quit: true, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort only; the outcome of the send has already been decided.
        }
    }
}
