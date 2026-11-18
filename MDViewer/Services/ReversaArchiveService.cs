using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MDViewer.Models;

namespace MDViewer.Services;

/// <summary>
/// Archives and restores the Reversa extraction of a root folder (feature 011).
/// The only class of the app that writes to the open folder (D-01), and only
/// through <see cref="Archive"/> and <see cref="Restore"/>, after the user
/// confirmed. Both are all or nothing: every move goes through a journal that
/// is undone in reverse order when a step fails (D-03). No WPF: testable with
/// a scratch folder.
/// </summary>
public static class ReversaArchiveService
{
    public const string ArchiveFolderName = "_reversa_archive";
    public const string ManifestName = "reversa-archive.json";

    private const string DefaultOutputFolder = "_reversa_sdd";
    private const string DefaultForwardFolder = "_reversa_forward";
    private const string ReversaFolder = ".reversa";
    private const string StateRelative = ".reversa/state.json";
    private const long MaxJsonBytes = 1024 * 1024;
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private const int MaxLockProbeFiles = 10_000;

    private static readonly string[] Satellites = { "_reversa_docs", "_reversa_bugs", "_reversa_refactor" };

    /// <summary>RN-03 (d): the only entries of <c>.reversa/</c> that belong to an extraction.</summary>
    private static readonly (string Relative, bool IsDirectory)[] ReversaEntries =
    {
        (".reversa/context", true),
        (".reversa/plan.md", false),
        (".reversa/active-requirements.json", false),
        (".reversa/forward-autonomous.json", false),
    };

    /// <summary>D-06: extraction progress. Every other key of state.json is installation data or a preference.</summary>
    private static readonly string[] ProgressKeys =
    {
        "phase", "completed", "pending", "checkpoints", "redator_progress", "regression_check", "autonomous_run"
    };

    private static readonly string[] AgentProgressKeys = { "redator_progress", "regression_check", "autonomous_run" };

    private static readonly string[] GitignoreLines =
    {
        "_reversa_archive", "_reversa_archive/", "/_reversa_archive", "/_reversa_archive/"
    };

