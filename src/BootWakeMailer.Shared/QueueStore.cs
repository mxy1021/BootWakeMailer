namespace BootWakeMailer.Shared;

/// <summary>
/// Reads and writes <c>queue.json</c> (architecture.md §6).
/// </summary>
/// <remarks>
/// The Windows Service is the only process that modifies this file; the
/// configuration tool only reads it to show the pending count
/// (requirements.md §5).
/// </remarks>
public static class QueueStore
{
    /// <summary>
    /// Loads the pending queue.
    /// </summary>
    /// <returns>
    /// An empty queue when the file does not exist, which is a normal first-run state.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The file exists but is corrupt. It is deliberately not replaced with an empty
    /// queue: silently discarding a damaged queue would lose pending notifications
    /// (architecture.md §14.5). The caller records the error and retries later.
    /// </exception>
    public static QueueDocument Load(string path)
    {
        var document = AtomicJsonFile.Read<QueueDocument>(path);

        if (document is null)
        {
            return new QueueDocument();
        }

        // Guard against a hand-edited or partially written "items": null document.
        document.Items ??= [];
        return document;
    }

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="queue"/>.</summary>
    public static void Save(string path, QueueDocument queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        AtomicJsonFile.Write(path, queue);
    }
}
