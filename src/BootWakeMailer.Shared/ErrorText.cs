namespace BootWakeMailer.Shared;

/// <summary>
/// Turns an exception into the short, actionable text stored in <c>status.json</c> and
/// in a queue item's <c>lastError</c> (architecture.md §7, §14.10).
/// </summary>
/// <remarks>
/// This class never adds configuration values of its own, so it cannot introduce the
/// SMTP password into a persisted error. <see cref="SmtpMailSender"/> guarantees the
/// same for the exceptions it lets through.
/// </remarks>
public static class ErrorText
{
    /// <summary>Upper bound for a stored message, so a status file stays small.</summary>
    private const int MaxMessageLength = 512;

    /// <summary>
    /// Builds the status document entry describing <paramref name="exception"/>.
    /// </summary>
    public static StatusError ToStatusError(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new StatusError
        {
            OccurredAtUtc = DateTime.UtcNow,
            Operation = operation,
            Type = DescribeType(exception),
            Message = Describe(exception),
        };
    }

    /// <summary>Exception type name, for example <c>SmtpCommandException</c>.</summary>
    public static string DescribeType(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.GetType().Name;
    }

    /// <summary>
    /// One-line message for <paramref name="exception"/>, or its type name when the
    /// exception carries no message.
    /// </summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;

        if (string.IsNullOrWhiteSpace(message))
        {
            return DescribeType(exception);
        }

        // Collapse line breaks so the text stays readable in the single-line status UI.
        message = message.Trim().ReplaceLineEndings(" ");

        return message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
    }

    /// <summary>
    /// Operation names recorded in <c>status.json</c>. Keeping them here avoids
    /// spelling mistakes in the service and in the configuration tool.
    /// </summary>
    public static class Operations
    {
        /// <summary>An SMTP connect, authenticate or send step, including its configuration input.</summary>
        public const string SmtpSend = "SmtpSend";

        /// <summary>Reading or writing <c>queue.json</c>.</summary>
        public const string QueueFile = "QueueFile";

        /// <summary>Reading or writing <c>status.json</c>.</summary>
        public const string StatusFile = "StatusFile";

        /// <summary>
        /// A fault in the queue-processing loop itself, rather than in one of the operations
        /// above. Recorded so a loop that stopped draining the queue is not silent.
        /// </summary>
        public const string QueueWorker = "QueueWorker";
    }
}
