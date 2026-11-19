using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer.Tests;

/// <summary>
/// The Reversa usage map (feature 013), read through
/// <see cref="ReversaProjectService.Detect"/> like the app does.
/// </summary>
public sealed class ReversaMapServiceTests : IDisposable
{
    private const string Feature = "_reversa_forward/013-map";
    private const string DiscoveryDone =
        "{\"phase\":\"revisao\",\"completed\":[\"reconhecimento\",\"escavacao\",\"interpretacao\",\"geracao\",\"revisao\"],\"pending\":[]}";

    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private void State(string json = DiscoveryDone) => _tmp.File(".reversa/state.json", json);

    private void Skills(params string[] names)
    {
        foreach (var name in names)
            _tmp.File($".claude/skills/{name}/SKILL.md", $"---\nname: {name}\n---\n");
    }

    private void ActiveFeature(params string[] files)
    {
        _tmp.Dir(Feature);
        _tmp.File(".reversa/active-requirements.json", $"{{\"feature-dir\":\"{Feature}\"}}");
        foreach (var file in files)
            _tmp.File($"{Feature}/{file}");
    }

    private ReversaMap Map() => ReversaProjectService.Detect(_tmp.Root)!.Map!;

    private static MapGroup Scenario(ReversaMap map, string letter) => map.Scenarios.Single(s => s.Letter == letter);

    private static MapStepState StateOf(MapGroup group, string command) =>
        group.AllSteps.Single(s => s.Command == command).State;

    #region Model

