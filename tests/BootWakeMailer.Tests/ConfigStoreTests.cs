using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class ConfigStoreTests
{
    private static AppConfig SampleConfig() => new()
    {
        Smtp = new SmtpSettings
        {
            Host = "smtp.example.com",
            Port = 587,
            SecurityMode = SmtpSecurityMode.StartTls,
            Username = "sender@example.com",
            EncryptedPassword = SecretProtector.Protect("hunter2"),
        },
        FromAddress = "sender@example.com",
        ToAddress = "recipient@example.com",
    };

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        ConfigStore.Save(path, SampleConfig());
        var loaded = ConfigStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal("smtp.example.com", loaded.Smtp.Host);
        Assert.Equal(587, loaded.Smtp.Port);
        Assert.Equal(SmtpSecurityMode.StartTls, loaded.Smtp.SecurityMode);
        Assert.Equal("sender@example.com", loaded.Smtp.Username);
        Assert.Equal("sender@example.com", loaded.FromAddress);
        Assert.Equal("recipient@example.com", loaded.ToAddress);
        Assert.Equal("hunter2", SecretProtector.Unprotect(loaded.Smtp.EncryptedPassword));
    }

    [Fact]
    public void Load_ReturnsNullWhenTheFileDoesNotExist()
    {
        using var temp = new TempDirectory();

        Assert.Null(ConfigStore.Load(temp.File("config.json")));
    }

    [Fact]
    public void Save_WritesThePlaintextPasswordNowhereInTheFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        ConfigStore.Save(path, SampleConfig());
        var text = File.ReadAllText(path);

        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains("encryptedPassword", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_WritesTheDocumentShapeFromTheArchitecture()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        ConfigStore.Save(path, SampleConfig());
        var text = File.ReadAllText(path);

        Assert.Contains("\"schemaVersion\": 1", text, StringComparison.Ordinal);
        Assert.Contains("\"smtp\":", text, StringComparison.Ordinal);
        Assert.Contains("\"host\":", text, StringComparison.Ordinal);
        Assert.Contains("\"securityMode\": \"StartTls\"", text, StringComparison.Ordinal);
        Assert.Contains("\"fromAddress\":", text, StringComparison.Ordinal);
        Assert.Contains("\"toAddress\":", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsWhenTheFileIsCorrupt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, "{ \"smtp\": ");

        Assert.Throws<InvalidDataException>(() => ConfigStore.Load(path));
    }

    [Fact]
    public void Load_ThrowsWhenTheFileIsUnreadableAsConfiguration()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");
        File.WriteAllText(path, "[1, 2, 3]");

        Assert.Throws<InvalidDataException>(() => ConfigStore.Load(path));
    }

    [Fact]
    public void Save_RejectsNullConfig()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentNullException>(() => ConfigStore.Save(temp.File("config.json"), null!));
    }

    [Fact]
    public void Save_DoesNotLeaveTheOldFileWhenReplacing()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.json");

        ConfigStore.Save(path, SampleConfig());
        var updated = SampleConfig();
        updated.ToAddress = "other@example.com";
        ConfigStore.Save(path, updated);

        var loaded = ConfigStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("other@example.com", loaded.ToAddress);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
