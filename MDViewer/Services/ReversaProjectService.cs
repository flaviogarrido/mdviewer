using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using MDViewer.Models;

namespace MDViewer.Services;

/// <summary>
/// Recognizes a root folder with the Reversa framework installed and builds
/// its two extra views: Skills (one entry per skill) and Flow (Reversa
/// documents by software engineering stage). Read-only: nothing is ever
/// written to the folder. Every read failure falls back to a default.
/// </summary>
public static class ReversaProjectService
{
    private const string DefaultOutputFolder = "_reversa_sdd";
    private const string DefaultForwardFolder = "_reversa_forward";
    internal const long MaxJsonBytes = 1024 * 1024;
    private const int MaxHeaderLines = 64;
    private const string PreferredEngine = ".claude";

    /// <summary>
    /// Null when <paramref name="root"/> has no <c>.reversa/state.json</c>
    /// (a regular folder). Runs on a background thread.
    /// </summary>
    public static ReversaProject? Detect(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var reversaDir = Path.Combine(root, ".reversa");
        if (!File.Exists(Path.Combine(reversaDir, "state.json")))
            return null;

        string? version = null;
        var outputFolder = DefaultOutputFolder;
        var forwardFolder = DefaultForwardFolder;
        using (var state = ReadJson(Path.Combine(reversaDir, "state.json")))
        {
            if (state?.RootElement.ValueKind == JsonValueKind.Object)
            {
                version = GetString(state.RootElement, "version");
                outputFolder = ValidFolderName(GetString(state.RootElement, "output_folder")) ?? DefaultOutputFolder;
                forwardFolder = ValidFolderName(GetString(state.RootElement, "forward_folder")) ?? DefaultForwardFolder;
            }
        }

        var activeFeatureDir = ReadActiveFeatureDir(root, reversaDir, forwardFolder);

        // Guide and map first: the Skills and Flow views show their state (feature 014).
        var skillDirs = FindSkills(root);
        var skillFiles = skillDirs.ToDictionary(
            kv => kv.Key, kv => Path.Combine(kv.Value, "SKILL.md"), StringComparer.OrdinalIgnoreCase);
        var guide = ReversaGuideService.Build(root, outputFolder, activeFeatureDir, skillFiles);
        var map = ReversaMapService.Build(guide, skillFiles);

        var (skills, skillCount) = BuildSkills(root, skillDirs, map);
        var flow = new FlowBuilder(root, outputFolder, forwardFolder, activeFeatureDir, guide).Build();
        var flowDocuments = flow.AllNodes()
            .Where(n => !n.IsDirectory)
            .Select(n => n.FullPath)
            .ToList();

        return new ReversaProject(root, version, outputFolder, forwardFolder, activeFeatureDir,
            skills, skillCount, flow, flowDocuments, guide: guide, map: map);
    }

    #region state.json / active-requirements.json

