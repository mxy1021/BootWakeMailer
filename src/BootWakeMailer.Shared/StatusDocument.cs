namespace BootWakeMailer.Shared;

/// <summary>
/// Root document of <c>status.json</c>. It holds only the few values that must
/// survive a process restart; pending count and service state are derived
/// elsewhere (architecture.md §7).
/// </summary>
public sealed class StatusDocument
{
    /// <summary>Schema version of this document.</summary>
    public int SchemaVersion { get; set; } = AppConstants.SchemaVersion;

    /// <summary>
    /// Time of the most recent notification that the SMTP server accepted, UTC.
    /// Changes only after SMTP acceptance (architecture.md §7).
    /// </summary>
    public DateTime? LastSuccessfulSendAtUtc { get; set; }

    /// <summary>
    /// Most recent error of any service operation. A later success does not clear
    /// it; only a newer error replaces it (architecture.md §7).
    /// </summary>
    public StatusError? LastError { get; set; }
}
