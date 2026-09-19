using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

public class AtomicJsonFileTests
{
    [Fact]
    public void WriteThenRead_RoundTripsTheDocument()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        var original = new QueueDocument
        {
            Items =
            [
                PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC", new DateTime(2026, 9, 19, 6, 30, 0, DateTimeKind.Utc)),
            ],
        };

        AtomicJsonFile.Write(path, original);
        var loaded = AtomicJsonFile.Read<QueueDocument>(path);

        Assert.NotNull(loaded);
        var reloaded = Assert.Single(loaded.Items);
        Assert.Equal("ZEN-PC", reloaded.ComputerName);
        Assert.Equal(MailEventType.Startup, reloaded.EventType);
        Assert.Equal(original.Items[0].OccurredAtUtc, reloaded.OccurredAtUtc);
    }

    [Fact]
    public void Read_ReturnsNullWhenTheFileDoesNotExist()
    {
        using var temp = new TempDirectory();

        Assert.Null(AtomicJsonFile.Read<QueueDocument>(temp.File("missing.json")));
    }

    [Fact]
    public void Read_ThrowsWhenTheFileIsNotValidJson()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.Throws<InvalidDataException>(() => AtomicJsonFile.Read<QueueDocument>(path));
    }

    [Fact]
    public void Read_ThrowsWhenTheFileContainsJsonNull()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        File.WriteAllText(path, "null");

        // A file that exists but holds no document must not be mistaken for "missing".
        Assert.Throws<InvalidDataException>(() => AtomicJsonFile.Read<QueueDocument>(path));
    }

    [Fact]
    public void Read_ThrowsWhenTheFileIsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        File.WriteAllBytes(path, []);

        Assert.Throws<InvalidDataException>(() => AtomicJsonFile.Read<QueueDocument>(path));
    }

    [Fact]
    public void Write_CreatesTheTargetDirectoryWhenMissing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "nested", "deeper", "status.json");

        AtomicJsonFile.Write(path, new StatusDocument());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Write_ProducesUtf8WithoutByteOrderMark()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");

        AtomicJsonFile.Write(path, new StatusDocument());

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3);
        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "The file must not start with a UTF-8 BOM.");
    }

    [Fact]
    public void Write_ReplacesExistingContentInsteadOfAppending()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");

        AtomicJsonFile.Write(path, new StatusDocument { LastSuccessfulSendAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        AtomicJsonFile.Write(path, new StatusDocument { LastSuccessfulSendAtUtc = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc) });

        var loaded = AtomicJsonFile.Read<StatusDocument>(path);

        Assert.NotNull(loaded);
        Assert.Equal(new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc), loaded.LastSuccessfulSendAtUtc);
    }

    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        AtomicJsonFile.Write(path, new QueueDocument());
        AtomicJsonFile.Write(path, new QueueDocument());

        Assert.Equal(new[] { "queue.json" }, OnlyFileNames(temp.Root));
    }

    [Fact]
    public void Write_LeavesTheOriginalFileIntactWhenSerializationFails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("status.json");

        var good = new StatusDocument { LastSuccessfulSendAtUtc = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc) };
        AtomicJsonFile.Write(path, good);

        // A document that cannot be serialized must not damage the existing file.
        Assert.ThrowsAny<Exception>(() => AtomicJsonFile.Write(path, new UnserializableDocument()));

        var reloaded = AtomicJsonFile.Read<StatusDocument>(path);
        Assert.NotNull(reloaded);
        Assert.Equal(good.LastSuccessfulSendAtUtc, reloaded.LastSuccessfulSendAtUtc);
        Assert.Equal(new[] { "status.json" }, OnlyFileNames(temp.Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadAndWrite_RejectBlankPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => AtomicJsonFile.Read<QueueDocument>(path));
        Assert.Throws<ArgumentException>(() => AtomicJsonFile.Write(path, new QueueDocument()));
    }

    private static string[] OnlyFileNames(string directory) =>
        Directory.GetFiles(directory).Select(f => Path.GetFileName(f)!).Order().ToArray();

    /// <summary>A type with no usable representation, used to force a serialization failure.</summary>
    private sealed class UnserializableDocument
    {
        public UnserializableDocument Self => this;
    }
}
