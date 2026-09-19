namespace BootWakeMailer.Shared;

/// <summary>
/// Resolves the application data files under <c>%ProgramData%\BootWakeMailer</c>
/// (requirements.md §2, architecture.md §4).
/// </summary>
/// <remarks>
/// The root directory is injected rather than hard-coded so tests can work in a
/// temporary directory instead of the real ProgramData folder. Use
/// <see cref="Default"/> for the production path.
/// </remarks>
public sealed class AppPaths
{
    /// <summary>File name of the single SMTP configuration document.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>File name of the pending notification queue document.</summary>
    public const string QueueFileName = "queue.json";

    /// <summary>File name of the persisted status document.</summary>
    public const string StatusFileName = "status.json";

    /// <param name="rootDirectory">Directory holding the three JSON files.</param>
    /// <exception cref="ArgumentException"><paramref name="rootDirectory"/> is null, empty or whitespace.</exception>
    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
        ConfigFilePath = Path.Combine(RootDirectory, ConfigFileName);
        QueueFilePath = Path.Combine(RootDirectory, QueueFileName);
        StatusFilePath = Path.Combine(RootDirectory, StatusFileName);
    }

    /// <summary><c>%ProgramData%\BootWakeMailer</c>.</summary>
    public static AppPaths Default { get; } = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppConstants.DataDirectoryName));

    /// <summary>Absolute path of the application data directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Absolute path of <c>config.json</c>.</summary>
    public string ConfigFilePath { get; }

    /// <summary>Absolute path of <c>queue.json</c>.</summary>
    public string QueueFilePath { get; }

    /// <summary>Absolute path of <c>status.json</c>.</summary>
    public string StatusFilePath { get; }

    /// <summary>
    /// Creates the application data directory when it does not exist. Safe to call
    /// repeatedly and before any file operation.
    /// </summary>
    public void EnsureRootDirectory() => Directory.CreateDirectory(RootDirectory);
}
