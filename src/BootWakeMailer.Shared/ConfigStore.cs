namespace BootWakeMailer.Shared;

/// <summary>
/// Reads and writes <c>config.json</c> (architecture.md §5).
/// </summary>
/// <remarks>
/// The configuration tool is the writer; the Windows Service reads the current file
/// before every send attempt, so a pending task automatically uses the latest saved
/// settings (architecture.md §12).
/// </remarks>
public static class ConfigStore
{
    /// <summary>
    /// Loads the configuration.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the file does not exist, meaning "not configured yet".
    /// </returns>
    /// <exception cref="InvalidDataException">The file exists but is not readable configuration.</exception>
    public static AppConfig? Load(string path) => AtomicJsonFile.Read<AppConfig>(path);

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="config"/>.</summary>
    public static void Save(string path, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        AtomicJsonFile.Write(path, config);
    }
}
