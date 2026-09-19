using BootWakeMailer.Shared;

namespace BootWakeMailer.Tests;

/// <summary>
/// The service writes <c>queue.json</c> and <c>status.json</c> while the configuration
/// tool reads them (requirements.md §5), so a read in progress must not break an atomic
/// write and a write in progress must not break a read.
/// </summary>
public class AtomicJsonConcurrencyTests
{
    [Fact]
    public void Save_SucceedsWhileTheSameFileIsOpenForReading()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        QueueStore.Save(path, QueueWithOneItem());

        // The sharing mode AtomicJsonFile.Read uses, standing in for the configuration
        // tool reading queue.json while the service rewrites it.
        using (var reader = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            QueueStore.Save(path, QueueWithOneItem());
        }

        Assert.Single(QueueStore.Load(path).Items);
    }

    [Fact]
    public async Task Load_AlwaysSeesACompleteDocumentWhileTheFileIsReplaced()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        QueueStore.Save(path, QueueWithOneItem());

        using var cancellation = new CancellationTokenSource();

        var writer = Task.Run(
            () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    QueueStore.Save(path, QueueWithOneItem());
                }
            },
            CancellationToken.None);

        for (var i = 0; i < 300; i++)
        {
            // Publishing a new version briefly removes the target name, so a reader must
            // retry instead of reporting an empty queue (architecture.md §14.5).
            Assert.Single(QueueStore.Load(path).Items);
        }

        await cancellation.CancelAsync();
        await writer;
    }

    [Fact]
    public void Save_FailsWithoutDamagingTheFileWhenAReaderDeniesDeletion()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");
        QueueStore.Save(path, QueueWithOneItem());
        var before = File.ReadAllText(path);

        // A handle that does not allow deletion cannot be replaced, however often the
        // service retries. The failure must be reported to the caller, and the queue on
        // disk must be left exactly as it was.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => QueueStore.Save(path, QueueWithOneItem()));
        }

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(["queue.json"], FileNames(temp.Root));
    }

    [Fact]
    public void Load_TreatsAMissingFileAsAbsentAfterRetrying()
    {
        using var temp = new TempDirectory();
        var path = temp.File("queue.json");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var loaded = AtomicJsonFile.Read<QueueDocument>(path);
        stopwatch.Stop();

        Assert.Null(loaded);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Retrying for a file that was never written must stay short, but took {stopwatch.Elapsed}.");
    }

    private static string[] FileNames(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>().Order().ToArray();

    private static QueueDocument QueueWithOneItem() =>
        new() { Items = [PendingMailEvent.Create(MailEventType.Startup, "ZEN-PC")] };
}
