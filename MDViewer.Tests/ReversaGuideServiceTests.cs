using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer.Tests;

/// <summary>
/// "Where am I / next step" (feature 012), read through
/// <see cref="ReversaProjectService.Detect"/> like the app does.
/// </summary>
public sealed class ReversaGuideServiceTests : IDisposable
{
    private const string Feature = "_reversa_forward/012-next-step";
    private const string DiscoveryDone =
        "{\"phase\":\"revisao\",\"completed\":[\"a\",\"b\",\"c\",\"d\",\"e\"],\"pending\":[]}";
    private const string NothingExtracted = "{\"phase\":null,\"completed\":[],\"pending\":[]}";

    private readonly TempFolder _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private void State(string json = DiscoveryDone) => _tmp.File(".reversa/state.json", json);

    private void Skills(params string[] names)
    {
        foreach (var name in names)
            _tmp.File($".claude/skills/{name}/SKILL.md", $"---\nname: {name}\n---\n");
    }

    private void ActiveFeature(string dir = Feature, string extra = "")
    {
        _tmp.Dir(dir);
        _tmp.File(".reversa/active-requirements.json", $"{{\"feature-dir\":\"{dir}\"{extra}}}");
    }

    private static string Actions(int done, int open) =>
        "| ID | Descrição | Status |\n|----|-----------|--------|\n"
        + string.Concat(Enumerable.Range(0, done).Select(i => $"| T{i:000} | done | `[X]` |\n"))
        + string.Concat(Enumerable.Range(0, open).Select(i => $"| T{done + i:000} | open | `[ ]` |\n"));

    private ReversaGuide Guide() => ReversaProjectService.Detect(_tmp.Root)!.Guide!;

    private static IEnumerable<string> Commands(ReversaGuide guide, GuideSuggestionKind kind) =>
        guide.Suggestions.Where(s => s.Kind == kind).Select(s => s.Command);

    #region Situation

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Unreadable_state_suggests_nothing(string json)
    {
        State(json);
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md");

        var guide = Guide();

        Assert.Equal(GuideSituation.StateUnreadable, guide.Situation);
        Assert.Equal("Reversa state could not be read", guide.Title);
        Assert.Empty(guide.Suggestions);
    }

    [Fact]
    public void No_extraction_suggests_reversa()
    {
        State(NothingExtracted);

        var guide = Guide();

        Assert.Equal(GuideSituation.NoExtraction, guide.Situation);
        Assert.Equal("No extraction yet", guide.Title);
        Assert.Equal("/reversa", guide.Primary!.Command);
    }

    [Fact]
    public void State_without_progress_fields_is_no_extraction()
    {
        State("{\"version\":\"1.3.6\"}");
        Assert.Equal(GuideSituation.NoExtraction, Guide().Situation);
    }

    [Fact]
    public void Pending_phases_mean_discovery_in_progress()
    {
        State("{\"phase\":\"escavacao\",\"completed\":[\"reconhecimento\"],\"pending\":[\"interpretacao\"]}");

        var guide = Guide();

        Assert.Equal(GuideSituation.DiscoveryInProgress, guide.Situation);
        Assert.Equal("Phase escavacao, 1 phase completed", guide.Detail);
        Assert.Equal("/reversa", guide.Primary!.Command);
    }

    [Fact]
    public void Open_plan_task_means_discovery_in_progress()
    {
        State("{\"phase\":\"revisao\",\"completed\":[\"a\",\"b\"],\"pending\":[]}");
        _tmp.File(".reversa/plan.md", "- [x] done\n- [ ] open\n");

        var guide = Guide();

        Assert.Equal(GuideSituation.DiscoveryInProgress, guide.Situation);
        Assert.Equal("Phase revisao, 2 phases completed", guide.Detail);
    }

    [Fact]
    public void Phase_without_completed_phases_is_in_progress()
    {
        State("{\"phase\":\"reconhecimento\",\"completed\":[],\"pending\":[]}");

        var guide = Guide();

        Assert.Equal(GuideSituation.DiscoveryInProgress, guide.Situation);
        Assert.Equal("Phase reconhecimento, no phases completed", guide.Detail);
    }

    [Fact]
    public void Discovery_in_progress_without_phase_omits_it()
    {
        State("{\"completed\":[\"a\"],\"pending\":[\"b\"]}");
        Assert.Equal("1 phase completed", Guide().Detail);
    }

