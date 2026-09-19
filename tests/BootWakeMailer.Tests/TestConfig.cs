using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// Builds configuration documents for tests, with the password protected exactly as the
/// application protects it (FR-03).
/// </summary>
internal static class TestConfig
{
    public const string FromAddress = "sender@example.com";
    public const string ToAddress = "recipient@example.com";

    /// <remarks>
    /// <paramref name="username"/> defaults to the sender address, because the
    /// configuration must name the single sender account to be valid (FR-03).
    /// </remarks>
    public static AppConfig Create(
        string host = "127.0.0.1",
        int port = 587,
        SmtpSecurityMode securityMode = SmtpSecurityMode.None,
        string username = FromAddress,
        string password = "",
        string fromAddress = FromAddress,
        string toAddress = ToAddress) =>
        new()
        {
            Smtp = new SmtpSettings
            {
                Host = host,
                Port = port,
                SecurityMode = securityMode,
                Username = username,
                EncryptedPassword = SecretProtector.Protect(password),
            },
            FromAddress = fromAddress,
            ToAddress = toAddress,
        };

    /// <summary>Writes <paramref name="config"/> as <c>config.json</c> in <paramref name="paths"/>.</summary>
    public static void Write(AppPaths paths, AppConfig config) =>
        ConfigStore.Save(paths.ConfigFilePath, config);

    /// <summary>A configuration that points at <paramref name="server"/>.</summary>
    public static AppConfig ForServer(FakeSmtpServer server, string username = FromAddress, string password = "") =>
        Create(host: "127.0.0.1", port: server.Port, SmtpSecurityMode.None, username, password);
}
