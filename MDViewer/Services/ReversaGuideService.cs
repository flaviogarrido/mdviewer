using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MDViewer.Models;

namespace MDViewer.Services;

/// <summary>
/// Deduces where a Reversa project stands and which command comes next
/// (feature 012), from what is on disk: <c>state.json</c>, <c>plan.md</c>,
/// the active feature artifacts and the satellite folders. Read-only: nothing
/// is ever written or executed. Every read failure counts as "absent".
/// The situation → command table is fixed for Reversa 1.3.6.
/// </summary>
public static class ReversaGuideService
{
    private const string DoubtMarker = "[DÚVIDA]";
    private const int MaxAlternatives = 3;
    private const int MaxComplementary = 3;

    // A table row ending with an action checkbox: "| [ ] |", "| [X] |" or "| `[ ]` |".
    private static readonly Regex ActionLine = new(
        @"^[ \t]*\|.*\|[ \t]*`?\[(?<mark>[ xX])\]`?[ \t]*\|[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // The numeric prefix of a feature folder: "012" or "20261118-205300".
    private static readonly Regex FeatureNumber = new(@"^\d+(-\d+)?", RegexOptions.Compiled);

    private sealed record Feature(string Id, FeatureStage Stage, int Doubts, int Done, int Total, bool Synced);

    /// <param name="activeFeatureDir">Already validated by <see cref="ReversaProjectService"/>; null when there is none.</param>
    /// <param name="skillFiles">Installed skills: folder name → <c>SKILL.md</c> path.</param>
    public static ReversaGuide Build(
        string root, string outputFolder, string? activeFeatureDir,
        IReadOnlyDictionary<string, string> skillFiles)
    {
        var reversaDir = Path.Combine(root, ".reversa");
        var outputDir = Path.Combine(root, outputFolder);

        using var state = ReversaProjectService.ReadJson(Path.Combine(reversaDir, "state.json"));
        if (state?.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new ReversaGuide(GuideSituation.StateUnreadable, "Reversa state could not be read",
                ".reversa/state.json is not valid JSON", Array.Empty<GuideSuggestion>());
        }

        var stateRoot = state.RootElement;
        var completedPhases = StringArray(stateRoot, "completed");
        var completed = ArrayCount(stateRoot, "completed");
        var phase = ReversaProjectService.GetString(stateRoot, "phase");
        var hasPhase = !string.IsNullOrWhiteSpace(phase);

        var feature = activeFeatureDir == null ? null : ReadFeature(activeFeatureDir, outputDir);
        var suggestions = new List<GuideSuggestion>();
        GuideSituation situation;
        string title, detail;

        void Primary(string skill) =>
            suggestions.Add(Suggestion(skill, "Next step", GuideSuggestionKind.Primary, skillFiles));

        void Alternatives(params (string Skill, string Hint)[] items)
        {
            foreach (var (skill, hint) in items.Where(i => skillFiles.ContainsKey(i.Skill)).Take(MaxAlternatives))
                suggestions.Add(Suggestion(skill, $"Or: {hint}", GuideSuggestionKind.Alternative, skillFiles));
        }

        if (feature is { Stage: not FeatureStage.Done })
        {
            situation = GuideSituation.FeatureInProgress;
            switch (feature.Stage)
            {
                case FeatureStage.Empty:
                    title = $"Feature {feature.Id}: no requirements yet";
                    detail = "The feature folder has no requirements.md";
                    Primary("reversa-requirements");
                    break;
                case FeatureStage.Requirements when feature.Doubts > 0:
                    title = $"Feature {feature.Id}: requirements";
                    detail = Count(feature.Doubts, "open question");
                    Primary("reversa-clarify");
                    break;
                case FeatureStage.Requirements:
                    title = $"Feature {feature.Id}: requirements";
                    detail = "Requirements are closed, ready for the plan";
                    Primary("reversa-plan");
                    break;
                case FeatureStage.Plan:
                    title = $"Feature {feature.Id}: plan";
                    detail = "The roadmap is ready, actions are missing";
                    Primary("reversa-to-do");
                    break;
                default:
                    title = $"Feature {feature.Id}: coding";
                    detail = $"{feature.Done} of {Count(feature.Total, "action")} done";
                    Primary("reversa-coding");
                    break;
            }
        }
        else if (feature is { Synced: false })
        {
            situation = GuideSituation.FeatureDelivered;
            title = $"Feature {feature.Id}: delivered";
            detail = (feature.Total == 1 ? "1 action done" : $"All {feature.Total} actions done")
                     + ", not yet synced to the extraction";
            Primary("reversa-sync");
        }
        else
        {
            var unfinished = ArrayCount(stateRoot, "pending") > 0
                             || HasOpenPlanTask(Path.Combine(reversaDir, "plan.md"))
                             || (hasPhase && completed == 0);

            if (unfinished)
            {
                situation = GuideSituation.DiscoveryInProgress;
                title = "Discovery in progress";
                var phases = completed == 0 ? "no phases completed" : $"{Count(completed, "phase")} completed";
                detail = hasPhase ? $"Phase {phase}, {phases}" : Capitalize(phases);
                Primary("reversa");
            }
            else if (completed > 0)
            {
                situation = GuideSituation.DiscoveryCompleted;
                title = "Discovery completed";
                detail = feature != null ? $"Feature {feature.Id} delivered and synced" : "Choose a destination";
                Primary("reversa-forward");
                Alternatives(
                    ("reversa-migrate", "rebuild on a modern stack"),
                    ("reversa-docs", "generate the visual documentation"));
            }
            else
            {
                situation = GuideSituation.NoExtraction;
                title = "No extraction yet";
                detail = "Start from your scenario";
                Primary("reversa");
                Alternatives(
                    ("reversa-new", "create a new project from an idea"),
                    ("reversa-brainstorm", "explore a raw idea first"));
            }
        }

        AddComplementary(suggestions, root, reversaDir, outputDir, skillFiles);
        return new ReversaGuide(situation, title, detail, suggestions)
        {
            Stage = feature?.Stage,
            HasDoubts = feature is { Doubts: > 0 },
            Synced = feature is { Synced: true },
            CompletedPhases = completedPhases,
            Phase = hasPhase ? phase : null
        };
    }

    #region Suggestions

    private static GuideSuggestion Suggestion(
        string skill, string hint, GuideSuggestionKind kind, IReadOnlyDictionary<string, string> skillFiles) =>
        new($"/{skill}", hint, skillFiles.TryGetValue(skill, out var path) ? path : null, kind);

    /// <summary>
    /// Side work found in the folder: the team orchestrator is suggested when
    /// its folder has at least one Markdown document, and <c>/reversa-resume</c>
    /// when there are paused features. Skills that are not installed are left out.
    /// </summary>
    private static void AddComplementary(
        List<GuideSuggestion> suggestions, string root, string reversaDir, string outputDir,
        IReadOnlyDictionary<string, string> skillFiles)
    {
        var teams = new (string Folder, string Skill, string Hint)[]
        {
            (Path.Combine(root, "_reversa_bugs"), "reversa-debugger", "bugs are registered"),
            (Path.Combine(root, "_reversa_refactor"), "reversa-refactor", "refactoring has started"),
            (Path.Combine(outputDir, "_pricing"), "reversa-pricing", "pricing has started"),
        };

        var added = 0;
        foreach (var (folder, skill, hint) in teams)
        {
            if (added == MaxComplementary) break;
            if (!skillFiles.ContainsKey(skill) || !HasMarkdown(folder)) continue;
            suggestions.Add(Suggestion(skill, $"Also: {hint}", GuideSuggestionKind.Complementary, skillFiles));
            added++;
        }

        var paused = PausedFeatureCount(Path.Combine(reversaDir, "active-requirements.json"));
        if (paused > 0 && skillFiles.ContainsKey("reversa-resume"))
        {
            suggestions.Add(Suggestion("reversa-resume", $"Also: {Count(paused, "paused feature")}",
                GuideSuggestionKind.Complementary, skillFiles));
        }
    }

    private static int PausedFeatureCount(string activePath)
    {
        using var active = ReversaProjectService.ReadJson(activePath);
        return active?.RootElement.ValueKind == JsonValueKind.Object
            ? ArrayCount(active.RootElement, "paused-features")
            : 0;
    }

    #endregion

    #region Active feature

    /// <summary>
    /// The stage comes from the files of the feature folder only, never from
    /// the self-declared fields of <c>active-requirements.json</c>.
    /// </summary>
    private static Feature ReadFeature(string featureDir, string outputDir)
    {
        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(featureDir));

        var requirements = ReadText(Path.Combine(featureDir, "requirements.md"));
        if (requirements == null) return new Feature(id, FeatureStage.Empty, 0, 0, 0, false);

        if (ReadText(Path.Combine(featureDir, "roadmap.md")) == null)
            return new Feature(id, FeatureStage.Requirements, CountOccurrences(requirements, DoubtMarker), 0, 0, false);

        var actions = ReadText(Path.Combine(featureDir, "actions.md"));
        var marks = actions == null
            ? new List<string>()
            : ActionLine.Matches(actions).Select(m => m.Groups["mark"].Value).ToList();
        if (marks.Count == 0) return new Feature(id, FeatureStage.Plan, 0, 0, 0, false);

        var done = marks.Count(m => m != " ");
        return done < marks.Count
            ? new Feature(id, FeatureStage.Coding, 0, done, marks.Count, false)
            : new Feature(id, FeatureStage.Done, 0, done, marks.Count, HasAddendum(outputDir, id));
    }