    [Fact]
    public void Completed_discovery_suggests_forward()
    {
        State();
        _tmp.File(".reversa/plan.md", "- [x] done\n");

        var guide = Guide();

        Assert.Equal(GuideSituation.DiscoveryCompleted, guide.Situation);
        Assert.Equal("Discovery completed", guide.Title);
        Assert.Equal("Choose a destination", guide.Detail);
        Assert.Equal("/reversa-forward", guide.Primary!.Command);
    }

    #endregion

    #region Active feature

    [Fact]
    public void Feature_folder_without_requirements()
    {
        State();
        ActiveFeature();

        var guide = Guide();

        Assert.Equal(GuideSituation.FeatureInProgress, guide.Situation);
        Assert.Equal("Feature 012-next-step: no requirements yet", guide.Title);
        Assert.Equal("/reversa-requirements", guide.Primary!.Command);
    }

    [Theory]
    [InlineData("a [DÚVIDA] b\n- [DÚVIDA] c\n", "2 open questions", "/reversa-clarify")]
    [InlineData("only [DÚVIDA] one\n", "1 open question", "/reversa-clarify")]
    [InlineData("closed\n", "Requirements are closed, ready for the plan", "/reversa-plan")]
    public void Requirements_stage(string requirements, string detail, string command)
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md", requirements);

        var guide = Guide();

        Assert.Equal(GuideSituation.FeatureInProgress, guide.Situation);
        Assert.Equal("Feature 012-next-step: requirements", guide.Title);
        Assert.Equal(detail, guide.Detail);
        Assert.Equal(command, guide.Primary!.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("# Actions\n\nNo table yet\n")]
    public void Plan_stage_until_actions_have_rows(string? actions)
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md", "still [DÚVIDA] here\n");
        _tmp.File($"{Feature}/roadmap.md");
        if (actions != null) _tmp.File($"{Feature}/actions.md", actions);

        var guide = Guide();

