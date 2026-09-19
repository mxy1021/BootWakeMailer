using System.Security.Cryptography;
using System.Text;

namespace BootWakeMailer.Shared;

/// <summary>
/// Windows DPAPI protection for the SMTP password (FR-03).
/// </summary>
/// <remarks>
/// Machine scope is used deliberately: the Windows Service account and the local
/// configuration tool must be able to read the same saved credential
/// (architecture.md §5). Machine scope means any process on this computer can
/// unprotect the value; that is the accepted MVP tradeoff versus storing the
/// password in plaintext.
/// </remarks>
public static class SecretProtector
{
    private const DataProtectionScope Scope = DataProtectionScope.LocalMachine;

    /// <summary>
    /// Protects <paramref name="plaintext"/> and returns Base64 text for
    /// <see cref="SmtpSettings.EncryptedPassword"/>.
    /// </summary>
    /// <param name="plaintext">
    /// The password. <c>null</c> or empty is stored as an empty string, which
    /// means "no password configured".
    /// </param>
    /// <exception cref="CryptographicException">DPAPI protection failed.</exception>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext),
            optionalEntropy: null,
            Scope);

        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Reverses <see cref="Protect"/>.
    /// </summary>
    /// <param name="protectedBase64">Base64 text previously returned by <see cref="Protect"/>.</param>
    /// <returns>The original password, or an empty string when nothing was stored.</returns>
    /// <exception cref="FormatException"><paramref name="protectedBase64"/> is not valid Base64.</exception>
    /// <exception cref="CryptographicException">
    /// The value was protected by another machine or user, or the data is damaged.
    /// </exception>
    public static string Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
        {
            return string.Empty;
        }

        var protectedBytes = Convert.FromBase64String(protectedBase64);

        return Encoding.UTF8.GetString(
            ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, Scope));
    }
}