    internal static JsonDocument? ReadJson(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxJsonBytes) return null;
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string? GetString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// A folder name from state.json is accepted only as a single path segment
    /// under the root: no separators, drive colon, "..", or invalid characters.
    /// Also used by <see cref="ReversaArchiveService"/> (D-09).
    /// </summary>
    internal static string? ValidFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) return null;
        if (name is "." or ".." || name.Contains("..")) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }

    private static string? ReadActiveFeatureDir(string root, string reversaDir, string forwardFolder)
    {
        using var active = ReadJson(Path.Combine(reversaDir, "active-requirements.json"));
        if (active?.RootElement.ValueKind != JsonValueKind.Object) return null;

        var featureDir = GetString(active.RootElement, "feature-dir");
        if (string.IsNullOrWhiteSpace(featureDir) || Path.IsPathRooted(featureDir)) return null;

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, featureDir)));
            var forwardRoot = Path.Combine(root, forwardFolder);
            return Directory.Exists(full) && IsUnder(full, forwardRoot) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Skills

    /// <summary>
    /// Reads the <c>name</c> and <c>description</c> of a SKILL.md front matter
    /// (the leading <c>---</c> block, at most 64 lines). Quotes are removed and
    /// folded/literal block values are joined with spaces. Any failure: nulls.
    /// </summary>
    internal static (string? Name, string? Description) ReadSkillHeader(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine()?.Trim() != "---") return (null, null);

            var lines = new List<string>();
            var closed = false;
            for (var i = 1; i < MaxHeaderLines; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                if (line.Trim() == "---") { closed = true; break; }
                lines.Add(line);
            }
            if (!closed) return (null, null);

            string? name = null, description = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (Regex.IsMatch(value, @"^[>|][+-]?$"))
                {
                    var parts = new List<string>();
                    while (i + 1 < lines.Count && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0])))
                    {
                        i++;
                        if (lines[i].Trim().Length > 0) parts.Add(lines[i].Trim());
                    }
                    value = string.Join(" ", parts);
                }
                else
                {
                    value = Unquote(value);
                }

                if (key == "name") name = NullIfEmpty(value);
                else if (key == "description") description = NullIfEmpty(value);
            }
            return (name, description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"");
        return value;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// One entry per skill (<c>&lt;engine&gt;/skills/&lt;name&gt;/SKILL.md</c>,
    /// keyed by folder name), preferring the <c>.claude</c> copy, then the first
    /// engine folder in ordinal order. Folder name → skill folder.
    /// </summary>
    private static Dictionary<string, string> FindSkills(string root)
    {
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var engines = FolderTreeService.EnumerateSorted(root, directories: true)
            .Where(e => !FolderTreeService.IsExcludedDirectory(Path.GetFileName(e))
                        && Directory.Exists(Path.Combine(e, "skills")))
            .OrderBy(e => Path.GetFileName(e).Equals(PreferredEngine, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => Path.GetFileName(e), StringComparer.OrdinalIgnoreCase);

        foreach (var engine in engines)
        {
            foreach (var skillDir in FolderTreeService.EnumerateSorted(Path.Combine(engine, "skills"), directories: true))
            {
                var key = Path.GetFileName(skillDir);
                if (!chosen.ContainsKey(key) && File.Exists(Path.Combine(skillDir, "SKILL.md")))
                    chosen[key] = skillDir;
            }
        }

        return chosen;
    }

    /// <summary>
    /// The Skills view, aligned with the usage map (feature 014): groups are
    /// the map scenarios (A to H) and then the skills outside them; inside a
    /// group, skills come in order of use. A skill the map shows as done or
    /// as the next step carries that mark.
    /// </summary>
    private static (FolderTree Tree, int Count) BuildSkills(
        string root, Dictionary<string, string> chosen, ReversaMap map)
    {
        var index = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);
        if (chosen.Count == 0)
        {
            var none = new FileTreeNode("No skills found", "reversa:skills/none", isDirectory: true, parent: null)
            {
                IsGroup = true
            };
            return (new FolderTree(root, new[] { none }, index), 0);
        }

        var families = SkillFamilies.Order.ToDictionary(
            f => f,
            f => new FileTreeNode(f, $"reversa:skills/{f}", isDirectory: true, parent: null)
            {
                IsGroup = true,
                IconOverride = "🗃️",
                IsExpanded = true
            });

        var skills = chosen
            .Select(kv => (Key: kv.Key, Dir: kv.Value, Header: ReadSkillHeader(Path.Combine(kv.Value, "SKILL.md"))))
            .Select(s => (s.Key, s.Dir, s.Header.Description, Name: s.Header.Name ?? s.Key))
            .ToList();
        // Order of use inside the group; skills without a place come last, by name.
        skills.Sort((a, b) =>
        {
            var byPlace = SkillFamilies.Position(a.Key).CompareTo(SkillFamilies.Position(b.Key));
            return byPlace != 0 ? byPlace : NativeMethods.StrCmpLogicalW(a.Name, b.Name);
        });

        foreach (var skill in skills)
        {
            var family = families[SkillFamilies.Of(skill.Key)];
            var skillMd = Path.Combine(skill.Dir, "SKILL.md");
            var state = map.Find(skill.Key)?.State ?? MapStepState.None;
            var node = new FileTreeNode(skill.Name, skillMd, isDirectory: false, family)
            {
                IconOverride = state switch
                {
                    MapStepState.Done => "✅",
                    MapStepState.Current => "▶️",
                    _ => "🧩"
                },
                IsHighlighted = state == MapStepState.Current,
                Description = WithState(skill.Description, state)
            };
            foreach (var aux in EnumerateSkillAuxiliaries(skill.Dir, skillMd))
            {
                var child = new FileTreeNode(aux.Name, aux.Path, isDirectory: false, node);
                node.Children.Add(child);
                index[aux.Path] = child;
            }
            family.Children.Add(node);
            index[skillMd] = node;
        }

        var nodes = SkillFamilies.Order
            .Select(f => families[f])
            .Where(f => f.Children.Count > 0)
            .ToList();
        return (new FolderTree(root, nodes, index), skills.Count);
    }

    /// <summary>The skill description with the map state in words, for the tooltip and assistive technology.</summary>
    private static string? WithState(string? description, MapStepState state)
    {
        var prefix = state switch
        {
            MapStepState.Done => "Done in this folder",
            MapStepState.Current => "Next step",
            _ => null
        };
        if (prefix == null) return description;
        return description == null ? prefix : $"{prefix}\n{description}";
    }

    /// <summary>The skill's other Markdown files, by relative path in natural order.</summary>
    private static List<(string Name, string Path)> EnumerateSkillAuxiliaries(string skillDir, string skillMd)
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
            var result = Directory.EnumerateFiles(skillDir, "*", options)
                .Where(f => FolderTreeService.IsMarkdownFile(f)
                            && !string.Equals(f, skillMd, StringComparison.OrdinalIgnoreCase))
                .Select(f => (Name: Path.GetRelativePath(skillDir, f).Replace('\\', '/'), Path: f))
                .ToList();
            result.Sort((a, b) => NativeMethods.StrCmpLogicalW(a.Name, b.Name));
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<(string, string)>();
        }
    }

    /// <summary>
    /// Fixed skill → group table (Reversa 1.3.6). The first groups are the
    /// scenarios of the usage map, with the map's own skills in order of use
    /// plus the skills of the same team that the map does not show; then the
    /// teams outside the scenarios. Unknown "reversa-*" skills go to "Reversa
    /// (other)", anything else to "Other".
    /// </summary>
    private static class SkillFamilies
    {
        public const string ReversaOther = "Reversa (other)";
        public const string Other = "Other";
        private const string IndependentAgents = "Independent agents";

        // Skills of a scenario's team that are not on the map, after the map's own.
        private static readonly Dictionary<string, string[]> ScenarioExtras = new()
        {
            ["A"] = new[] { "reversa-agents-help" },
            ["D"] = new[]
            {
                "reversa-forward-autonomous", "reversa-code-express", "reversa-resume", "reversa-principles"
            },
            ["E"] = new[] { "reversa-debugger-debate", "reversa-debugger-graph", "reversa-depth-inspection" },
        };

        private static readonly (string Family, string[] Skills)[] Table = BuildTable();

        private static (string Family, string[] Skills)[] BuildTable()
        {
            var table = new List<(string, string[])>();
            foreach (var (letter, _, skills) in ReversaMapService.Layout.Where(l => l.Letter != null))
            {
                var extras = ScenarioExtras.TryGetValue(letter!, out var e) ? e : Array.Empty<string>();
                table.Add((ReversaMapService.ScenarioLabel(letter!), skills.Concat(extras).ToArray()));
            }

            table.Add(("Migration", new[]
            {
                "reversa-migrate", "reversa-paradigm-advisor", "reversa-curator", "reversa-strategist",
                "reversa-designer", "reversa-screen-translator", "reversa-inspector"
            }));
            table.Add(("Docs", new[]
            {
                "reversa-docs", "reversa-docs-analyst", "reversa-docs-mapper", "reversa-docs-storyteller",
                "reversa-docs-publisher", "reversa-arquitetura-3d", "reversa-especialista-d3",
                "reversa-highcharts-visualizer", "reversa-selo-generativo", "reversa-image-prompt-json"
            }));
            table.Add((IndependentAgents,
                ReversaMapService.Layout.First(l => l.Title == IndependentAgents).Skills.ToArray()));
            table.Add(("Front-end", new[] { "impeccable" }));
            return table.ToArray();
        }

        // First group wins: a skill belongs to one group only.
        private static readonly Dictionary<string, (string Family, int Position)> BySkill = BuildIndex();

        private static Dictionary<string, (string, int)> BuildIndex()
        {
            var index = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (family, skills) in Table)
            {
                for (var i = 0; i < skills.Length; i++)
                    index.TryAdd(skills[i], (family, i));
            }
            return index;
        }

        public static readonly IReadOnlyList<string> Order =
            Table.Select(t => t.Family).Append(ReversaOther).Append(Other).ToList();

        public static string Of(string skillKey) =>
            BySkill.TryGetValue(skillKey, out var entry) ? entry.Family
            : skillKey.StartsWith("reversa-", StringComparison.OrdinalIgnoreCase) ? ReversaOther
            : Other;

        /// <summary>Place of the skill in its group (order of use); skills without one come last.</summary>
        public static int Position(string skillKey) =>
            BySkill.TryGetValue(skillKey, out var entry) ? entry.Position : int.MaxValue;
    }

    #endregion

    #region Flow

    /// <summary>
    /// Builds the Flow view: Reversa documents grouped in stages, in the order
    /// of the software engineering process. The first matching rule claims a
    /// document, so each one appears in exactly one stage. Core stages without
    /// documents are kept as pending; optional ones are omitted.
    /// </summary>
    private sealed class FlowBuilder
    {
        private static readonly string[] UnitFiles = { "requirements.md", "design.md", "tasks.md" };

        private static readonly string[] FeatureOrder =
        {
            "requirements.md", "investigation.md", "roadmap.md", "data-delta.md", "legacy-impact.md",
            "actions.md", "onboarding.md", "regression-watch.md"
        };

        // Top-level folders of the output folder claimed by a named rule; never units.
        private static readonly HashSet<string> ReservedOutputFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "brainstorms", "ideation", "flowcharts", "adrs", "architecture", "sdd", "user-stories",
            "traceability", "openapi", "addenda", "migration", "screens", "_pricing"
        };

        private static readonly Regex FeatureFolder = new(@"^(\d+)-", RegexOptions.Compiled);

        private readonly string _root;
        private readonly string _out;
        private readonly string _fwd;
        private readonly string? _activeFeatureDir;
        private readonly ReversaGuide _guide;
        private readonly Dictionary<string, FileTreeNode> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);

        public FlowBuilder(
            string root, string outputFolder, string forwardFolder, string? activeFeatureDir, ReversaGuide guide)
        {
            _guide = guide;
            _root = root;
            _out = Path.Combine(root, outputFolder);
            _fwd = Path.Combine(root, forwardFolder);
            _activeFeatureDir = activeFeatureDir;
        }

        public FolderTree Build()
        {
            var stages = new List<FileTreeNode>();
            AddStage(stages, 1, "Conception", core: false, ConceptionHelp, FillConception);
            AddStage(stages, 2, "Reconnaissance", core: true, Discovery("Scout"), s =>
            {
                Files(s, _out, "inventory.md", "dependencies.md");
                TopFiles(s, _out, n => n.StartsWith("surface", StringComparison.OrdinalIgnoreCase));
            });
            AddStage(stages, 3, "Excavation", core: true, Discovery("Archaeologist"), s =>
            {
                Files(s, _out, "code-analysis.md", "data-dictionary.md");
                Folder(s, Path.Combine(_out, "flowcharts"));
            });
            AddStage(stages, 4, "Interpretation", core: true, Discovery("Detective"), s =>
            {
                Files(s, _out, "domain.md", "state-machines.md", "permissions.md");
                Folder(s, Path.Combine(_out, "adrs"));
            });
            AddStage(stages, 5, "Architecture", core: true, Discovery("Architect"), s =>
            {
                Files(s, _out, "architecture.md");
                Folder(s, Path.Combine(_out, "architecture"));
                Files(s, _out, "c4-context.md", "c4-containers.md", "c4-components.md");
                TopFiles(s, _out, n => n.StartsWith("erd", StringComparison.OrdinalIgnoreCase));
            });
            AddStage(stages, 6, "Specification", core: true, Discovery("Writer"), FillSpecification);
            AddStage(stages, 7, "Review", core: true, Discovery("Reviewer"), s =>
            {
                Files(s, _out, "confidence-report.md", "questions.md", "gaps.md", "user-decisions.md");
                TopFiles(s, _out, n => n.StartsWith("cross-review", StringComparison.OrdinalIgnoreCase));
            });
            AddStage(stages, 8, "Evolution", core: true, Scenario("D", "reversa-forward"), FillEvolution);
            AddStage(stages, 9, "Bugs", core: false, Scenario("E", "reversa-debugger"), s => Contents(s, Path.Combine(_root, "_reversa_bugs")));
            AddStage(stages, 10, "Refactoring", core: false, Scenario("F", "reversa-refactor"), s => Contents(s, Path.Combine(_root, "_reversa_refactor")));
            AddStage(stages, 11, "Migration", core: false, AfterDiscovery("reversa-migrate"), s =>
            {
                Folder(s, Path.Combine(_out, "migration"));
                Folder(s, Path.Combine(_out, "screens"));
            });
            AddStage(stages, 12, "Documentation", core: false, AfterDiscovery("reversa-docs"), s => Contents(s, Path.Combine(_root, "_reversa_docs")));
            AddStage(stages, 13, "Pricing", core: false, Scenario("G", "reversa-pricing"), s => Contents(s, Path.Combine(_out, "_pricing")));
            AddStage(stages, 14, "Other", core: false, null, s =>
            {
                Contents(s, _out);
                Folder(s, _fwd);
            });

            return new FolderTree(_root, stages, _index);
        }

        // Where each stage sits on the usage map (feature 014): shown as the stage tooltip.
        private static string ConceptionHelp =>
            $"Map: {ReversaMapService.ScenarioLabel("B")} (/reversa-brainstorm) or "
            + $"{ReversaMapService.ScenarioLabel("C")} (/reversa-new)";

        private static string Discovery(string agent) =>
            $"Map: {ReversaMapService.ScenarioLabel("A")} (/reversa), {agent}";

        private static string Scenario(string letter, string skill) =>
            $"Map: {ReversaMapService.ScenarioLabel(letter)} (/{skill})";

        private static string AfterDiscovery(string skill) => $"Map: After discovery (/{skill})";

        /// <param name="help">Scenario and command that produce the stage; null for none.</param>
        private static FileTreeNode NewStage(int number, string name, bool pending, string? help) =>
            new(pending ? $"{name} (pending)" : name, $"reversa:flow/{number:00}-{name.ToLowerInvariant()}",
                isDirectory: true, parent: null)
            {
                IsGroup = true,
                IsPending = pending,
                IconOverride = "🧭",
                Description = pending ? (help == null ? "No documents yet" : $"No documents yet. {help}") : help
            };

        private static void AddStage(
            List<FileTreeNode> stages, int number, string name, bool core, string? help, Action<FileTreeNode> fill)
        {
            var stage = NewStage(number, name, pending: false, help);
            fill(stage);
            if (stage.Children.Count > 0)
                stages.Add(stage);
            else if (core)
                stages.Add(NewStage(number, name, pending: true, help));
        }

        /// <summary>The forward stage of the active feature, as shown after its folder name.</summary>
        private string? ActiveStageText() => _guide.Stage switch
        {
            FeatureStage.Empty => "no requirements",
            FeatureStage.Requirements => "requirements",
            FeatureStage.Plan => "plan",
            FeatureStage.Coding => $"coding {_guide.ActionsDone}/{_guide.ActionsTotal}",
            FeatureStage.Done => _guide.Synced ? "synced" : "delivered",
            _ => null
        };

        private void FillConception(FileTreeNode stage)
        {
            var entries = TopEntries(_out);
            foreach (var prefix in new[] { "newproject-brief", "personas", "prd" })
            {
                foreach (var (path, isDir) in entries.Where(e =>
                             Path.GetFileName(e.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    if (isDir) Folder(stage, path);
                    else File(stage, path);
                }
            }
            Folder(stage, Path.Combine(_out, "brainstorms"));
            Folder(stage, Path.Combine(_out, "ideation"));
        }

        private void FillSpecification(FileTreeNode stage)
        {
            Folder(stage, Path.Combine(_out, "sdd"));

            if (Directory.Exists(_out))
            {
                foreach (var dir in FolderTreeService.EnumerateSorted(_out, directories: true))
                {
                    var name = Path.GetFileName(dir);
                    if (ReservedOutputFolders.Contains(name) || FolderTreeService.IsExcludedDirectory(name)) continue;
                    if (!UnitFiles.Any(f => System.IO.File.Exists(Path.Combine(dir, f)))) continue;

                    var unit = new FileTreeNode(name, dir, isDirectory: true, stage);
                    Files(unit, dir, UnitFiles);
                    Contents(unit, dir);
                    Attach(stage, unit);
                }
            }

            Folder(stage, Path.Combine(_out, "user-stories"));
            Folder(stage, Path.Combine(_out, "traceability"));
            Folder(stage, Path.Combine(_out, "openapi"));
        }

        private void FillEvolution(FileTreeNode stage)
        {
            var addendaDir = Path.Combine(_out, "addenda");
            var addenda = Directory.Exists(addendaDir)
                ? FolderTreeService.EnumerateSorted(addendaDir, directories: false)
                    .Where(FolderTreeService.IsMarkdownFile)
                    .ToList()
                : new List<string>();

            if (Directory.Exists(_fwd))
            {
                foreach (var dir in FolderTreeService.EnumerateSorted(_fwd, directories: true))
                {
                    var name = Path.GetFileName(dir);
                    var match = FeatureFolder.Match(name);
                    if (!match.Success) continue;

                    var isActive = _activeFeatureDir != null
                                   && string.Equals(Path.TrimEndingDirectorySeparator(dir), _activeFeatureDir,
                                       StringComparison.OrdinalIgnoreCase);
                    var label = isActive && ActiveStageText() is { } stageText ? $"{name} · {stageText}" : name;
                    var feature = new FileTreeNode(label, dir, isDirectory: true, stage)
                    {
                        IsHighlighted = isActive
                    };
                    Files(feature, dir, FeatureOrder);
                    Folder(feature, Path.Combine(dir, "audit"));
                    Folder(feature, Path.Combine(dir, "interfaces"));
                    Contents(feature, dir);

                    var id = match.Groups[1].Value;
                    foreach (var addendum in addenda.Where(a =>
                                 Path.GetFileName(a).StartsWith(id + "-", StringComparison.OrdinalIgnoreCase)))
                    {
                        File(feature, addendum, "addenda/" + Path.GetFileName(addendum));
                    }
                    Attach(stage, feature);
                }
            }

            // Addenda without a matching feature folder close the stage.
            Folder(stage, addendaDir);
        }

        #region Node helpers

        /// <summary>Adds a Markdown file (once in the whole Flow) under <paramref name="parent"/>.</summary>
        private void File(FileTreeNode parent, string path, string? displayName = null)
        {
            if (!FolderTreeService.IsMarkdownFile(path) || !System.IO.File.Exists(path)) return;
            var full = Path.GetFullPath(path);
            if (!_claimed.Add(full)) return;

            var node = new FileTreeNode(displayName ?? Path.GetFileName(full), full, isDirectory: false, parent);
            parent.Children.Add(node);
            _index[full] = node;
        }

        private void Files(FileTreeNode parent, string directory, params string[] names)
        {
            foreach (var name in names)
                File(parent, Path.Combine(directory, name));
        }

        /// <summary>Top-level Markdown files of <paramref name="directory"/> whose name matches.</summary>
        private void TopFiles(FileTreeNode parent, string directory, Func<string, bool> match)
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in FolderTreeService.EnumerateSorted(directory, directories: false))
            {
                if (match(Path.GetFileName(file)))
                    File(parent, file);
            }
        }

        private static List<(string Path, bool IsDir)> TopEntries(string directory)
        {
            if (!Directory.Exists(directory)) return new List<(string, bool)>();
            return FolderTreeService.EnumerateSorted(directory, directories: true).Select(d => (d, true))
                .Concat(FolderTreeService.EnumerateSorted(directory, directories: false).Select(f => (f, false)))
                .ToList();
        }

        /// <summary>Adds <paramref name="directory"/> as a folder node with its unclaimed documents.</summary>
        private void Folder(FileTreeNode parent, string directory)
        {
            if (!Directory.Exists(directory)) return;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var folder = new FileTreeNode(Path.GetFileName(full), full, isDirectory: true, parent);
            Contents(folder, full);
            Attach(parent, folder);
        }

        /// <summary>Adds the unclaimed documents of <paramref name="directory"/> directly under <paramref name="parent"/>.</summary>
        private void Contents(FileTreeNode parent, string directory)
        {
            if (!Directory.Exists(directory)) return;
            var scratch = new Dictionary<string, FileTreeNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Prune(FolderTreeService.BuildSubtree(directory, parent, scratch)))
                parent.Children.Add(node);
        }

        /// <summary>Drops already claimed files (and folders left empty); claims and indexes the rest.</summary>
        private List<FileTreeNode> Prune(IEnumerable<FileTreeNode> nodes)
        {
            var result = new List<FileTreeNode>();
            foreach (var node in nodes)
            {
                if (node.IsDirectory)
                {
                    var kept = Prune(node.Children.ToList());
                    node.Children.Clear();
                    foreach (var child in kept)
                        node.Children.Add(child);
                    if (kept.Count == 0) continue;
                }
                else if (!_claimed.Add(node.FullPath))
                {
                    continue;
                }
                _index[node.FullPath] = node;
                result.Add(node);
            }
            return result;
        }

        /// <summary>Attaches a folder node to its parent only when it has documents.</summary>
        private void Attach(FileTreeNode parent, FileTreeNode folder)
        {
            if (folder.Children.Count == 0) return;
            parent.Children.Add(folder);
            _index[folder.FullPath] = folder;
        }

        #endregion
    }

    #endregion
}
