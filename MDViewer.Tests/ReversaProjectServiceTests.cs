using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer.Tests;

public sealed class ReversaProjectServiceTests : IDisposable
{
    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private void State(string json = "{\"version\":\"1.3.6\"}") => _tmp.File(".reversa/state.json", json);

    private static IEnumerable<string> Stages(ReversaProject p) => p.Flow.Nodes.Select(n => n.Name);

    private static FileTreeNode Stage(ReversaProject p, string name) =>
        p.Flow.Nodes.Single(n => n.Name == name);

    #region Detection

    [Fact]
    public void Plain_folder_is_not_reversa()
    {
        _tmp.File("doc.md");
        Assert.Null(ReversaProjectService.Detect(_tmp.Root));
    }

    [Fact]
    public void Reads_the_version_from_state_json()
    {
        State();
        Assert.Equal("1.3.6", ReversaProjectService.Detect(_tmp.Root)!.Version);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\": 5}")]
    public void Unreadable_state_still_detects_with_defaults(string json)
    {
        State(json);

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Null(project.Version);
        Assert.Equal("_reversa_sdd", project.OutputFolder);
        Assert.Equal("_reversa_forward", project.ForwardFolder);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\\\b")]
    [InlineData("C:")]
    [InlineData(" spaced ")]
    public void Output_folder_must_be_a_single_segment_under_the_root(string folder)
    {
        State($"{{\"output_folder\":\"{folder}\"}}");
        Assert.Equal("_reversa_sdd", ReversaProjectService.Detect(_tmp.Root)!.OutputFolder);
    }

    [Fact]
    public void Custom_output_folder_is_used()
    {
        State("{\"output_folder\":\"specs\"}");
        _tmp.File("specs/architecture.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal("specs", project.OutputFolder);
        Assert.Contains(_tmp.PathOf("specs/architecture.md"), project.FlowDocuments);
    }

    [Fact]
    public void Active_feature_outside_the_forward_folder_is_ignored()
    {
        State();
        _tmp.Dir("_reversa_forward/001-x");
        _tmp.Dir("elsewhere");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"elsewhere\"}");

        Assert.Null(ReversaProjectService.Detect(_tmp.Root)!.ActiveFeatureDir);
    }

    [Fact]
    public void Detection_never_writes_to_the_folder()
    {
        State();
        _tmp.File(".claude/skills/reversa/SKILL.md", "---\nname: reversa\n---\n");
        _tmp.File("_reversa_sdd/architecture.md");
        _tmp.File("_reversa_forward/001-a/requirements.md");
        var before = _tmp.Snapshot();

        ReversaProjectService.Detect(_tmp.Root);

        Assert.Equal(before, _tmp.Snapshot());
    }

    [Fact]
    public void Archived_extractions_never_enter_the_flow()
    {
        // RN-09 / D-19 (feature 011): the Flow only reads the current extraction.
        State();
        _tmp.File("_reversa_sdd/inventory.md");
        _tmp.File("_reversa_archive/20261118-140312/_reversa_sdd/domain.md");
        _tmp.File("_reversa_archive/20261118-140312/_reversa_forward/001-a/requirements.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Contains(_tmp.PathOf("_reversa_sdd/inventory.md"), project.FlowDocuments);
        Assert.DoesNotContain(project.FlowDocuments,
            d => d.Contains("_reversa_archive", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Skill header

    [Fact]
    public void Reads_quoted_and_folded_header_values()
    {
        var path = _tmp.File("SKILL.md",
            "---\nname: 'it''s'\ndescription: >\n  first line\n  second line\nother: x\n---\nbody");

        Assert.Equal(("it's", "first line second line"), ReversaProjectService.ReadSkillHeader(path));
    }

    [Fact]
    public void Reads_double_quoted_values()
    {
        var path = _tmp.File("SKILL.md", "---\nname: \"x\"\ndescription: \"say \\\"hi\\\"\"\n---\n");

        Assert.Equal(("x", "say \"hi\""), ReversaProjectService.ReadSkillHeader(path));
    }

    [Theory]
    [InlineData("no front matter")]
    [InlineData("---\nname: never closed\n")]
    [InlineData("")]
    public void Malformed_header_gives_nulls(string content)
    {
        var path = _tmp.File("SKILL.md", content);
        Assert.Equal((null, null), ReversaProjectService.ReadSkillHeader(path));
    }

    [Fact]
    public void Missing_file_gives_nulls()
    {
        Assert.Equal((null, null), ReversaProjectService.ReadSkillHeader(_tmp.PathOf("none.md")));
    }

    #endregion

    #region Skills view

    [Fact]
    public void Prefers_the_claude_copy_of_a_skill_and_groups_by_scenario()
    {
        State();
        _tmp.File(".agents/skills/reversa-forward/SKILL.md", "---\nname: agents copy\n---\n");
        var claude = _tmp.File(".claude/skills/reversa-forward/SKILL.md", "---\nname: claude copy\ndescription: d\n---\n");
        _tmp.File(".claude/skills/reversa-forward/references/guide.md");
        _tmp.File(".claude/skills/my-tool/SKILL.md", "---\nname: mine\n---\n");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(2, project.SkillCount);
        Assert.Equal(new[] { "D · Implement / evolve a feature", "Other" }, project.Skills.Nodes.Select(n => n.Name));
        var skill = project.Skills.Find(claude)!;
        Assert.Equal("claude copy", skill.Name);
        Assert.Equal("d", skill.Description);
        Assert.Equal(new[] { "references/guide.md" }, skill.Children.Select(c => c.Name));
    }

    [Fact]
    public void Unknown_reversa_skill_goes_to_reversa_other()
    {
        State();
        _tmp.File(".claude/skills/reversa-future/SKILL.md", "---\n---\n");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        var family = Assert.Single(project.Skills.Nodes);
        Assert.Equal("Reversa (other)", family.Name);
        Assert.Equal("reversa-future", Assert.Single(family.Children).Name); // folder name when no "name"
    }

    [Fact]
    public void No_skills_shows_a_single_notice()
    {
        State();

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(0, project.SkillCount);
        var notice = Assert.Single(project.Skills.Nodes);
        Assert.True(notice.IsGroup);
        Assert.Equal("No skills found", notice.Name);
    }


    private void Skill(string name, string header = "") =>
        _tmp.File($".claude/skills/{name}/SKILL.md", $"---\n{header}---\n");

    private const string DiscoveryDone =
        "{\"phase\":\"revisao\",\"completed\":[\"reconhecimento\",\"escavacao\",\"interpretacao\",\"geracao\",\"revisao\"],\"pending\":[]}";

    [Fact]
    public void Groups_follow_the_map_scenarios_then_the_other_teams()
    {
        State();
        foreach (var name in new[]
                 {
                     "my-skill", "impeccable", "reversa-visor", "reversa-migrate", "reversa-n8n",
                     "reversa-forward", "reversa-brainstorm", "reversa", "reversa-docs", "reversa-future"
                 })
            Skill(name);

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(
            new[]
            {
                "A · Legacy system", "B · Raw idea", "D · Implement / evolve a feature", "H · n8n workflow",
                "Migration", "Docs", "Independent agents", "Front-end", "Reversa (other)", "Other"
            },
            project.Skills.Nodes.Select(n => n.Name));
    }

    [Fact]
    public void Skills_of_a_group_come_in_order_of_use()
    {
        State();
        foreach (var name in new[]
                 {
                     "reversa-plan", "reversa-forward-autonomous", "reversa-coding", "reversa-requirements",
                     "reversa-forward", "reversa-sync"
                 })
            Skill(name);

        var group = Assert.Single(ReversaProjectService.Detect(_tmp.Root)!.Skills.Nodes);

        Assert.Equal(
            new[]
            {
                "reversa-forward", "reversa-requirements", "reversa-plan", "reversa-coding", "reversa-sync",
                "reversa-forward-autonomous"
            },
            group.Children.Select(c => c.Name));
    }

    [Fact]
    public void Skills_without_a_place_come_last_by_name()
    {
        State();
        foreach (var name in new[] { "tool-10", "tool-2", "reversa-zeta", "reversa-alpha" })
            Skill(name);

        var groups = ReversaProjectService.Detect(_tmp.Root)!.Skills.Nodes;

        Assert.Equal(new[] { "reversa-alpha", "reversa-zeta" }, groups[0].Children.Select(c => c.Name));
        Assert.Equal(new[] { "tool-2", "tool-10" }, groups[1].Children.Select(c => c.Name));
    }

    [Fact]
    public void Skills_carry_the_state_of_the_map()
    {
        State(DiscoveryDone);
        Skill("reversa-requirements", "description: d\n");
        Skill("reversa-plan");
        Skill("reversa-to-do");
        Skill("reversa-coding", "description: c\n");
        _tmp.File("_reversa_forward/014-x/requirements.md");
        _tmp.File("_reversa_forward/014-x/roadmap.md");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"_reversa_forward/014-x\"}");

        var skills = Assert.Single(ReversaProjectService.Detect(_tmp.Root)!.Skills.Nodes).Children;

        Assert.Equal(new[] { "✅", "✅", "▶️", "🧩" }, skills.Select(s => s.Icon));
        Assert.Equal(new[] { false, false, true, false }, skills.Select(s => s.IsHighlighted));
        Assert.Equal("Done in this folder\nd", skills[0].ToolTipText);
        Assert.Equal("Done in this folder", skills[1].ToolTipText);
        Assert.Equal("Next step", skills[2].ToolTipText);
        Assert.Equal("c", skills[3].ToolTipText);
    }

    [Fact]
    public void Unreadable_state_keeps_the_groups_without_marks()
    {
        State("not json");
        Skill("reversa");
        Skill("reversa-forward");

        var groups = ReversaProjectService.Detect(_tmp.Root)!.Skills.Nodes;

        Assert.Equal(new[] { "A · Legacy system", "D · Implement / evolve a feature" }, groups.Select(g => g.Name));
        Assert.All(groups.SelectMany(g => g.Children), s =>
        {
            Assert.Equal("🧩", s.Icon);
            Assert.False(s.IsHighlighted);
        });
    }

    [Fact]
    public void Every_skill_of_a_map_scenario_is_in_the_group_of_its_letter()
    {
        State();
        var scenarios = ReversaMapService.Layout.Where(l => l.Letter != null).ToList();
        foreach (var skill in scenarios.SelectMany(l => l.Skills))
            Skill(skill);

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(8, scenarios.Count);
        foreach (var (letter, title, skills) in scenarios)
        {
            var group = project.Skills.Nodes.Single(n => n.Name == $"{letter} · {title}");
            Assert.Equal(skills, group.Children.Select(c => c.Name));
        }
    }

    #endregion

    #region Flow view

    [Fact]
    public void Core_stages_without_documents_are_pending_and_optional_ones_omitted()
    {
        State();

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(new[]
        {
            "Reconnaissance (pending)", "Excavation (pending)", "Interpretation (pending)",
            "Architecture (pending)", "Specification (pending)", "Review (pending)", "Evolution (pending)"
        }, Stages(project));
        Assert.All(project.Flow.Nodes, n => Assert.True(n.IsPending));
        Assert.Empty(project.FlowDocuments);
    }

    [Fact]
    public void Documents_are_classified_by_stage_in_engineering_order()
    {
        State();
        _tmp.File("_reversa_sdd/prd.md");
        _tmp.File("_reversa_sdd/inventory.md");
        _tmp.File("_reversa_sdd/domain.md");
        _tmp.File("_reversa_sdd/architecture.md");
        _tmp.File("_reversa_sdd/billing/requirements.md");
        _tmp.File("_reversa_sdd/gaps.md");
        _tmp.File("_reversa_sdd/loose-note.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal("prd.md", Assert.Single(Stage(project, "Conception").Children).Name);
        Assert.Equal("billing", Assert.Single(Stage(project, "Specification").Children).Name);
        Assert.Equal("loose-note.md", Assert.Single(Stage(project, "Other").Children).Name);
        Assert.Equal(
            new[] { "prd.md", "inventory.md", "domain.md", "architecture.md", "requirements.md", "gaps.md", "loose-note.md" },
            project.FlowDocuments.Select(Path.GetFileName));
    }

    [Fact]
    public void Each_document_appears_once_in_the_flow()
    {
        State();
        _tmp.File("_reversa_sdd/architecture.md");
        _tmp.File("_reversa_sdd/architecture/c4.md");
        _tmp.File("_reversa_sdd/flowcharts/login.md");

        var docs = ReversaProjectService.Detect(_tmp.Root)!.FlowDocuments;

        Assert.Equal(docs.Count, docs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, docs.Count);
    }

    [Fact]
    public void Features_follow_the_pipeline_order_with_the_addendum_last()
    {
        State();
        _tmp.File("_reversa_forward/001-login/onboarding.md");
        _tmp.File("_reversa_forward/001-login/actions.md");
        _tmp.File("_reversa_forward/001-login/requirements.md");
        _tmp.File("_reversa_forward/001-login/zzz-extra.md");
        _tmp.File("_reversa_sdd/addenda/001-login.md");
        _tmp.File("_reversa_forward/10-later/requirements.md");
        _tmp.File("_reversa_forward/2-earlier/requirements.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        var evolution = Stage(project, "Evolution");
        Assert.Equal(new[] { "001-login", "2-earlier", "10-later" }, evolution.Children.Select(c => c.Name));
        Assert.Equal(
            new[] { "requirements.md", "actions.md", "onboarding.md", "zzz-extra.md", "addenda/001-login.md" },
            evolution.Children[0].Children.Select(c => c.Name));
    }

    [Fact]
    public void Active_feature_is_highlighted()
    {
        State();
        _tmp.File("_reversa_forward/001-a/requirements.md");
        _tmp.File("_reversa_forward/002-b/requirements.md");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"_reversa_forward/002-b\"}");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(_tmp.PathOf("_reversa_forward/002-b"), project.ActiveFeatureDir);
        var features = Stage(project, "Evolution").Children;
        Assert.False(features[0].IsHighlighted);
        Assert.True(features[1].IsHighlighted);
    }


    [Fact]
    public void Stages_point_to_their_place_on_the_map()
    {
        State();
        _tmp.File("_reversa_sdd/inventory.md");
        _tmp.File("_reversa_sdd/something-else.md");
        _tmp.File("_reversa_bugs/bug.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal("Map: A · Legacy system (/reversa), Scout", Stage(project, "Reconnaissance").ToolTipText);
        Assert.Equal("Map: E · I have a bug (/reversa-debugger)", Stage(project, "Bugs").ToolTipText);
        Assert.Equal(
            "No documents yet. Map: D · Implement / evolve a feature (/reversa-forward)",
            Stage(project, "Evolution (pending)").ToolTipText);
        Assert.Null(Stage(project, "Other").ToolTipText);
    }

    [Theory]
    [InlineData(new string[0], "no requirements")]
    [InlineData(new[] { "requirements.md" }, "requirements")]
    [InlineData(new[] { "requirements.md", "roadmap.md" }, "plan")]
    public void Active_feature_shows_its_forward_stage(string[] files, string stage)
    {
        State();
        _tmp.File("_reversa_forward/001-a/requirements.md");
        _tmp.Dir("_reversa_forward/002-b");
        _tmp.File("_reversa_forward/002-b/notes.md");
        foreach (var file in files)
            _tmp.File($"_reversa_forward/002-b/{file}");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"_reversa_forward/002-b\"}");

        var features = Stage(ReversaProjectService.Detect(_tmp.Root)!, "Evolution").Children;

        Assert.Equal(new[] { "001-a", $"002-b · {stage}" }, features.Select(f => f.Name));
        Assert.Equal(_tmp.PathOf("_reversa_forward/002-b"), features[1].FullPath);
    }

    [Theory]
    [InlineData("| T1 | a | `[X]` |\n| T2 | b | `[ ]` |\n| T3 | c | `[ ]` |\n", false, "coding 1/3")]
    [InlineData("| T1 | a | `[X]` |\n", false, "delivered")]
    [InlineData("| T1 | a | `[X]` |\n", true, "synced")]
    public void Active_feature_shows_coding_progress_and_delivery(string actions, bool addendum, string stage)
    {
        State();
        _tmp.File("_reversa_forward/002-b/requirements.md");
        _tmp.File("_reversa_forward/002-b/roadmap.md");
        _tmp.File("_reversa_forward/002-b/actions.md", actions);
        if (addendum) _tmp.File("_reversa_sdd/addenda/002-b.md");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"_reversa_forward/002-b\"}");

        var feature = Assert.Single(Stage(ReversaProjectService.Detect(_tmp.Root)!, "Evolution").Children);

        Assert.Equal($"002-b · {stage}", feature.Name);
        Assert.True(feature.IsHighlighted);
    }

    [Fact]
    public void Features_have_no_stage_without_an_active_one_or_with_unreadable_state()
    {
        State();
        _tmp.File("_reversa_forward/001-a/requirements.md");
        Assert.Equal("001-a", Assert.Single(Stage(ReversaProjectService.Detect(_tmp.Root)!, "Evolution").Children).Name);

        State("not json");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"_reversa_forward/001-a\"}");
        Assert.Equal("001-a", Assert.Single(Stage(ReversaProjectService.Detect(_tmp.Root)!, "Evolution").Children).Name);
    }

    #endregion
}
