using System.Text;
using System.Text.Json.Nodes;
using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer.Tests;

/// <summary>Feature 011: archive and restore the Reversa extraction.</summary>
public sealed class ReversaArchiveServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 18, 14, 3, 12);
    private const string Stamp = "20261118-140312";

    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    #region Helpers

    /// <summary>A realistic state.json: install keys, progress keys and preferences.</summary>
    private static string StateJson(
        string phase = "\"revisao\"", string pending = "[]",
        string output = "\"_reversa_sdd\"", string forward = "\"_reversa_forward\"",
        string userName = "Garrido") => $$"""
        {
          "version": "1.3.6",
          "project": "mdviewer",
          "user_name": "{{userName}}",
          "chat_language": "pt-br",
          "doc_language": "Português",
          "output_folder": {{output}},
          "forward_folder": {{forward}},
          "phase": {{phase}},
          "completed": ["reconhecimento"],
          "pending": {{pending}},
          "checkpoints": { "scout": { "modules": 10 } },
          "engines": ["claude-code"],
          "agents": ["reversa"],
          "created_files": ["CLAUDE.md"],
          "specs_choice": "auto",
          "custom_folders": [],
          "autonomous": true,
          "redator_progress": { "units_done": [] },
          "regression_check": { "result": "x" },
          "autonomous_run": { "warnings": [] }
        }
        """;

    private void State(string json) => _tmp.File(".reversa/state.json", json);

    /// <summary>A finished extraction with every kind of RN-03 item, plus installer files that must stay.</summary>
    private void Extraction(string phase = "\"revisao\"", string pending = "[]", string domain = "# domain")
    {
        State(StateJson(phase, pending));
        _tmp.File("_reversa_sdd/domain.md", domain);
        _tmp.File("_reversa_sdd/addenda/010-x.md");
        _tmp.File("_reversa_forward/010-visao-reversa/requirements.md");
        _tmp.File(".reversa/context/surface.json", "{}");
        _tmp.File(".reversa/plan.md", "- [x] Scout ✅\n");
        _tmp.File(".reversa/active-requirements.json", "{}");
        _tmp.File(".reversa/forward-autonomous.json", "{}");
        Installer();
    }

    private void Installer()
    {
        _tmp.File(".reversa/config.toml", "a = 1\n");
        _tmp.File(".reversa/version", "1.3.6\n");
        _tmp.File(".reversa/_config/manifest.yaml", "x: 1\n");
        _tmp.File(".claude/skills/reversa/SKILL.md", "---\nname: reversa\n---\n");
    }

    private string Archived(string relative, string stamp = Stamp) =>
        _tmp.PathOf($"{ReversaArchiveService.ArchiveFolderName}/{stamp}/{relative}");

    private string ArchiveDir(string stamp = Stamp) => _tmp.PathOf($"{ReversaArchiveService.ArchiveFolderName}/{stamp}");

    private JsonObject ReadState(string? path = null) =>
        JsonNode.Parse(File.ReadAllText(path ?? _tmp.PathOf(".reversa/state.json")))!.AsObject();

    /// <summary>Relative path of every entry plus the bytes of every file: "nothing changed" without write times.</summary>
    private List<string> Tree() =>
        Directory.EnumerateFileSystemEntries(_tmp.Root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_tmp.Root, p) +
                         (File.Exists(p) ? "|" + Convert.ToBase64String(File.ReadAllBytes(p)) : "/"))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    /// <summary>A hand-made archive folder.</summary>
    private string MakeArchive(string name, string? manifest, string? stateCopy, params string[] files)
    {
        var dir = _tmp.Dir($"{ReversaArchiveService.ArchiveFolderName}/{name}");
        if (manifest != null) _tmp.File($"{ReversaArchiveService.ArchiveFolderName}/{name}/{ReversaArchiveService.ManifestName}", manifest);
        if (stateCopy != null) _tmp.File($"{ReversaArchiveService.ArchiveFolderName}/{name}/.reversa/state.json", stateCopy);
        foreach (var file in files)
            _tmp.File($"{ReversaArchiveService.ArchiveFolderName}/{name}/{file}");
        return dir;
    }

    private static string Manifest(string items) =>
        $$"""{"schema-version":1,"state-copy":".reversa/state.json","items":[{{items}}]}""";

    private const string SddItem = """{"source":"_reversa_sdd","kind":"directory"}""";

    #endregion

    #region Inspect / PlanArchive (T007)

    [Fact]
    public void Nothing_to_archive_without_items_and_with_null_phase()
    {
        State(StateJson(phase: "null"));
        _tmp.Dir("_reversa_sdd"); // an empty folder is not an extraction
        Installer();

        Assert.False(ReversaArchiveService.Inspect(_tmp.Root).CanArchive);
        Assert.False(ReversaArchiveService.PlanArchive(_tmp.Root, Now).HasExtraction);
    }

    [Fact]
    public void A_single_satellite_file_is_an_extraction()
    {
        State(StateJson(phase: "null"));
        _tmp.File("_reversa_bugs/x.md");

        Assert.True(ReversaArchiveService.Inspect(_tmp.Root).CanArchive);
    }

    [Fact]
    public void A_phase_alone_is_an_extraction_with_no_items()
    {
        State(StateJson(phase: "\"escavacao\""));

        var plan = ReversaArchiveService.PlanArchive(_tmp.Root, Now);

        Assert.True(ReversaArchiveService.Inspect(_tmp.Root).CanArchive);
        Assert.True(plan.HasExtraction);
        Assert.Empty(plan.Items);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void Archive_count_includes_invalid_folders_and_valid_count_does_not()
    {
        Extraction();
        Assert.True(ReversaArchiveService.Archive(_tmp.Root, Now).Success);
        MakeArchive("broken", manifest: null, stateCopy: null);

        var status = ReversaArchiveService.Inspect(_tmp.Root);

        Assert.Equal(2, status.ArchiveCount);
        Assert.Equal(1, status.ValidArchiveCount);
    }

    [Fact]
    public void No_archive_folder_counts_zero()
    {
        Extraction();
        var status = ReversaArchiveService.Inspect(_tmp.Root);
        Assert.Equal(0, status.ArchiveCount);
        Assert.Equal(0, status.ValidArchiveCount);
    }

    [Fact]
    public void Pending_steps_make_the_extraction_unfinished()
    {
        Extraction(pending: "[\"geracao\"]");
        Assert.True(ReversaArchiveService.PlanArchive(_tmp.Root, Now).IsUnfinished);
    }

    [Fact]
    public void An_open_plan_task_makes_the_extraction_unfinished()
    {
        Extraction();
        _tmp.File(".reversa/plan.md", "- [x] Scout ✅\n- [ ] Writer\n");
        Assert.True(ReversaArchiveService.PlanArchive(_tmp.Root, Now).IsUnfinished);
    }

    [Fact]
    public void Finished_plan_and_no_pending_is_finished()
    {
        Extraction();
        Assert.False(ReversaArchiveService.PlanArchive(_tmp.Root, Now).IsUnfinished);
    }

    [Fact]
    public void Without_plan_md_only_pending_counts()
    {
        Extraction();
        File.Delete(_tmp.PathOf(".reversa/plan.md"));
        Assert.False(ReversaArchiveService.PlanArchive(_tmp.Root, Now).IsUnfinished);
    }

    [Fact]
    public void Items_follow_rn03_and_leave_installer_files_alone()
    {
        Extraction();
        _tmp.File("_reversa_docs/index.md");
        _tmp.File("_reversa_bugs/b.md");
        _tmp.File("_reversa_refactor/r.md");

        var plan = ReversaArchiveService.PlanArchive(_tmp.Root, Now);

        Assert.Equal(new[]
        {
            "_reversa_sdd", "_reversa_forward", "_reversa_docs", "_reversa_bugs", "_reversa_refactor",
            ".reversa/context", ".reversa/plan.md", ".reversa/active-requirements.json",
            ".reversa/forward-autonomous.json"
        }, plan.Items);
        Assert.Equal(10, plan.FileCount);
        Assert.Equal($"_reversa_archive/{Stamp}/", plan.Destination);
    }

    [Theory]
    [InlineData("output_folder", "\"..\\\\x\"")]
    [InlineData("output_folder", "\"C:\\\\abs\"")]
    [InlineData("output_folder", "\"_reversa_archive\"")]
    [InlineData("output_folder", "\"_reversa_bugs\"")]
    [InlineData("forward_folder", "\".reversa\"")]
    [InlineData("forward_folder", "\"_reversa_sdd\"")]
    public void Invalid_folder_names_block_archiving(string field, string value)
    {
        State(StateJson(
            output: field == "output_folder" ? value : "\"_reversa_sdd\"",
            forward: field == "forward_folder" ? value : "\"_reversa_forward\""));
        _tmp.File("_reversa_sdd/domain.md");

        var plan = ReversaArchiveService.PlanArchive(_tmp.Root, Now);

        Assert.NotNull(plan.Error);
        Assert.StartsWith("Cannot archive: ", plan.Error);
        Assert.Contains(field, plan.Error);
        // The menu item stays enabled, so the click can explain why.
        Assert.True(ReversaArchiveService.Inspect(_tmp.Root).CanArchive);
    }

    [Fact]
    public void Inspect_and_plan_never_write()
    {
        Extraction();
        MakeArchive("old", Manifest(""), StateJson());
        var before = Tree();

        ReversaArchiveService.Inspect(_tmp.Root);
        ReversaArchiveService.PlanArchive(_tmp.Root, Now);

        Assert.Equal(before, Tree());
    }

    #endregion

    #region Archive, happy path (T008)

    [Fact]
    public void Archive_mirrors_the_relative_structure()
    {
        Extraction();
        var stateBytes = File.ReadAllBytes(_tmp.PathOf(".reversa/state.json"));

        var outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(ArchiveDir(), outcome.ArchivePath);
        foreach (var moved in new[]
                 {
                     "_reversa_sdd/domain.md", "_reversa_sdd/addenda/010-x.md",
                     "_reversa_forward/010-visao-reversa/requirements.md", ".reversa/context/surface.json",
                     ".reversa/plan.md", ".reversa/active-requirements.json", ".reversa/forward-autonomous.json"
                 })
        {
            Assert.False(File.Exists(_tmp.PathOf(moved)), moved);
            Assert.True(File.Exists(Archived(moved)), moved);
        }
        Assert.Equal(stateBytes, File.ReadAllBytes(Archived(".reversa/state.json")));
        Assert.Contains((_tmp.PathOf("_reversa_sdd"), Archived("_reversa_sdd")), outcome.PathMap);
    }

    [Fact]
    public void Archive_writes_the_manifest()
    {
        Extraction();

        ReversaArchiveService.Archive(_tmp.Root, Now);

        var manifest = JsonNode.Parse(File.ReadAllText(Archived(ReversaArchiveService.ManifestName)))!;
        Assert.Equal(1, (int)manifest["schema-version"]!);
        Assert.Equal(".reversa/state.json", (string)manifest["state-copy"]!);
        Assert.Equal("revisao", (string)manifest["phase"]!);
        Assert.Equal("1.3.6", (string)manifest["reversa-version"]!);
        var items = manifest["items"]!.AsArray().Select(i => ((string)i!["source"]!, (string)i["kind"]!)).ToList();
        Assert.Contains(("_reversa_sdd", "directory"), items);
        Assert.Contains((".reversa/plan.md", "file"), items);
        Assert.Contains((".reversa/forward-autonomous.json", "file"), items);
        Assert.Equal(6, items.Count);
    }

    [Fact]
    public void Archive_resets_progress_and_keeps_every_other_key()
    {
        Extraction();
        var before = ReadState();

        ReversaArchiveService.Archive(_tmp.Root, Now);

        var after = ReadState();
        Assert.Null(after["phase"]);
        Assert.Empty(after["completed"]!.AsArray());
        Assert.Empty(after["pending"]!.AsArray());
        Assert.Empty(after["checkpoints"]!.AsObject());
        foreach (var removed in new[] { "redator_progress", "regression_check", "autonomous_run" })
            Assert.False(after.ContainsKey(removed), removed);

        var progress = new HashSet<string>
        {
            "phase", "completed", "pending", "checkpoints", "redator_progress", "regression_check", "autonomous_run"
        };
        foreach (var (key, value) in before.Where(kv => !progress.Contains(kv.Key)))
            Assert.True(JsonNode.DeepEquals(value, after[key]), key);
    }

    [Fact]
    public void Archive_leaves_installer_files_identical()
    {
        Extraction();
        var keep = new[] { ".reversa/config.toml", ".reversa/version", ".reversa/_config/manifest.yaml", ".claude/skills/reversa/SKILL.md" };
        var before = keep.Select(k => File.ReadAllBytes(_tmp.PathOf(k))).ToList();

        ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.Equal(before, keep.Select(k => File.ReadAllBytes(_tmp.PathOf(k))).ToList());
        Assert.False(File.Exists(Archived(".reversa/version")));
        Assert.False(Directory.Exists(Archived(".reversa/_config")));
    }

    [Fact]
    public void Two_archives_in_the_same_second_get_a_suffix()
    {
        Extraction(domain: "# first");
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Extraction(domain: "# second");

        var outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.Equal(ArchiveDir(Stamp + "-2"), outcome.ArchivePath);
        Assert.Equal("# first", File.ReadAllText(Archived("_reversa_sdd/domain.md")));
        Assert.Equal("# second", File.ReadAllText(Archived("_reversa_sdd/domain.md", Stamp + "-2")));
    }

    [Fact]
    public void Nothing_to_archive_writes_nothing()
    {
        State(StateJson(phase: "null"));
        Installer();
        var before = Tree();

        var outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.False(outcome.Success);
        Assert.Equal("No extraction to archive.", outcome.Error);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void A_phase_only_archive_holds_the_manifest_and_the_state_copy()
    {
        State(StateJson(phase: "\"escavacao\""));

        var outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.True(outcome.Success, outcome.Error);
        var entries = Directory.EnumerateFileSystemEntries(ArchiveDir(), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(ArchiveDir(), p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal);
        Assert.Equal(new[] { ".reversa", ".reversa/state.json", ReversaArchiveService.ManifestName }, entries);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(Archived(ReversaArchiveService.ManifestName)))!["items"]!.AsArray());
        Assert.Null(ReadState()["phase"]);
    }

    [Fact]
    public void Archive_leaves_no_temporary_state_file()
    {
        Extraction();
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Assert.False(File.Exists(_tmp.PathOf(".reversa/state.json.tmp")));
    }

    #endregion

    #region Archive, failures (T009)

    [Fact]
    public void A_locked_file_cancels_archiving_and_changes_nothing()
    {
        Extraction();
        var before = Tree();

        ArchiveOutcome outcome;
        using (new FileStream(_tmp.PathOf("_reversa_sdd/domain.md"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.False(outcome.Success);
        Assert.StartsWith("Archiving failed: ", outcome.Error);
        Assert.Contains("_reversa_sdd/domain.md", outcome.Error);
        Assert.EndsWith("Nothing was changed.", outcome.Error);
        Assert.Empty(outcome.Stranded);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void A_late_failure_moves_everything_back()
    {
        Extraction();
        var before = Tree();

        // .reversa/context moves after _reversa_sdd and _reversa_forward.
        ArchiveOutcome outcome;
        using (new FileStream(_tmp.PathOf(".reversa/context/surface.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.False(outcome.Success);
        Assert.Contains(".reversa/context/surface.json", outcome.Error);
        Assert.Equal(before, Tree());
        Assert.False(File.Exists(_tmp.PathOf(".reversa/state.json.tmp")));
    }

    [Fact]
    public void An_unreadable_state_is_copied_as_is_and_not_reset()
    {
        Extraction();
        State("{ not json");

        var outcome = ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("{ not json", File.ReadAllText(_tmp.PathOf(".reversa/state.json")));
        Assert.Equal("{ not json", File.ReadAllText(Archived(".reversa/state.json")));
        Assert.True(File.Exists(Archived("_reversa_sdd/domain.md")));
        Assert.Contains(outcome.Warnings, w => w.Contains("state.json"));
    }

    #endregion

    #region .gitignore (T010)

    private byte[] Gitignore() => File.ReadAllBytes(_tmp.PathOf(".gitignore"));

    [Fact]
    public void Gitignore_gets_the_archive_line_at_the_end()
    {
        Extraction();
        _tmp.File(".gitignore", "bin/\nobj/\n");

        ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.Equal("bin/\nobj/\n_reversa_archive/\n", File.ReadAllText(_tmp.PathOf(".gitignore")));
    }

    [Theory]
    [InlineData("_reversa_archive")]
    [InlineData("_reversa_archive/")]
    [InlineData("/_reversa_archive")]
    [InlineData("  /_reversa_archive/  ")]
    public void Gitignore_with_an_equivalent_line_is_untouched(string line)
    {
        Extraction();
        _tmp.File(".gitignore", $"bin/\n{line}\nobj/\n");
        var before = Gitignore();

        ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.Equal(before, Gitignore());
    }

    [Fact]
    public void Missing_gitignore_is_not_created()
    {
        Extraction();
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Assert.False(File.Exists(_tmp.PathOf(".gitignore")));
    }

    [Fact]
    public void Gitignore_keeps_crlf_and_the_bom()
    {
        Extraction();
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        File.WriteAllBytes(_tmp.PathOf(".gitignore"), bom.Concat(Encoding.ASCII.GetBytes("bin/\r\nobj/\r\n")).ToArray());

        ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.Equal(bom.Concat(Encoding.ASCII.GetBytes("bin/\r\nobj/\r\n_reversa_archive/\r\n")), Gitignore());
    }

    [Fact]
    public void Gitignore_without_a_final_newline_gets_one_first()
    {
        Extraction();
        _tmp.File(".gitignore", "bin/\nobj/");

        ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.Equal("bin/\nobj/\n_reversa_archive/\n", File.ReadAllText(_tmp.PathOf(".gitignore")));
    }

    #endregion

    #region ListArchives / Restore (T011)

    [Fact]
    public void Archives_are_listed_most_recent_first_with_phase_and_features()
    {
        MakeArchive("20261101-090000", Manifest(""), StateJson(phase: "\"geracao\""),
            "_reversa_forward/001-a/requirements.md", "_reversa_forward/002-b/requirements.md", "_reversa_forward/notes/x.md");
        MakeArchive("20261117-090000", Manifest(""), StateJson());

        var list = ReversaArchiveService.ListArchives(_tmp.Root);

        Assert.Equal(new[] { "20261117-090000", "20261101-090000" }, list.Select(a => a.Name));
        Assert.All(list, a => Assert.True(a.IsValid, a.InvalidReason));
        Assert.Equal("geracao", list[1].Phase);
        Assert.Equal(2, list[1].FeatureCount);
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), list[1].CreatedAt);
    }

    [Fact]
    public void No_archive_folder_lists_nothing()
    {
        Assert.Empty(ReversaArchiveService.ListArchives(_tmp.Root));
    }

    public static TheoryData<string, string?, string?, string[]> InvalidArchives => new()
    {
        { "no manifest", null, StateJson(), Array.Empty<string>() },
        { "no state copy", Manifest(""), null, Array.Empty<string>() },
        { "state copy not json", Manifest(""), "{ nope", Array.Empty<string>() },
        { "state copy not an object", Manifest(""), "[]", Array.Empty<string>() },
        { "manifest not json", "{ nope", StateJson(), Array.Empty<string>() },
        { "schema version 2", """{"schema-version":2,"state-copy":".reversa/state.json","items":[]}""", StateJson(), Array.Empty<string>() },
        { "dot dot source", Manifest("""{"source":"../x","kind":"directory"}"""), StateJson(), Array.Empty<string>() },
        { "source outside rn03", Manifest("""{"source":".reversa/config.toml","kind":"file"}"""), StateJson(), new[] { ".reversa/config.toml" } },
        { "whole .reversa", Manifest("""{"source":".reversa","kind":"directory"}"""), StateJson(), new[] { ".reversa/x.md" } },
        { "kind mismatch", Manifest("""{"source":".reversa/plan.md","kind":"directory"}"""), StateJson(), new[] { ".reversa/plan.md" } },
        { "missing item", Manifest(SddItem), StateJson(), Array.Empty<string>() },
    };

    [Theory]
    [MemberData(nameof(InvalidArchives))]
    public void Invalid_archives_carry_a_reason(string scenario, string? manifest, string? stateCopy, string[] files)
    {
        MakeArchive("20261101-090000", manifest, stateCopy, files);

        var entry = Assert.Single(ReversaArchiveService.ListArchives(_tmp.Root));

        Assert.False(entry.IsValid, scenario);
        Assert.False(string.IsNullOrWhiteSpace(entry.InvalidReason));
    }

    [Fact]
    public void Restoring_with_a_current_extraction_swaps_them()
    {
        Extraction(domain: "# old");
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Extraction(phase: "\"geracao\"", domain: "# new");
        var current = ReadState();
        current["user_name"] = "Changed";
        current["autonomous"] = false;
        State(current.ToJsonString());
        var later = Now.AddMinutes(5);

        var outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), later);

        Assert.True(outcome.Success, outcome.Error);
        var newArchive = ArchiveDir("20261118-140812");
        Assert.Equal(newArchive, outcome.PreviousArchivePath);
        Assert.Equal("# new", File.ReadAllText(Path.Combine(newArchive, "_reversa_sdd", "domain.md")));
        Assert.Equal("# old", File.ReadAllText(_tmp.PathOf("_reversa_sdd/domain.md")));
        Assert.True(File.Exists(_tmp.PathOf("_reversa_forward/010-visao-reversa/requirements.md")));
        Assert.True(File.Exists(_tmp.PathOf(".reversa/plan.md")));
        Assert.False(Directory.Exists(ArchiveDir()));

        var state = ReadState();
        Assert.Equal("revisao", (string)state["phase"]!);
        Assert.Equal("Changed", (string)state["user_name"]!);
        Assert.False((bool)state["autonomous"]!);
        Assert.True(state.ContainsKey("redator_progress"));
        Assert.Equal("geracao", (string)ReadState(Path.Combine(newArchive, ".reversa", "state.json"))["phase"]!);
        Assert.False(File.Exists(_tmp.PathOf(".reversa/state.json.tmp")));
    }

    [Fact]
    public void Restore_uses_the_folder_names_of_the_archived_state()
    {
        Extraction();
        ReversaArchiveService.Archive(_tmp.Root, Now);
        var current = ReadState();
        current["output_folder"] = "docs_sdd";
        State(current.ToJsonString());

        var outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), Now.AddMinutes(1));

        Assert.True(outcome.Success, outcome.Error);
        Assert.True(File.Exists(_tmp.PathOf("_reversa_sdd/domain.md")));
        Assert.Equal("_reversa_sdd", (string)ReadState()["output_folder"]!);
    }

    [Fact]
    public void Restoring_without_a_current_extraction_archives_nothing_and_leaves_gitignore()
    {
        Extraction();
        ReversaArchiveService.Archive(_tmp.Root, Now);
        _tmp.File(".gitignore", "bin/\n");

        var outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), Now.AddMinutes(1));

        Assert.True(outcome.Success, outcome.Error);
        Assert.Null(outcome.PreviousArchivePath);
        Assert.Empty(Directory.EnumerateDirectories(_tmp.PathOf(ReversaArchiveService.ArchiveFolderName)));
        Assert.Equal("bin/\n", File.ReadAllText(_tmp.PathOf(".gitignore")));
        Assert.Equal("revisao", (string)ReadState()["phase"]!);
    }

    [Fact]
    public void A_locked_archived_file_undoes_the_whole_restore()
    {
        Extraction(domain: "# old");
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Extraction(phase: "\"geracao\"", domain: "# new");
        var before = Tree();

        ArchiveOutcome outcome;
        using (new FileStream(Archived("_reversa_sdd/domain.md"), FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), Now.AddMinutes(5));

        Assert.False(outcome.Success);
        Assert.StartsWith("Restoring failed: ", outcome.Error);
        Assert.Contains("domain.md", outcome.Error);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void A_phase_only_archive_is_valid_and_restores_the_phase()
    {
        State(StateJson(phase: "\"escavacao\""));
        ReversaArchiveService.Archive(_tmp.Root, Now);

        Assert.True(Assert.Single(ReversaArchiveService.ListArchives(_tmp.Root)).IsValid);

        var outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), Now.AddMinutes(1));

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("escavacao", (string)ReadState()["phase"]!);
        Assert.False(Directory.Exists(ArchiveDir()));
    }

    [Fact]
    public void A_folder_that_cannot_be_removed_is_a_warning_and_becomes_invalid()
    {
        Extraction();
        ReversaArchiveService.Archive(_tmp.Root, Now);

        ArchiveOutcome outcome;
        // Readable by the restore, but not deletable while this handle is open.
        using (new FileStream(Archived(".reversa/state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), Now.AddMinutes(1));

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains($"The restored archive folder could not be removed: _reversa_archive/{Stamp}.", outcome.Warnings);
        Assert.True(File.Exists(_tmp.PathOf("_reversa_sdd/domain.md")));
        Assert.Equal("revisao", (string)ReadState()["phase"]!);
        Assert.True(Directory.Exists(ArchiveDir()));
        Assert.False(Assert.Single(ReversaArchiveService.ListArchives(_tmp.Root)).IsValid);
    }

    [Fact]
    public void Restore_path_map_points_both_ways()
    {
        Extraction(domain: "# old");
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Extraction(phase: "\"geracao\"", domain: "# new");

        var outcome = ReversaArchiveService.Restore(_tmp.Root, ArchiveDir(), Now.AddMinutes(5));

        var newArchive = ArchiveDir("20261118-140812");
        Assert.Equal(Path.Combine(newArchive, "_reversa_sdd", "domain.md"),
            ReversaArchiveService.MapPath(outcome.PathMap, _tmp.PathOf("_reversa_sdd/domain.md")));
        Assert.Equal(_tmp.PathOf("_reversa_sdd/domain.md"),
            ReversaArchiveService.MapPath(outcome.PathMap, Archived("_reversa_sdd/domain.md")));
        Assert.Equal(_tmp.PathOf("README.md"), ReversaArchiveService.MapPath(outcome.PathMap, _tmp.PathOf("README.md")));
    }

    #endregion

    #region PlanRestore (T030)

    [Fact]
    public void Plan_for_a_deleted_archive()
    {
        Extraction();
        var before = Tree();

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now);

        Assert.Equal("The archive no longer exists.", plan.Error);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Plan_for_an_invalid_archive()
    {
        Extraction();
        MakeArchive(Stamp, manifest: null, StateJson());
        var before = Tree();

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now);

        Assert.StartsWith("Cannot restore: ", plan.Error);
        Assert.EndsWith(".", plan.Error);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Plan_with_an_unreadable_current_state()
    {
        MakeArchive(Stamp, Manifest(SddItem), StateJson(), "_reversa_sdd/domain.md");
        State("{ nope");
        var before = Tree();

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now);

        Assert.Equal("Cannot restore: .reversa/state.json cannot be read.", plan.Error);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Plan_with_an_archived_folder_name_outside_the_root()
    {
        State(StateJson(phase: "null"));
        MakeArchive(Stamp, Manifest(""), StateJson(output: "\"..\\\\x\""));
        var before = Tree();

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now);

        Assert.Equal("Cannot restore: output_folder points outside the folder (..\\x).", plan.Error);
        Assert.Equal(before, Tree());
    }

    [Theory]
    [InlineData("_reversa_sdd")]
    [InlineData("_REVERSA_SDD")]
    public void Plan_with_an_occupied_destination(string plainFolder)
    {
        State(StateJson(phase: "null", output: "\"docs_sdd\""));
        _tmp.File($"{plainFolder}/mine.md");
        MakeArchive(Stamp, Manifest(SddItem), StateJson(), "_reversa_sdd/domain.md");
        var before = Tree();

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now);

        Assert.Equal("Cannot restore: _reversa_sdd already exists.", plan.Error);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void A_destination_held_by_the_current_extraction_is_not_occupied()
    {
        // Same folder, other casing: it moves out first, so it does not block.
        State(StateJson(output: "\"_Reversa_Sdd\""));
        _tmp.File("_Reversa_Sdd/now.md");
        MakeArchive(Stamp, Manifest(SddItem), StateJson(), "_reversa_sdd/domain.md");

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now);

        Assert.Null(plan.Error);
    }

    [Fact]
    public void Plan_lists_both_extractions()
    {
        Extraction(domain: "# old");
        ReversaArchiveService.Archive(_tmp.Root, Now);
        Extraction(phase: "\"geracao\"", pending: "[\"revisao\"]", domain: "# new");
        var before = Tree();

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now.AddMinutes(5));

        Assert.Null(plan.Error);
        Assert.Equal(Stamp, plan.Archive!.Name);
        Assert.Equal(6, plan.Items.Count);
        Assert.Equal(7, plan.FileCount);
        Assert.NotNull(plan.Current);
        Assert.True(plan.Current!.IsUnfinished);
        Assert.Equal(7, plan.Current.FileCount);
        Assert.Equal("_reversa_archive/20261118-140812/", plan.Current.Destination);
        Assert.Equal(before, Tree());
    }

    [Fact]
    public void Plan_without_a_current_extraction_has_no_current()
    {
        Extraction();
        ReversaArchiveService.Archive(_tmp.Root, Now);

        var plan = ReversaArchiveService.PlanRestore(_tmp.Root, ArchiveDir(), Now.AddMinutes(1));

        Assert.Null(plan.Error);
        Assert.Null(plan.Current);
    }

    #endregion
}
