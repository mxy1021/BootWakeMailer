using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class ConfigValidatorTests
{
    private static AppConfig ValidConfig() => new()
    {
        Smtp = new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            SecurityMode = SmtpSecurityMode.StartTls,
            Username = "sender@example.com",
            EncryptedPassword = SecretProtector.Protect("password"),
        },
        FromAddress = "sender@example.com",
        ToAddress = "recipient@example.com",
    };

    [Fact]
    public void Validate_AcceptsACompleteConfiguration()
    {
        Assert.Empty(ConfigValidator.Validate(ValidConfig()));
        Assert.True(ConfigValidator.IsValid(ValidConfig()));
    }

    [Fact]
    public void Validate_AcceptsAnEmptyPasswordBecauseNotEveryRelayAuthenticates()
    {
        var config = ValidConfig();
        config.Smtp.EncryptedPassword = string.Empty;

        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_ReportsAMissingConfiguration()
    {
        var problems = ConfigValidator.Validate(null);

        Assert.Single(problems);
        Assert.False(ConfigValidator.IsValid(null));
    }

    [Fact]
    public void Validate_ReportsAMissingSmtpSection()
    {
        var config = ValidConfig();
        config.Smtp = null!;

        Assert.Contains("SMTP settings are missing.", ConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_ReportsAnEmptyHost()
    {
        var config = ValidConfig();
        config.Smtp.Host = "  ";

        Assert.Contains("SMTP host is required.", ConfigValidator.Validate(config));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Validate_ReportsAnOutOfRangePort(int port)
    {
        var config = ValidConfig();
        config.Smtp.Port = port;

        Assert.Contains("SMTP port must be between 1 and 65535.", ConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_ReportsAnUnknownSecurityMode()
    {
        var config = ValidConfig();
        config.Smtp.SecurityMode = (SmtpSecurityMode)99;

        Assert.Contains("SMTP security mode is not recognized.", ConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_ReportsAnEmptyUsername()
    {
        var config = ValidConfig();
        config.Smtp.Username = string.Empty;

        Assert.Contains("SMTP user name is required.", ConfigValidator.Validate(config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("@example.com")]
    public void Validate_ReportsAnInvalidSenderAddress(string address)
    {
        var config = ValidConfig();
        config.FromAddress = address;

        Assert.Contains("Sender address is not a valid email address.", ConfigValidator.Validate(config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void Validate_ReportsAnInvalidRecipientAddress(string address)
    {
        var config = ValidConfig();
        config.ToAddress = address;

        Assert.Contains("Recipient address is not a valid email address.", ConfigValidator.Validate(config));
    }

    [Fact]
    public void Validate_NeverEchoesTheStoredPassword()
    {
        var config = ValidConfig();
        config.Smtp.Host = string.Empty;
        config.Smtp.Username = string.Empty;
        config.ToAddress = string.Empty;

        var problems = ConfigValidator.Validate(config);

        Assert.NotEmpty(problems);
        foreach (var problem in problems)
        {
            Assert.DoesNotContain("password", problem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(config.Smtp.EncryptedPassword, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Validate_ReportsEveryProblemAtOnce()
    {
        var config = new AppConfig
        {
            Smtp = new SmtpSettings { Port = 0 },
            FromAddress = "x",
            ToAddress = "y",
        };

        var problems = ConfigValidator.Validate(config);

        Assert.Contains("SMTP host is required.", problems);
        Assert.Contains("SMTP port must be between 1 and 65535.", problems);
        Assert.Contains("SMTP user name is required.", problems);
        Assert.Contains("Sender address is not a valid email address.", problems);
        Assert.Contains("Recipient address is not a valid email address.", problems);
    }
}
