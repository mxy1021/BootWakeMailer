namespace BootWakeMailer.Shared;

/// <summary>
/// Reads and writes <c>status.json</c> (architecture.md §7).
/// </summary>
/// <remarks>
/// The Windows Service is the only writer; the configuration tool reads it to show
/// the latest successful send time and the latest error.
/// </remarks>
public static class StatusStore
{
    /// <summary>
    /// Loads the status document.
    /// </summary>
    /// <returns>
    /// An empty status when the file does not exist. Status is advisory, so a
    /// missing file is treated as "nothing recorded yet" rather than an error.
    /// </returns>
    /// <exception cref="InvalidDataException">The file exists but is corrupt.</exception>
    public static StatusDocument Load(string path) =>
        AtomicJsonFile.Read<StatusDocument>(path) ?? new StatusDocument();

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="status"/>.</summary>
    public static void Save(string path, StatusDocument status)
    {
        ArgumentNullException.ThrowIfNull(status);

        AtomicJsonFile.Write(path, status);
    }
}
