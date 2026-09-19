using System.Text.Json;

namespace BootWakeMailer.Shared;

/// <summary>
/// Reads and writes JSON documents with the atomic replacement sequence required by
/// requirements.md §5 and architecture.md §4: serialize the complete document to a
/// temporary file in the same directory, flush it to disk, then move or replace it
/// over the target. A reader therefore never observes a half-written document.
/// </summary>
public static class AtomicJsonFile
{
    private const string TempFileExtension = ".tmp";

    /// <summary>
    /// Reads and deserializes the document at <paramref name="path"/>.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the file does not exist, so the caller decides whether an
    /// absent file means "not configured yet" or "start from an empty document".
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The file exists but is empty, is not valid JSON, or contains <c>null</c>.
    /// Callers must report this instead of silently treating damaged data as absent
    /// (architecture.md §14.5).
    /// </exception>
    public static T? Read<T>(string path) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        T? document;
        try
        {
            document = JsonSerializer.Deserialize<T>(stream, JsonSettings.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"'{path}' is not a valid {typeof(T).Name} document.", exception);
        }

        return document
            ?? throw new InvalidDataException($"'{path}' does not contain a {typeof(T).Name} document.");
    }

    /// <summary>
    /// Serializes <paramref name="document"/> as UTF-8 and atomically replaces
    /// <paramref name="path"/>. The target directory is created when missing.
    /// </summary>
    /// <remarks>
    /// <see cref="File.Replace(string, string, string?)"/> keeps the destination's
    /// access control entries, which matters because the service and the elevated
    /// configuration tool both write these files.
    /// </remarks>
    public static void Write<T>(string path, T document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}{TempFileExtension}");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonSettings.SerializerOptions);
                // Flush to disk before the swap, so an interrupted write cannot
                // leave the target referencing incomplete content.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            // Replace/Move consume the temporary file. On failure one may be left
            // behind; it is harmless, but the original exception must not be masked.
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temporary file is harmless and will be overwritten or
            // cleaned up by a later write.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
