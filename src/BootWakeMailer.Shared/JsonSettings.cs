using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BootWakeMailer.Shared;

/// <summary>
/// The single serializer configuration used for every JSON file, so that
/// <c>config.json</c>, <c>queue.json</c> and <c>status.json</c> all share the
/// same casing, enum and timestamp format (requirements.md §5).
/// </summary>
public static class JsonSettings
{
    /// <summary>Options passed to every <see cref="JsonSerializer"/> call.</summary>
    /// <remarks>
    /// Timestamps are written as UTC ISO 8601 with a <c>Z</c> suffix because the
    /// models use <see cref="DateTime"/> with <see cref="DateTimeKind.Utc"/>.
    /// Enums are written by name so the files stay readable and version tolerant.
    /// </remarks>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            // Keep non-ASCII characters (for example a localized computer name)
            // readable instead of escaping them as \uXXXX.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Reflection-based metadata. Named explicitly because MakeReadOnly
            // requires a resolver to be set.
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        options.Converters.Add(new JsonStringEnumConverter());

        // Freeze the instance so later modification cannot affect documents that
        // were already written, and so all future serializations reuse the same
        // cached metadata.
        options.MakeReadOnly();
        return options;
    }
}