    /// <summary>A Markdown task still open: "- [ ] …" (RN-07, D-08).</summary>
    private static readonly Regex OpenTask = new(@"^\s*[-*+]\s+\[ \]", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex FeatureFolder = new(@"^\d+-", RegexOptions.Compiled);

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    #region Inspect / PlanArchive

    /// <summary>
    /// Archive and restore availability, for the menu (D-18). Never writes.
    /// Runs on a background thread, on every scan.
    /// </summary>
    public static ArchiveStatus Inspect(string root)
    {
        root = Normalize(root);
        var plan = BuildPlan(root, DateTime.Now, countFiles: false);

        int count = 0, valid = 0;
        var archiveRoot = Full(root, ArchiveFolderName);
        if (Directory.Exists(archiveRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(archiveRoot))
            {
                count++;
                if (Validate(dir).Entry.IsValid) valid++;
            }
        }

        // With an Error the item stays enabled: the click explains what is wrong.
        return new ArchiveStatus(plan.HasExtraction || plan.Error != null, count, valid);
    }

    /// <summary>What <see cref="Archive"/> would do at <paramref name="now"/>. Never writes.</summary>
    public static ArchivePlan PlanArchive(string root, DateTime now) =>
        BuildPlan(Normalize(root), now, countFiles: true);

    private static ArchivePlan BuildPlan(string root, DateTime now, bool countFiles)
    {
        var state = ReadJsonObject(StatePath(root));
        var folders = ResolveFolders(state);

        var items = new List<string>();
        var fileCount = 0;
        var anyFile = false;
        foreach (var (relative, isDirectory) in Candidates(folders))
        {
            var full = Full(root, relative);
            if (!ExistsAs(full, isDirectory)) continue;

            items.Add(relative);
            if (countFiles)
                fileCount += CountFiles(full, isDirectory);
            else
                anyFile = anyFile || HasFiles(full, isDirectory);
        }

        var hasPhase = state?["phase"] != null;
        return new ArchivePlan(
            items,
            fileCount,
            IsUnfinished(root, state),
            $"{ArchiveFolderName}/{FreeArchiveName(root, now)}/",
            folders.Problem != null ? $"Cannot archive: {folders.Problem}." : null,
            HasExtraction: anyFile || fileCount > 0 || hasPhase);
    }

    /// <summary>RN-03 candidates in order: extraction, forward, satellites, then the .reversa entries.</summary>
    private static IEnumerable<(string Relative, bool IsDirectory)> Candidates(Folders folders)
    {
        yield return (folders.Output ?? DefaultOutputFolder, true);
        yield return (folders.Forward ?? DefaultForwardFolder, true);
        foreach (var satellite in Satellites)
            yield return (satellite, true);
        foreach (var entry in ReversaEntries)
            yield return entry;
    }

    /// <summary>RN-07: <c>pending</c> not empty, or an open task in <c>.reversa/plan.md</c>.</summary>
    private static bool IsUnfinished(string root, JsonObject? state)
    {
        if (state?["pending"] is JsonArray { Count: > 0 }) return true;

        var plan = Full(root, ".reversa/plan.md");
        try
        {
            var info = new FileInfo(plan);
            return info.Exists && info.Length <= MaxJsonBytes && OpenTask.IsMatch(File.ReadAllText(plan));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>First free <c>yyyyMMdd-HHmmss[-N]</c> name in the archive folder (RN-02, D-20).</summary>
    private static string FreeArchiveName(string root, DateTime now)
    {
        var stamp = now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var archiveRoot = Full(root, ArchiveFolderName);
        var name = stamp;
        for (var n = 2; Exists(Path.Combine(archiveRoot, name)); n++)
            name = $"{stamp}-{n}";
        return name;
    }

    #endregion

    #region Archive

    /// <summary>
    /// Moves the current extraction to a new archive folder and resets the
    /// progress in state.json (RN-02 to RN-05). The plan is recalculated here
    /// (R-05). On failure everything is moved back.
    /// </summary>
    public static ArchiveOutcome Archive(string root, DateTime now)
    {
        root = Normalize(root);
        var plan = PlanArchive(root, now);
        if (plan.Error != null) return ArchiveOutcome.Failed(plan.Error);
        if (!plan.HasExtraction) return ArchiveOutcome.Failed("No extraction to archive.");

        var journal = new ArchiveJournal(root);
        var pathMap = new List<(string From, string To)>();
        var warnings = new List<string>();
        string archiveDir;
        try
        {
            archiveDir = ArchiveCurrent(root, plan, now, journal, pathMap);
            ResetState(root, warnings);
        }
        catch (StepException ex)
        {
            return Fail("Archiving failed", ex, journal, root);
        }

        UpdateGitignore(root, warnings);
        return new ArchiveOutcome(true, archiveDir, pathMap, warnings, null, Array.Empty<(string, string)>());
    }

    /// <summary>
    /// The archive steps shared by <see cref="Archive"/> and the first step of
    /// <see cref="Restore"/>: create the folder, move each item, copy
    /// state.json byte for byte, and write the manifest last (D-05). Throws
    /// <see cref="StepException"/>; the caller rolls the journal back.
    /// </summary>
    private static string ArchiveCurrent(
        string root, ArchivePlan plan, DateTime now, ArchiveJournal journal, List<(string From, string To)> pathMap)
    {
        var archiveRoot = Full(root, ArchiveFolderName);
        journal.EnsureDirectory(archiveRoot);
        var archiveDir = Path.Combine(archiveRoot, FreeArchiveName(root, now));
        journal.EnsureDirectory(archiveDir);

        var moved = new List<(string Source, bool IsDirectory)>();
        foreach (var relative in plan.Items)
        {
            var from = Full(root, relative);
            var to = Full(archiveDir, relative);
            var isDirectory = Directory.Exists(from);
            if (isDirectory)
                journal.MoveDirectory(from, to);
            else
                journal.MoveFile(from, to);
            moved.Add((relative, isDirectory));
            pathMap.Add((from, to));
        }

        var statePath = StatePath(root);
        if (File.Exists(statePath))
            journal.CopyFile(statePath, Full(archiveDir, StateRelative));

        var state = ReadJsonObject(statePath);
        var folders = ResolveFolders(state);
        var manifest = new JsonObject
        {
            ["schema-version"] = 1,
            ["created-at"] = new DateTimeOffset(now).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["app"] = "MDViewer",
            ["reversa-version"] = state?["version"]?.DeepClone(),
            ["phase"] = state?["phase"]?.DeepClone(),
            ["output-folder"] = folders.Output ?? DefaultOutputFolder,
            ["forward-folder"] = folders.Forward ?? DefaultForwardFolder,
            ["state-copy"] = StateRelative,
            ["items"] = new JsonArray(moved
                .Select(m => (JsonNode?)new JsonObject
                {
                    ["source"] = m.Source,
                    ["kind"] = m.IsDirectory ? "directory" : "file"
                })
                .ToArray()),
        };
        journal.WriteNewFile(Path.Combine(archiveDir, ManifestName), manifest.ToJsonString(JsonWriteOptions));
        return archiveDir;
    }

    /// <summary>
    /// D-06: progress keys reset, everything else kept. An unreadable
    /// state.json is left alone with a warning (RNF Robustness); a failed
    /// write throws, so the archive is undone.
    /// </summary>
    private static void ResetState(string root, List<string> warnings)
    {
        var statePath = StatePath(root);
        var state = ReadJsonObject(statePath);
        if (state == null)
        {
            if (File.Exists(statePath))
                warnings.Add("The .reversa/state.json file could not be read, so it was not reset. " +
                             "The next /reversa may resume the archived extraction.");
            return;
        }

        state["phase"] = null;
        state["completed"] = new JsonArray();
        state["pending"] = new JsonArray();
        state["checkpoints"] = new JsonObject();
        foreach (var key in AgentProgressKeys)
            state.Remove(key);

        WriteState(root, state);
    }

    #endregion

    #region ListArchives

    /// <summary>Every folder of <c>_reversa_archive/</c>, most recent first, valid or not (RN-11).</summary>
    public static IReadOnlyList<ArchiveEntry> ListArchives(string root)
    {
        var archiveRoot = Full(Normalize(root), ArchiveFolderName);
        if (!Directory.Exists(archiveRoot)) return Array.Empty<ArchiveEntry>();

        return Directory.EnumerateDirectories(archiveRoot)
            .Select(dir => Validate(dir).Entry)
            .OrderByDescending(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record Validation(ArchiveEntry Entry, List<(string Source, bool IsDirectory)> Items, JsonObject? StateCopy);

    /// <summary>D-10 / interfaces §3: manifest, state copy and every item checked against the disk.</summary>
    private static Validation Validate(string dir)
    {
        var name = Path.GetFileName(dir);
        DateTime? createdAt = DateTime.TryParseExact(
            name.Length >= StampFormat.Length ? name[..StampFormat.Length] : name,
            StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromName)
            ? fromName
            : null;

        var stateCopy = ReadJsonObject(Full(dir, StateRelative));
        string? phase = null;
        var featureCount = 0;
        var copyFolders = ResolveFolders(stateCopy);
        if (stateCopy != null)
        {
            phase = GetString(stateCopy, "phase");
            if (copyFolders.Forward != null)
                featureCount = CountFeatures(Full(dir, copyFolders.Forward));
        }

        var items = new List<(string Source, bool IsDirectory)>();
        string? reason;
        var manifestPath = Path.Combine(dir, ManifestName);
        var manifest = File.Exists(manifestPath) ? ReadJsonObject(manifestPath) : null;
        if (!File.Exists(manifestPath))
        {
            reason = $"No {ManifestName}";
        }
        else if (manifest == null)
        {
            reason = $"{ManifestName} is not valid JSON";
        }
        else
        {
            if (GetString(manifest, "created-at") is { } created
                && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                createdAt = stamp.LocalDateTime;

            reason = !IsSchemaVersion1(manifest["schema-version"]) ? "Unsupported schema-version"
                : GetString(manifest, "state-copy") != StateRelative ? "Invalid state-copy"
                : !File.Exists(Full(dir, StateRelative)) ? "No .reversa/state.json copy"
                : stateCopy == null ? "state.json copy is not valid JSON"
                : ReadItems(manifest["items"], dir, copyFolders, items);
        }

        var entry = new ArchiveEntry(dir, name, createdAt, phase, featureCount, reason == null, reason);
        return new Validation(entry, items, stateCopy);
    }

    private static bool IsSchemaVersion1(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && value.TryGetValue<int>(out var version) && version == 1;

    /// <summary>
    /// Fills <paramref name="items"/> from the manifest. Each source must be
    /// one of the RN-03 paths (written exactly, with <c>/</c>), once, with the
    /// declared kind on disk. An empty list is valid (phase-only archive).
    /// Returns the reason when invalid.
    /// </summary>
    private static string? ReadItems(JsonNode? node, string dir, Folders copyFolders,
        List<(string Source, bool IsDirectory)> items)
    {
        if (node is not JsonArray array) return "items is missing";

        var allowed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (copyFolders.Output != null) allowed[copyFolders.Output] = true;
        if (copyFolders.Forward != null) allowed[copyFolders.Forward] = true;
        foreach (var satellite in Satellites) allowed[satellite] = true;
        foreach (var (relative, isDirectory) in ReversaEntries) allowed[relative] = isDirectory;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array)
        {
            if (item is not JsonObject obj || GetString(obj, "source") is not { } source || GetString(obj, "kind") is not { } kind)
                return "An item has no source or kind";
            if (!allowed.TryGetValue(source, out var expectedDirectory))
                return $"Item not allowed: {source}";
            if (kind is not ("directory" or "file"))
                return $"Unknown kind for {source}";
            var isDirectory = kind == "directory";
            if (isDirectory != expectedDirectory)
                return $"Wrong kind for {source}";
            if (!seen.Add(source))
                return $"Duplicate item: {source}";
            if (!ExistsAs(Full(dir, source), isDirectory))
                return $"Missing {source}";
            items.Add((source, isDirectory));
        }
        return null;
    }

    private static int CountFeatures(string forwardDir)
    {
        try
        {
            return Directory.Exists(forwardDir)
                ? Directory.EnumerateDirectories(forwardDir).Count(d => FeatureFolder.IsMatch(Path.GetFileName(d)))
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    #endregion

    #region PlanRestore / Restore

    private sealed record RestoreDetails(
        List<(string Source, bool IsDirectory)> Items, JsonObject StateCopy, JsonObject CurrentState);

    /// <summary>
    /// What <see cref="Restore"/> would do (D-21). Checks, in order, stopping at
    /// the first problem: the archive exists and is valid; the current
    /// state.json is readable; the folder names are valid; the current
    /// extraction; no destination taken by something that stays. Never writes.
    /// </summary>
    public static RestorePlan PlanRestore(string root, string archiveDir, DateTime now) =>
        PlanRestore(Normalize(root), Normalize(archiveDir), now, out _);

    private static RestorePlan PlanRestore(string root, string archiveDir, DateTime now, out RestoreDetails? details)
    {
        details = null;
        var none = Array.Empty<string>();

        if (!Directory.Exists(archiveDir))
            return new RestorePlan(null, none, 0, null, "The archive no longer exists.");
        if (!string.Equals(Path.GetDirectoryName(archiveDir), Full(root, ArchiveFolderName), StringComparison.OrdinalIgnoreCase))
            return new RestorePlan(null, none, 0, null, $"Cannot restore: the folder is not inside {ArchiveFolderName}.");

        // (a) valid archive
        var validation = Validate(archiveDir);
        var entry = validation.Entry;
        if (!entry.IsValid || validation.StateCopy == null)
            return new RestorePlan(entry, none, 0, null, $"Cannot restore: {entry.InvalidReason}.");

        // (b) readable current state
        var current = ReadJsonObject(StatePath(root));
        if (current == null)
            return new RestorePlan(entry, none, 0, null, "Cannot restore: .reversa/state.json cannot be read.");

        // (c) folder names, current then archived
        var problem = ResolveFolders(current).Problem ?? ResolveFolders(validation.StateCopy).Problem;
        if (problem != null)
            return new RestorePlan(entry, none, 0, null, $"Cannot restore: {problem}.");

        // (d) the current extraction, archived first
        var currentPlan = BuildPlan(root, now, countFiles: true);
        var moving = new HashSet<string>(
            currentPlan.HasExtraction ? currentPlan.Items : none, StringComparer.OrdinalIgnoreCase);

        // (e) destinations taken by something that does not move out
        foreach (var (source, _) in validation.Items)
        {
            if (Exists(Full(root, source)) && !moving.Contains(source))
                return new RestorePlan(entry, none, 0, null, $"Cannot restore: {source} already exists.");
        }

        var fileCount = validation.Items.Sum(i => CountFiles(Full(archiveDir, i.Source), i.IsDirectory));
        details = new RestoreDetails(validation.Items, validation.StateCopy, current);
        return new RestorePlan(
            entry,
            validation.Items.Select(i => i.Source).ToList(),
            fileCount,
            currentPlan.HasExtraction ? currentPlan : null,
            null);
    }

    /// <summary>
    /// D-11 / RN-12, one journal: (1) archive the current extraction, if any;
    /// (2) move every archived item back; (3) write the merged state.json.
    /// A failure in (1)–(3) undoes everything. Then, outside the block: (4)
    /// delete the manifest and the state copy and remove the emptied folder;
    /// (5) the .gitignore line, only when (1) happened.
    /// </summary>
    public static ArchiveOutcome Restore(string root, string archiveDir, DateTime now)
    {
        root = Normalize(root);
        archiveDir = Normalize(archiveDir);
        var plan = PlanRestore(root, archiveDir, now, out var details);
        if (plan.Error != null || details == null)
            return ArchiveOutcome.Failed(plan.Error ?? "Cannot restore.");

        var journal = new ArchiveJournal(root);
        var pathMap = new List<(string From, string To)>();
        var warnings = new List<string>();
        string? previousArchive = null;
        try
        {
            if (plan.Current != null)
                previousArchive = ArchiveCurrent(root, plan.Current, now, journal, pathMap);

            foreach (var (source, isDirectory) in details.Items)
            {
                var from = Full(archiveDir, source);
                var to = Full(root, source);
                if (isDirectory)
                    journal.MoveDirectory(from, to);
                else
                    journal.MoveFile(from, to);
                pathMap.Add((from, to));
            }

            WriteState(root, MergeState(details.CurrentState, details.StateCopy));
        }
        catch (StepException ex)
        {
            return Fail("Restoring failed", ex, journal, root);
        }

        CleanUpRestored(root, archiveDir, warnings);
        if (previousArchive != null)
            UpdateGitignore(root, warnings);

        return new ArchiveOutcome(true, archiveDir, pathMap, warnings, null,
            Array.Empty<(string, string)>(), previousArchive);
    }

    /// <summary>The current state with the archived progress and folder names (RN-12, RF-13, data-delta §3).</summary>
    private static JsonObject MergeState(JsonObject current, JsonObject copy)
    {
        var merged = current.DeepClone().AsObject();
        foreach (var key in ProgressKeys.Append("output_folder").Append("forward_folder"))
        {
            if (copy.TryGetPropertyValue(key, out var value))
                merged[key] = value?.DeepClone();
            else
                merged.Remove(key);
        }
        return merged;
    }

    /// <summary>
    /// Deletes only the two metadata files and the folders left empty (RN-08,
    /// RN-12). Anything else left inside keeps the folder, with a warning.
    /// </summary>
    private static void CleanUpRestored(string root, string archiveDir, List<string> warnings)
    {
        try
        {
            File.Delete(Path.Combine(archiveDir, ManifestName));
            File.Delete(Full(archiveDir, StateRelative));
            foreach (var dir in Directory.EnumerateDirectories(archiveDir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            Directory.Delete(archiveDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The restored archive folder could not be removed: {Relative(root, archiveDir)}.");
        }
    }

    #endregion

    #region state.json and .gitignore

    /// <summary>
    /// Atomic write (D-07): <c>state.json.tmp</c> then a replacing rename, so
    /// a reader never sees a truncated file. The temp file is removed on failure.
    /// </summary>
    private static void WriteState(string root, JsonObject state)
    {
        var path = StatePath(root);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, state.ToJsonString(JsonWriteOptions), Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* best effort */ }
            throw new StepException(StateRelative, ex);
        }
    }

    /// <summary>
    /// D-12 / RN-14: appends <c>_reversa_archive/</c> to an existing
    /// .gitignore that has no equivalent line, with its own line ending. Only
    /// ASCII is appended, so the encoding (and a BOM) is kept. Failure is a warning.
    /// </summary>
    private static void UpdateGitignore(string root, List<string> warnings)
    {
        var path = Path.Combine(root, ".gitignore");
        try
        {
            if (!File.Exists(path)) return;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                warnings.Add("The .gitignore file was not updated: it is not UTF-8 text.");
                return;
            }

            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = Utf8NoBom.GetString(bytes, bom, bytes.Length - bom);
            if (text.Split('\n').Any(line => GitignoreLines.Contains(line.Trim(), StringComparer.Ordinal)))
                return;

            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var addition = (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + "_reversa_archive/" + newline;
            File.AppendAllText(path, addition, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The .gitignore file was not updated: {TrimPeriod(ex.Message)}.");
        }
    }

    #endregion

    #region Path mapping

    /// <summary>
    /// <paramref name="path"/> after a move: the first entry of
    /// <paramref name="map"/> whose <c>From</c> is the path or one of its
    /// folders wins. Unmoved paths come back unchanged.
    /// </summary>
    public static string MapPath(IReadOnlyList<(string From, string To)> map, string path)
    {
        foreach (var (from, to) in map)
        {
            if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase))
                return to;
            if (path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return to + path[from.Length..];
        }
        return path;
    }

    #endregion

    #region Failure

    private static ArchiveOutcome Fail(string prefix, StepException ex, ArchiveJournal journal, string root)
    {
        var stranded = journal.Rollback();
        var message = $"{prefix}: {ex.Relative}: {TrimPeriod(ex.InnerException?.Message ?? ex.Message)}.";
        if (stranded.Count == 0)
        {
            message += " Nothing was changed.";
        }
        else
        {
            message += " Some items could not be put back:\n" + string.Join("\n", stranded.Select(s =>
                s.Original.Length == 0
                    ? $"{Relative(root, s.Current)} → should be removed"
                    : $"{Relative(root, s.Current)} → should be {Relative(root, s.Original)}"));
        }
        return ArchiveOutcome.Failed(message, stranded);
    }

    /// <summary>A step that failed, with the path (relative to the root, <c>/</c>) to show the user.</summary>
    private sealed class StepException(string relative, Exception inner) : Exception(inner.Message, inner)
    {
        public string Relative { get; } = relative;
    }

    /// <summary>
    /// Moves and creations done so far, undone in reverse order (D-03, D-04).
    /// No lock pre-check: the first failing step rolls back. The rollback
    /// tries every step, collects what it could not put back, and removes only
    /// folders it created, and only when empty.
    /// </summary>
    private sealed class ArchiveJournal(string root)
    {
        private enum Kind { CreatedDirectory, CreatedFile, MovedDirectory, MovedFile }

        private readonly List<(Kind Kind, string Path, string Original)> _steps = new();

        public void EnsureDirectory(string path)
        {
            if (Directory.Exists(path)) return;
            if (Path.GetDirectoryName(path) is { } parent)
                EnsureDirectory(parent);
            Run(path, () => Directory.CreateDirectory(path));
            _steps.Add((Kind.CreatedDirectory, path, ""));
        }

        public void MoveDirectory(string from, string to)
        {
            EnsureParent(to);
            try
            {
                Directory.Move(from, to);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Name the file that holds the folder, when one can be found.
                if (FindLockedFile(from) is { } locked)
                    throw new StepException(Relative(root, locked.Path), locked.Error);
                throw new StepException(Relative(root, from), ex);
            }
            _steps.Add((Kind.MovedDirectory, to, from));
        }

        public void MoveFile(string from, string to)
        {
            EnsureParent(to);
            Run(from, () => File.Move(from, to));
            _steps.Add((Kind.MovedFile, to, from));
        }

        public void CopyFile(string from, string to)
        {
            EnsureParent(to);
            Run(from, () => File.Copy(from, to, overwrite: false));
            _steps.Add((Kind.CreatedFile, to, ""));
        }

        public void WriteNewFile(string path, string content)
        {
            EnsureParent(path);
            Run(path, () =>
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                var bytes = Utf8NoBom.GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            });
            _steps.Add((Kind.CreatedFile, path, ""));
        }

        /// <summary>Undoes every step; returns what is still out of place (current, original; original empty = should be removed).</summary>
        public List<(string Current, string Original)> Rollback()
        {
            var stranded = new List<(string Current, string Original)>();
            for (var i = _steps.Count - 1; i >= 0; i--)
            {
                var (kind, path, original) = _steps[i];
                try
                {
                    switch (kind)
                    {
                        case Kind.MovedDirectory:
                            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                            Directory.Move(path, original);
                            break;
                        case Kind.MovedFile:
                            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                            File.Move(path, original);
                            break;
                        case Kind.CreatedFile:
                            File.Delete(path);
                            break;
                        case Kind.CreatedDirectory:
                            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                                Directory.Delete(path);
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (kind is Kind.MovedDirectory or Kind.MovedFile)
                        stranded.Add((path, original));
                    else if (kind == Kind.CreatedFile)
                        stranded.Add((path, ""));
                }
            }
            _steps.Clear();
            return stranded;
        }

        private void EnsureParent(string path)
        {
            if (Path.GetDirectoryName(path) is { } parent)
                EnsureDirectory(parent);
        }

        private void Run(string path, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StepException(Relative(root, path), ex);
            }
        }

        /// <summary>After a failed folder move: the first file inside that cannot be opened, if any.</summary>
        private static (string Path, Exception Error)? FindLockedFile(string directory)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", AllFiles).Take(MaxLockProbeFiles))
                {
                    try
                    {
                        using var _ = new FileStream(file, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return (file, ex);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                /* the folder itself is unreadable: report the folder */
            }
            return null;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Folder names from a state.json (D-09). Missing or non-string: the
    /// default. Invalid, reserved or equal to each other: null, with the
    /// problem (field and value) for the message.
    /// </summary>
    private sealed record Folders(string? Output, string? Forward, string? Problem);

    private static Folders ResolveFolders(JsonObject? state)
    {
        var (output, outputProblem) = ResolveFolder(state, "output_folder", DefaultOutputFolder);
        var (forward, forwardProblem) = ResolveFolder(state, "forward_folder", DefaultForwardFolder);
        var problem = outputProblem ?? forwardProblem;
        if (problem == null && string.Equals(output, forward, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"forward_folder uses the same name as output_folder ({forward})";
            forward = null;
        }
        return new Folders(output, forward, problem);
    }

    private static (string? Name, string? Problem) ResolveFolder(JsonObject? state, string field, string fallback)
    {
        if (state == null || GetString(state, field) is not { } raw) return (fallback, null);

        var name = ReversaProjectService.ValidFolderName(raw);
        if (name == null)
            return (null, $"{field} points outside the folder ({raw})");
        if (name.Equals(ArchiveFolderName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(ReversaFolder, StringComparison.OrdinalIgnoreCase)
            || Satellites.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (null, $"{field} uses a reserved name ({raw})");
        return (name, null);
    }

    /// <summary>A JSON object of at most 1 MB, or null (missing, too large, unreadable, invalid, not an object).</summary>
    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxJsonBytes) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) return null;
            _ = obj.Count; // duplicate keys surface here
            return obj;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? GetString(JsonObject obj, string property) =>
        obj.TryGetPropertyValue(property, out var node) && node is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string StatePath(string root) => Full(root, StateRelative);

    private static string Full(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool ExistsAs(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    private static int CountFiles(string path, bool isDirectory)
    {
        if (!isDirectory) return 1;
        try { return Directory.EnumerateFiles(path, "*", AllFiles).Count(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool HasFiles(string path, bool isDirectory)
    {
        if (!isDirectory) return true;
        try { return Directory.EnumerateFiles(path, "*", AllFiles).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string TrimPeriod(string message) => message.Trim().TrimEnd('.');

    #endregion
}
