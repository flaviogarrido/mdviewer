using MDViewer.Services;

namespace MDViewer.Models;

/// <summary>
/// Archive state of a Reversa folder, computed by
/// <see cref="ReversaArchiveService.Inspect"/> on every scan. Drives the
/// "Archive extraction" and "Restore extraction" menu items (feature 011).
/// </summary>
/// <param name="CanArchive">There is an extraction to archive (RN-01), or the folder names are invalid and clicking explains why.</param>
/// <param name="ArchiveCount">Folders in <c>_reversa_archive/</c>, valid or not: "Restore extraction" is enabled when &gt; 0.</param>
/// <param name="ValidArchiveCount">Folders that can actually be restored (RN-11).</param>
public sealed record ArchiveStatus(bool CanArchive, int ArchiveCount, int ValidArchiveCount)
{
    /// <summary>Nothing to archive, no archive: a failed inspection or a plain folder.</summary>
    public static ArchiveStatus None { get; } = new(false, 0, 0);
}

/// <summary>
/// What archiving the current extraction would do. Shown in the confirmation
/// and recalculated inside the operation itself (R-05).
/// </summary>
/// <param name="Items">Paths relative to the root (with <c>/</c>) that exist and move, in RN-03 order. May be empty with <paramref name="HasExtraction"/> true (only <c>phase</c>).</param>
/// <param name="FileCount">Files inside <paramref name="Items"/>.</param>
/// <param name="IsUnfinished"><c>pending</c> is not empty, or <c>.reversa/plan.md</c> has a <c>- [ ]</c> task (RN-07).</param>
/// <param name="Destination">Archive folder that would be created, relative to the root, ending with <c>/</c>.</param>
/// <param name="Error">Why nothing can be done (e.g. a folder name pointing outside the root); null when the plan can run.</param>
/// <param name="HasExtraction">There is something to archive. Decides, never the size of <paramref name="Items"/>.</param>
public sealed record ArchivePlan(
    IReadOnlyList<string> Items, int FileCount, bool IsUnfinished, string Destination, string? Error, bool HasExtraction);

/// <summary>One folder of <c>_reversa_archive/</c>, as listed in the restore window.</summary>
/// <param name="FullPath">The archive folder.</param>
/// <param name="Name">Folder name (<c>yyyyMMdd-HHmmss[-N]</c> when created by the app).</param>
/// <param name="CreatedAt">From the manifest, else parsed from the name; null when neither works.</param>
/// <param name="Phase"><c>phase</c> of the archived <c>state.json</c>.</param>
/// <param name="FeatureCount"><c>NNN-*</c> folders in the archived forward folder.</param>
/// <param name="IsValid">Can be restored (RN-11).</param>
/// <param name="InvalidReason">Why not, in English, for the list.</param>
public sealed record ArchiveEntry(
    string FullPath, string Name, DateTime? CreatedAt, string? Phase, int FeatureCount, bool IsValid, string? InvalidReason);

/// <summary>What restoring an archive would do (D-21). Recalculated inside the restore.</summary>
/// <param name="Archive">The chosen archive; null when it no longer exists.</param>
/// <param name="Items">Manifest <c>source</c> paths that come back, in manifest order.</param>
/// <param name="FileCount">Files that come back.</param>
/// <param name="Current">The current extraction, archived first; null when there is none.</param>
/// <param name="Error">First failed precondition, with the final English message.</param>
public sealed record RestorePlan(
    ArchiveEntry? Archive, IReadOnlyList<string> Items, int FileCount, ArchivePlan? Current, string? Error);

/// <summary>Result of archiving or restoring.</summary>
/// <param name="Success">The all-or-nothing block completed.</param>
/// <param name="ArchivePath">Archive folder created (archive) or restored (restore).</param>
/// <param name="PathMap">Absolute path prefixes that moved, first match wins: remaps the history and the open document.</param>
/// <param name="Warnings">Secondary problems that did not undo the operation.</param>
/// <param name="Error">Full failure message (with the stranded items, if any); null on success.</param>
/// <param name="Stranded">Items the rollback could not put back: where they are and where they belong.</param>
/// <param name="PreviousArchivePath">Restore only: where the current extraction was archived first, if it was.</param>
public sealed record ArchiveOutcome(
    bool Success,
    string? ArchivePath,
    IReadOnlyList<(string From, string To)> PathMap,
    IReadOnlyList<string> Warnings,
    string? Error,
    IReadOnlyList<(string Current, string Original)> Stranded,
    string? PreviousArchivePath = null)
{
    public static ArchiveOutcome Failed(string error, IReadOnlyList<(string Current, string Original)>? stranded = null) =>
        new(false, null, Array.Empty<(string, string)>(), Array.Empty<string>(), error,
            stranded ?? Array.Empty<(string, string)>());
}
