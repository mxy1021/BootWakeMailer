namespace BootWakeMailer.Tests;

/// <summary>
/// A throwaway directory used as the application data folder for a single test, so
/// the suite never touches the real <c>%ProgramData%\BootWakeMailer</c>.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "BootWakeMailer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute path of the created directory.</summary>
    public string Root { get; }

    /// <summary>Combines <paramref name="fileName"/> with <see cref="Root"/>.</summary>
    public string File(string fileName) => Path.Combine(Root, fileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temporary directory must not fail an otherwise passing test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