        Assert.Equal("Feature 012-next-step: plan", guide.Title);
        Assert.Equal("/reversa-to-do", guide.Primary!.Command);
    }

    [Fact]
    public void Coding_stage_counts_actions()
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md");
        _tmp.File($"{Feature}/roadmap.md");
        _tmp.File($"{Feature}/actions.md", Actions(done: 7, open: 5));

        var guide = Guide();

        Assert.Equal("Feature 012-next-step: coding", guide.Title);
        Assert.Equal("7 of 12 actions done", guide.Detail);
        Assert.Equal("/reversa-coding", guide.Primary!.Command);
    }

    [Fact]
    public void Action_rows_without_backticks_and_with_crlf_count()
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md");
        _tmp.File($"{Feature}/roadmap.md");
        _tmp.File($"{Feature}/actions.md", "| T001 | a | [x] |\r\n| T002 | b | [ ] |\r\n- [ ] not a row\r\n");

        Assert.Equal("1 of 2 actions done", Guide().Detail);
    }

    [Fact]
    public void Delivered_feature_without_addendum_suggests_sync()
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md");
        _tmp.File($"{Feature}/roadmap.md");
        _tmp.File($"{Feature}/actions.md", Actions(done: 3, open: 0));
        _tmp.File("_reversa_sdd/addenda/0123-other.md");
        _tmp.File("_reversa_sdd/addenda/011-other.md");

        var guide = Guide();

        Assert.Equal(GuideSituation.FeatureDelivered, guide.Situation);
        Assert.Equal("All 3 actions done, not yet synced to the extraction", guide.Detail);
        Assert.Equal("/reversa-sync", guide.Primary!.Command);
    }

    [Fact]
    public void Synced_feature_falls_back_to_the_discovery_situation()
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md");
        _tmp.File($"{Feature}/roadmap.md");
        _tmp.File($"{Feature}/actions.md", Actions(done: 3, open: 0));
        _tmp.File("_reversa_sdd/addenda/012-next-step.md");

        var guide = Guide();

        Assert.Equal(GuideSituation.DiscoveryCompleted, guide.Situation);
        Assert.Equal("Feature 012-next-step delivered and synced", guide.Detail);
        Assert.Equal("/reversa-forward", guide.Primary!.Command);
    }

    [Fact]
    public void Feature_outside_the_forward_folder_is_ignored()
    {
        State();
        _tmp.Dir("elsewhere/012-x");
        _tmp.File(".reversa/active-requirements.json", "{\"feature-dir\":\"elsewhere/012-x\"}");

        Assert.Equal(GuideSituation.DiscoveryCompleted, Guide().Situation);
    }

    [Fact]
    public void Oversized_requirements_count_as_absent()
    {
        State();
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md", new string('x', 1024 * 1024 + 1));

        Assert.Equal("/reversa-requirements", Guide().Primary!.Command);
    }

    [Fact]
    public void Archived_extractions_are_not_read()
    {
        State(NothingExtracted);
        _tmp.File("_reversa_archive/20260101-000000/_reversa_bugs/bug.md");
        _tmp.File("_reversa_archive/20260101-000000/.reversa/state.json", DiscoveryDone);
        Skills("reversa-debugger");

        var guide = Guide();

        Assert.Equal(GuideSituation.NoExtraction, guide.Situation);
        Assert.Empty(Commands(guide, GuideSuggestionKind.Complementary));
    }

    #endregion

    #region Suggestions

    [Fact]
    public void Primary_is_kept_when_its_skill_is_not_installed()
    {
        State();

        var primary = Guide().Primary!;

        Assert.Null(primary.SkillPath);
        Assert.False(primary.CanOpenSkill);
    }

    [Fact]
    public void Installed_skill_is_linked()
    {
        State();
        Skills("reversa-forward");

        Assert.Equal(_tmp.PathOf(".claude/skills/reversa-forward/SKILL.md"), Guide().Primary!.SkillPath);
    }

    [Fact]
    public void Completed_discovery_offers_the_installed_destinations()
    {
        State();
        Skills("reversa-forward", "reversa-docs");

        Assert.Equal(new[] { "/reversa-docs" }, Commands(Guide(), GuideSuggestionKind.Alternative));

        Skills("reversa-migrate");
        Assert.Equal(new[] { "/reversa-migrate", "/reversa-docs" }, Commands(Guide(), GuideSuggestionKind.Alternative));
    }

    [Fact]
    public void No_extraction_offers_the_starting_scenarios()
    {
        State(NothingExtracted);
        Skills("reversa", "reversa-new", "reversa-brainstorm");

        Assert.Equal(new[] { "/reversa-new", "/reversa-brainstorm" },
            Commands(Guide(), GuideSuggestionKind.Alternative));
    }

    [Fact]
    public void Feature_in_progress_has_no_alternatives()
    {
        State();
        Skills("reversa-migrate", "reversa-docs", "reversa-requirements");
        ActiveFeature();

        Assert.Empty(Commands(Guide(), GuideSuggestionKind.Alternative));
    }

    [Fact]
    public void Team_folders_with_markdown_add_complementary_commands()
    {
        State();
        Skills("reversa-debugger", "reversa-refactor", "reversa-pricing");
        _tmp.File("_reversa_bugs/credito/bug-001.md");
        _tmp.Dir("_reversa_refactor/empty");
        _tmp.File("_reversa_refactor/notes.txt", "not markdown");
        _tmp.File("_reversa_sdd/_pricing/profile.md");

        Assert.Equal(new[] { "/reversa-debugger", "/reversa-pricing" },
            Commands(Guide(), GuideSuggestionKind.Complementary));
    }

    [Fact]
    public void Complementary_command_needs_its_skill()
    {
        State();
        _tmp.File("_reversa_bugs/bug-001.md");

        Assert.Empty(Commands(Guide(), GuideSuggestionKind.Complementary));
    }

    [Fact]
    public void Paused_features_offer_resume()
    {
        State();
        Skills("reversa-resume");
        _tmp.File(".reversa/active-requirements.json", "{\"paused-features\":[{},{}]}");

        var resume = Guide().Suggestions.Single(s => s.Kind == GuideSuggestionKind.Complementary);

        Assert.Equal("/reversa-resume", resume.Command);
        Assert.Equal("Also: 2 paused features", resume.Hint);
    }

    [Fact]
    public void Primary_comes_first_and_only_once()
    {
        State();
        Skills("reversa-forward", "reversa-migrate", "reversa-docs", "reversa-debugger");
        _tmp.File("_reversa_bugs/bug.md");

        var guide = Guide();

        Assert.Equal(
            new[]
            {
                GuideSuggestionKind.Primary, GuideSuggestionKind.Alternative,
                GuideSuggestionKind.Alternative, GuideSuggestionKind.Complementary
            },
            guide.Suggestions.Select(s => s.Kind));
        Assert.All(guide.Suggestions, s => Assert.StartsWith("/", s.Command));
    }

    [Fact]
    public void Building_the_guide_writes_nothing()
    {
        State();
        Skills("reversa-forward", "reversa-debugger");
        ActiveFeature();
        _tmp.File($"{Feature}/requirements.md", "[DÚVIDA]\n");
        _tmp.File("_reversa_bugs/bug.md");
        var before = _tmp.Snapshot();

        Guide();

        Assert.Equal(before, _tmp.Snapshot());
    }

    #endregion
}
