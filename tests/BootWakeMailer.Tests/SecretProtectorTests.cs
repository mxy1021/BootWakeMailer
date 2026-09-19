using System.Security.Cryptography;
using System.Text;
using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// DPAPI tests. These exercise the real Windows DPAPI in machine scope
/// (<see cref="DataProtectionScope.LocalMachine"/>), so they only run on Windows.
/// </summary>
public class SecretProtectorTests
{
    [Theory]
    [InlineData("s3cret-password")]
    [InlineData("with spaces and symbols !@#$%^&*()")]
    [InlineData("密码パスワード")]
    [InlineData("a")]
    public void ProtectThenUnprotect_ReturnsTheOriginalPassword(string password)
    {
        var protectedValue = SecretProtector.Protect(password);

        Assert.Equal(password, SecretProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Protect_ReturnsBase64ThatDoesNotContainThePlaintext()
    {
        const string password = "SuperSecret123";

        var protectedValue = SecretProtector.Protect(password);

        Assert.NotEqual(password, protectedValue);
        Assert.DoesNotContain(password, protectedValue, StringComparison.Ordinal);

        // Must be valid Base64, because config.json stores it as text.
        var bytes = Convert.FromBase64String(protectedValue);
        Assert.NotEmpty(bytes);

        // The raw encrypted bytes must not contain the plaintext either.
        Assert.True(
            bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) < 0,
            "The DPAPI output must not contain the plaintext password.");
    }

    [Fact]
    public void Protect_ProducesADifferentValueEachTime()
    {
        const string password = "SameInput";

        Assert.NotEqual(SecretProtector.Protect(password), SecretProtector.Protect(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_StoresEmptyInputAsEmptyString(string? password)
    {
        Assert.Equal(string.Empty, SecretProtector.Protect(password));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unprotect_ReturnsEmptyStringForEmptyInput(string? stored)
    {
        Assert.Equal(string.Empty, SecretProtector.Unprotect(stored));
    }

    [Fact]
    public void Unprotect_ThrowsFormatExceptionForNonBase64Input()
    {
        Assert.Throws<FormatException>(() => SecretProtector.Unprotect("not base64 !!!"));
    }

    [Fact]
    public void Unprotect_ThrowsCryptographicExceptionForBase64ThatIsNotDpapiData()
    {
        var notDpapi = Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text pretending to be protected"));

        Assert.Throws<CryptographicException>(() => SecretProtector.Unprotect(notDpapi));
    }

    [Fact]
    public void Unprotect_ThrowsCryptographicExceptionForTamperedProtectedData()
    {
        var protectedValue = Convert.FromBase64String(SecretProtector.Protect("original"));
        protectedValue[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(
            () => SecretProtector.Unprotect(Convert.ToBase64String(protectedValue)));
    }
}
