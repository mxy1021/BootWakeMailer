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

    /// <summary>Attempts to open the file while a writer is replacing it.</summary>
    private const int OpenAttempts = 5;

    /// <summary>
    /// Pause between open attempts. The absent window a swap opens is measured in
    /// microseconds, so this budget is already generous; keeping it short matters because
    /// it is also spent on a file that genuinely does not exist yet.
    /// </summary>
    private const int OpenRetryDelayMilliseconds = 5;

    /// <summary>Attempts to swap the new version over the target.</summary>
    private const int SwapAttempts = 5;

    /// <summary>
    /// Pause between swap attempts. A reader that is merely slow to release the file has
    /// this long to do so before the write is reported as failed.
    /// </summary>
    private const int SwapRetryDelayMilliseconds = 50;

    /// <summary>Win32 ERROR_SHARING_VIOLATION: the file is open by another handle.</summary>
    private const int ErrorSharingViolation = 32;

    /// <summary>Win32 ERROR_LOCK_VIOLATION: a region of the file is locked.</summary>
    private const int ErrorLockViolation = 33;

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

        // Absence is decided by the open attempt rather than by File.Exists, which reports
        // false whenever it cannot determine the answer — exactly what happens while
        // another handle is replacing the file.
        var stream = Open(path);

        if (stream is null)
        {
            return null;
        }

        using (stream)
        {
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
    }

    /// <summary>
    /// Opens the file for a complete read, retrying briefly while a writer replaces it.
    /// </summary>
    /// <returns><c>null</c> when the file does not exist.</returns>
    /// <remarks>
    /// <see cref="File.Replace(string, string, string?)"/> publishes the new content by
    /// renaming over the target, so for a very short moment the target name is absent and
    /// an open by name fails with <see cref="FileNotFoundException"/>. Without a retry a
    /// reader would mistake that instant for "nothing has been written yet" and report an
    /// empty queue. A reader that is still blocked after the retries gets the real I/O
    /// error instead of a misleading empty document.
    /// </remarks>
    private static FileStream? Open(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // FileShare.Delete is required, not optional: the Windows Service rewrites
                // these files with File.Replace while the configuration tool may be reading
                // them (requirements.md §5). ReplaceFile fails with a sharing violation
                // unless the reader allows the destination to be deleted, which would turn
                // a read in the other process into a failed write in the service. Allowing
                // write sharing is safe because writers never modify a file in place, they
                // only replace it.
                return new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (DirectoryNotFoundException)
            {
                // The application data directory has not been created yet.
                return null;
            }
            catch (Exception exception) when (attempt < OpenAttempts && IsTransientOpenFailure(exception))
            {
                Thread.Sleep(OpenRetryDelayMilliseconds);
            }
            catch (FileNotFoundException)
            {
                // Still absent after the retries: nothing has been written yet.
                return null;
            }
        }
    }

    private static bool IsTransientOpenFailure(Exception exception) =>
        exception is FileNotFoundException || (exception is IOException io && IsSharingViolation(io));

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

            Swap(tempPath, fullPath);
        }
        finally
        {
            // Replace/Move consume the temporary file. On failure one may be left
            // behind; it is harmless, but the original exception must not be masked.
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Moves the fully written temporary file over the target.
    /// </summary>
    /// <remarks>
    /// A sharing violation is retried briefly. The service's own readers allow deletion
    /// and never block this, but an external tool or editor holding the file open would
    /// otherwise fail a queue write that is perfectly retryable. The retry is bounded, so
    /// a genuinely inaccessible file still surfaces as an error the caller can record.
    /// </remarks>
    private static void Swap(string tempPath, string fullPath)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                MoveOver(tempPath, fullPath);
                return;
            }
            catch (IOException exception) when (attempt < SwapAttempts && IsSharingViolation(exception))
            {
                Thread.Sleep(SwapRetryDelayMilliseconds);
            }
        }
    }

    /// <summary>Puts the fully written temporary file in place of the target.</summary>
    private static void MoveOver(string tempPath, string fullPath)
    {
        try
        {
            File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (FileNotFoundException)
        {
            // The target does not exist yet, so this is the first write of the file.
            File.Move(tempPath, fullPath);
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

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
