using BootWakeMailer.Shared;
using MimeKit;

namespace BootWakeMailer.Tests;

/// <summary>
/// A test double for the shared MailKit sender, so the durable queue can be exercised
/// without an SMTP server.
/// </summary>
internal sealed class FakeMailSender : IMailSender
{
    private readonly Lock _gate = new();
    private readonly List<MimeMessage> _accepted = [];
    private readonly List<AppConfig> _configs = [];

    /// <summary>
    /// Returns the exception that should fail an attempt, or <c>null</c> to accept the
    /// message. The second argument is the one-based attempt number.
    /// </summary>
    public Func<MimeMessage, int, Exception?>? FailureFactory { get; set; }

    /// <summary>
    /// When set, the first attempt waits here until the test completes it. The value is
    /// consumed by that attempt, so later attempts are never held.
    /// </summary>
    public TaskCompletionSource? HoldFirstSend { get; set; }

    /// <summary>Completed when an attempt starts, before any hold or failure.</summary>
    public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Makes every attempt take measurable time, so a cycle has a real duration.</summary>
    public TimeSpan SendDelay { get; set; }

    /// <summary>Number of attempts made, including failed ones.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Messages the fake accepted, in send order.</summary>
    public IReadOnlyList<MimeMessage> Accepted
    {
        get
        {
            lock (_gate)
            {
                return _accepted.ToArray();
            }
        }
    }

    /// <summary>Subjects of the accepted messages, in send order.</summary>
    public IReadOnlyList<string> AcceptedSubjects =>
        Accepted.Select(message => message.Subject ?? string.Empty).ToArray();

    /// <summary>Configuration handed to each attempt, in attempt order.</summary>
    public IReadOnlyList<AppConfig> Configs
    {
        get
        {
            lock (_gate)
            {
                return _configs.ToArray();
            }
        }
    }

    public async Task SendAsync(AppConfig config, MimeMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        int attempt;

        lock (_gate)
        {
            attempt = ++AttemptCount;
            _configs.Add(config);
        }

        var hold = HoldFirstSend;
        HoldFirstSend = null;

        SendStarted.TrySetResult();

        if (hold is not null)
        {
            await hold.Task.ConfigureAwait(false);
        }

        if (SendDelay > TimeSpan.Zero)
        {
            await Task.Delay(SendDelay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var failure = FailureFactory?.Invoke(message, attempt);

        if (failure is not null)
        {
            throw failure;
        }

        lock (_gate)
        {
            _accepted.Add(message);
        }
    }

    /// <summary>A failure factory that fails every attempt.</summary>
    public static Func<MimeMessage, int, Exception?> AlwaysFails(Func<Exception> exception) =>
        (_, _) => exception();

    /// <summary>A failure factory that fails the first <paramref name="failures"/> attempts.</summary>
    public static Func<MimeMessage, int, Exception?> FailsThenSucceeds(int failures, Func<Exception> exception) =>
        (_, attempt) => attempt <= failures ? exception() : null;
}