    [Fact]
    public void Has_the_eight_scenarios_in_order_with_their_entry_commands()
    {
        State();

        var map = Map();

        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }, map.Scenarios.Select(s => s.Letter));
        Assert.Equal(
            new[]
            {
                "/reversa", "/reversa-brainstorm", "/reversa-new", "/reversa-forward",
                "/reversa-debugger", "/reversa-refactor", "/reversa-pricing", "/reversa-n8n"
            },
            map.Scenarios.Select(s => s.Entry!.Command));
        Assert.Equal(new[] { "After discovery", "Independent agents", "Shortcuts" }, map.Sections.Select(s => s.Title));
        Assert.All(map.Sections, s => Assert.Null(s.Entry));
    }

    [Fact]
    public void Repeats_the_guide_situation_and_next_command()
    {
        State();
        ActiveFeature("requirements.md", "roadmap.md");

        var project = ReversaProjectService.Detect(_tmp.Root)!;

        Assert.Equal(project.Guide!.Title, project.Map!.Title);
        Assert.Equal(project.Guide.Detail, project.Map.Detail);
        Assert.Equal("/reversa-to-do", project.Map.Next!.Command);
    }

    [Fact]
    public void Forward_steps_follow_the_feature_stage()
    {
        State();
        ActiveFeature("requirements.md", "roadmap.md");

        var forward = Scenario(Map(), "D");

        Assert.True(forward.IsCurrent);
        Assert.Equal(MapStepState.Done, StateOf(forward, "/reversa-requirements"));
        Assert.Equal(MapStepState.Done, StateOf(forward, "/reversa-plan"));
        Assert.Equal(MapStepState.Current, StateOf(forward, "/reversa-to-do"));
        Assert.Equal(MapStepState.None, StateOf(forward, "/reversa-coding"));
        Assert.Equal(MapStepState.None, StateOf(forward, "/reversa-clarify"));
        Assert.Equal(MapStepState.None, StateOf(forward, "/reversa-forward"));
    }

    [Fact]
    public void Delivered_feature_marks_everything_but_sync()
    {
        State();
        ActiveFeature("requirements.md", "roadmap.md");
        _tmp.File($"{Feature}/actions.md", "| T001 | a | `[X]` |\n");

        var forward = Scenario(Map(), "D");

        Assert.Equal(MapStepState.Done, StateOf(forward, "/reversa-to-do"));
        Assert.Equal(MapStepState.Done, StateOf(forward, "/reversa-coding"));
        Assert.Equal(MapStepState.Current, StateOf(forward, "/reversa-sync"));
    }

    [Fact]
    public void Synced_feature_marks_sync_and_points_back_to_the_entry()
    {
        State();
        ActiveFeature("requirements.md", "roadmap.md");
        _tmp.File($"{Feature}/actions.md", "| T001 | a | `[X]` |\n");
        _tmp.File("_reversa_sdd/addenda/013-map.md");

        var forward = Scenario(Map(), "D");

        Assert.Equal(MapStepState.Done, StateOf(forward, "/reversa-sync"));
        Assert.Equal(MapStepState.Current, StateOf(forward, "/reversa-forward"));
        Assert.True(forward.IsCurrent);
    }

    [Fact]
    public void Optional_steps_are_never_marked_done()
    {
        State();
        ActiveFeature("requirements.md", "roadmap.md");
        _tmp.File($"{Feature}/actions.md", "| T001 | a | `[X]` |\n");
        _tmp.File("_reversa_sdd/addenda/013-map.md");

        var optional = Map().Scenarios.SelectMany(s => s.Steps).Where(s => s.IsOptional).ToList();

        Assert.NotEmpty(optional);
        Assert.All(optional, s => Assert.NotEqual(MapStepState.Done, s.State));
    }

    [Fact]
    public void Discovery_phases_follow_state_json()
    {
        State("{\"phase\":\"interpretacao\",\"completed\":[\"reconhecimento\",\"escavacao\"],\"pending\":[\"geracao\"]}");

        var legacy = Scenario(Map(), "A");

        Assert.True(legacy.IsCurrent);
        Assert.Equal(MapStepState.Current, StateOf(legacy, "/reversa"));
        Assert.Equal(MapStepState.Done, StateOf(legacy, "/reversa-scout"));
        Assert.Equal(MapStepState.Done, StateOf(legacy, "/reversa-archaeologist"));
        Assert.Equal(MapStepState.Current, StateOf(legacy, "/reversa-detective"));
        Assert.Equal(MapStepState.Current, StateOf(legacy, "/reversa-architect"));
        Assert.Equal(MapStepState.None, StateOf(legacy, "/reversa-writer"));
    }

    [Fact]
    public void Completed_discovery_marks_every_phase_and_no_phase_as_current()
    {
        State();

        var map = Map();
        var legacy = Scenario(map, "A");

        Assert.All(legacy.Steps.Where(s => !s.IsOptional), s => Assert.Equal(MapStepState.Done, s.State));
        Assert.False(legacy.IsCurrent);
        Assert.Single(map.Scenarios, s => s.IsCurrent);
        Assert.True(Scenario(map, "D").IsCurrent);
    }

    [Fact]
    public void Unreadable_state_has_no_current_scenario_and_no_next()
    {
        State("not json");

        var map = Map();

        Assert.Null(map.Next);
        Assert.DoesNotContain(map.Scenarios, s => s.IsCurrent);
        Assert.All(map.Scenarios.SelectMany(s => s.AllSteps), s => Assert.Equal(MapStepState.None, s.State));
    }

    [Fact]
    public void Team_folders_flag_their_scenario()
    {
        State();
        Skills("reversa-debugger");
        _tmp.File("_reversa_bugs/bug.md");

        var map = Map();

        Assert.True(Scenario(map, "E").HasWork);
        Assert.DoesNotContain(map.Scenarios, s => s.Letter != "E" && s.HasWork);
    }

    [Fact]
    public void Installed_skills_are_linked_and_the_others_are_not()
    {
        State();
        Skills("reversa-brainstorm");

        var map = Map();

        Assert.Equal(_tmp.PathOf(".claude/skills/reversa-brainstorm/SKILL.md"), Scenario(map, "B").Entry!.SkillPath);
        Assert.Null(Scenario(map, "H").Entry!.SkillPath);
    }

    [Fact]
    public void Find_knows_only_commands_of_the_map()
    {
        State();

        var map = Map();

        Assert.Equal("/reversa-debugger", map.Find("reversa-debugger")!.Command);
        Assert.Equal("/reversa-forward", map.Find("REVERSA-FORWARD")!.Command);
        Assert.Null(map.Find("format-c"));
        Assert.Null(map.Find("impeccable"));
    }

    #endregion

    #region Page

    [Fact]
    public void Page_lists_every_command_with_its_actions()
    {
        State();
        Skills("reversa-brainstorm");

        var map = Map();
        var html = ReversaMapService.ToHtml(map);

        foreach (var step in map.Scenarios.Concat(map.Sections).SelectMany(g => g.AllSteps))
        {
            Assert.Contains($">{step.Command}<", html);
            Assert.Contains($"data-copy=\"{step.Skill}\"", html);
        }
        Assert.Contains("data-skill=\"reversa-brainstorm\"", html);
        Assert.DoesNotContain("data-skill=\"reversa-n8n\"", html);
        Assert.Contains("<span class=\"rv-cmd rv-missing\" title=\"Not installed in this folder\">/reversa-n8n</span>", html);
    }

    [Fact]
    public void Page_marks_states_with_alternative_text()
    {
        State();
        ActiveFeature("requirements.md", "roadmap.md");

        var html = ReversaMapService.ToHtml(Map());

        Assert.Contains("aria-label=\"Done\"", html);
        Assert.Contains("aria-label=\"Next step\"", html);
        Assert.Contains("aria-label=\"D: Implement / evolve a feature (you are here)\"", html);
        Assert.Contains("You are here", html);
    }

    [Fact]
    public void Page_encodes_text_that_comes_from_the_folder()
    {
        State("{\"phase\":\"<b>x</b>\",\"completed\":[],\"pending\":[\"a\"]}");

        var html = ReversaMapService.ToHtml(Map());

        Assert.DoesNotContain("<b>x</b>", html);
        Assert.Contains("&lt;b&gt;x&lt;/b&gt;", html);
    }

    [Fact]
    public void Page_has_no_script_and_no_file_path()
    {
        State();
        Skills("reversa-forward");

        var html = ReversaMapService.ToHtml(Map());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tmp.Root, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SKILL.md", html);
    }

    [Fact]
    public void Rendered_page_keeps_the_template_script_policy()
    {
        State();

        var page = new MarkdownRenderService().RenderHtmlPage(ReversaMapService.ToHtml(Map()), isDarkTheme: true);

        Assert.Contains("data-theme=\"dark\"", page);
        Assert.Contains("Content-Security-Policy", page);
        Assert.Contains("class=\"rv-map\"", page);
        Assert.DoesNotContain("{{", page);
    }

    #endregion
}
