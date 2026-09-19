using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BootWakeMailer.Tests;

/// <summary>Behaviour switches for <see cref="FakeSmtpServer"/>.</summary>
internal sealed class FakeSmtpServerOptions
{
    /// <summary>Accept the connection but never send the 220 greeting, to exercise the client timeout.</summary>
    public bool StaySilent { get; init; }

    /// <summary>Advertise <c>AUTH PLAIN</c> and <c>AUTH LOGIN</c> in the EHLO response.</summary>
    public bool AdvertiseAuthentication { get; init; }

    /// <summary>Accept the offered credentials; when false the server answers 535.</summary>
    public bool AcceptAuthentication { get; init; } = true;
}

/// <summary>One message the fake server accepted.</summary>
internal sealed record ReceivedMessage(string From, IReadOnlyList<string> Recipients, string Data);

/// <summary>
/// A minimal plaintext SMTP server on a loopback port, so the real MailKit send path can
/// be tested end to end: connection, greeting, EHLO, optional authentication, MAIL/RCPT,
/// DATA and acceptance (FR-07).
/// </summary>
/// <remarks>
/// Only what <c>MailKit.Net.Smtp.SmtpClient</c> needs is implemented. TLS is not
/// offered, so tests use <see cref="BootWakeMailer.Shared.SmtpSecurityMode.None"/>.
/// </remarks>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptLoop;
    private readonly Lock _gate = new();
    private readonly List<ReceivedMessage> _messages = [];
    private readonly List<string> _connections = [];

    public FakeSmtpServer(FakeSmtpServerOptions? options = null)
    {
        Options = options ?? new FakeSmtpServerOptions();

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
    }

    public FakeSmtpServerOptions Options { get; }

    /// <summary>The loopback port the server listens on.</summary>
    public int Port { get; }

    /// <summary>Number of connections accepted so far.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    /// <summary>True when a client offered credentials.</summary>
    public bool AuthenticationAttempted { get; private set; }

    /// <summary>Messages accepted by this server, in the order they were queued.</summary>
    public IReadOnlyList<ReceivedMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _listener.Stop();

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The accept loop ends by cancellation; that is not a test failure.
        }

        _cancellation.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            lock (_gate)
            {
                _connections.Add(client.Client.RemoteEndPoint?.ToString() ?? "unknown");
            }

            // One task per connection; a broken client must not stop the server.
            _ = Task.Run(() => HandleConnectionAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" })
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            try
            {
                await HandleSessionAsync(reader, writer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A client that disconnects mid-session (timeout and cancellation tests)
                // is expected behaviour for this fake, not a failure.
            }
        }
    }

    private async Task HandleSessionAsync(StreamReader reader, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (Options.StaySilent)
        {
            // Hold the connection open without greeting, so the client must time out.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ReplyAsync(writer, "220 fake ESMTP BootWakeMailer.Tests").ConfigureAwait(false);

        var sender = string.Empty;
        var recipients = new List<string>();

        while (await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (StartsWith(line, "EHLO") || StartsWith(line, "HELO"))
            {
                await ReplyAsync(writer, "250-fake greets you").ConfigureAwait(false);

                if (Options.AdvertiseAuthentication)
                {
                    await ReplyAsync(writer, "250-AUTH PLAIN LOGIN").ConfigureAwait(false);
                }

                await ReplyAsync(writer, "250-8BITMIME").ConfigureAwait(false);
                await ReplyAsync(writer, "250 SMTPUTF8").ConfigureAwait(false);
            }
            else if (StartsWith(line, "AUTH"))
            {
                await HandleAuthenticationAsync(reader, writer, line, cancellationToken).ConfigureAwait(false);
            }
            else if (StartsWith(line, "MAIL FROM"))
            {
                sender = Address(line);
                recipients.Clear();
                await ReplyAsync(writer, "250 2.1.0 Ok").ConfigureAwait(false);
            }
            else if (StartsWith(line, "RCPT TO"))
            {
                recipients.Add(Address(line));
                await ReplyAsync(writer, "250 2.1.5 Ok").ConfigureAwait(false);
            }
            else if (StartsWith(line, "RSET") || StartsWith(line, "NOOP"))
            {
                await ReplyAsync(writer, "250 2.1.0 Ok").ConfigureAwait(false);
            }
            else if (StartsWith(line, "DATA"))
            {
                await ReplyAsync(writer, "354 End data with <CR><LF>.<CR><LF>").ConfigureAwait(false);
                var data = await ReadDataAsync(reader, cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    _messages.Add(new ReceivedMessage(sender, recipients.ToArray(), data));
                }

                await ReplyAsync(writer, "250 2.0.0 Ok: queued as fake").ConfigureAwait(false);
            }
            else if (StartsWith(line, "QUIT"))
            {
                await ReplyAsync(writer, "221 2.0.0 Bye").ConfigureAwait(false);
                return;
            }
            else
            {
                await ReplyAsync(writer, "500 5.5.1 Command not recognized").ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAuthenticationAsync(
        StreamReader reader,
        StreamWriter writer,
        string line,
        CancellationToken cancellationToken)
    {
        AuthenticationAttempted = true;

        // "AUTH PLAIN <base64>" carries the credentials inline; "AUTH LOGIN" asks for them.
        var mechanism = line.Length > 4 ? line[4..].Trim() : string.Empty;

        if (StartsWith(mechanism, "LOGIN"))
        {
            await ReplyAsync(writer, "334 VXNlcm5hbWU6").ConfigureAwait(false);
            _ = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
            await ReplyAsync(writer, "334 UGFzc3dvcmQ6").ConfigureAwait(false);
            _ = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        else if (!mechanism.Contains(' ', StringComparison.Ordinal))
        {
            // A bare "AUTH PLAIN" without an initial response.
            await ReplyAsync(writer, "334 ").ConfigureAwait(false);
            _ = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        await ReplyAsync(
            writer,
            Options.AcceptAuthentication
                ? "235 2.7.0 Authentication successful"
                : "535 5.7.8 Authentication credentials invalid").ConfigureAwait(false);
    }

    private static async Task<string> ReadDataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var data = new StringBuilder();

        while (await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line == ".")
            {
                break;
            }

            // Undo dot stuffing so the recorded data matches what the client sent.
            data.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
        }

        return data.ToString();
    }

    private static Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken) =>
        reader.ReadLineAsync(cancellationToken).AsTask();

    private static Task ReplyAsync(StreamWriter writer, string text) => writer.WriteLineAsync(text);

    /// <summary>Extracts the address from a <c>MAIL FROM:&lt;a@b&gt;</c> style command.</summary>
    private static string Address(string command)
    {
        var start = command.IndexOf('<', StringComparison.Ordinal);
        var end = command.IndexOf('>', StringComparison.Ordinal);

        return start >= 0 && end > start
            ? command[(start + 1)..end]
            : command[(command.IndexOf(':') + 1)..].Trim();
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