    /// <summary>A Markdown file of <c>addenda/</c> whose name starts with the feature number.</summary>
    private static bool HasAddendum(string outputDir, string featureId)
    {
        var number = FeatureNumber.Match(featureId) is { Success: true } m ? m.Value : featureId;
        return FolderTreeService.EnumerateSorted(Path.Combine(outputDir, "addenda"), directories: false)
            .Where(FolderTreeService.IsMarkdownFile)
            .Select(Path.GetFileName)
            .Any(name => name!.StartsWith(number, StringComparison.OrdinalIgnoreCase)
                         && (name.Length == number.Length || !char.IsDigit(name[number.Length])));
    }

    #endregion

    #region Reading

    private static int ArrayCount(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static IReadOnlyList<string> StringArray(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList()
            : Array.Empty<string>();

    private static bool HasOpenPlanTask(string planPath) =>
        ReadText(planPath) is { } plan && ReversaArchiveService.OpenTask.IsMatch(plan);

    /// <summary>Null when the file is missing, larger than 1 MB or unreadable.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > ReversaProjectService.MaxJsonBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasMarkdown(string folder)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 8,
            AttributesToSkip = 0
        };
        try
        {
            return Directory.Exists(folder)
                   && Directory.EnumerateFiles(folder, "*", options).Any(FolderTreeService.IsMarkdownFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    #endregion
}
