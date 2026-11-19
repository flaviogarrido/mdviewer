using System.Net;
using System.Text;
using MDViewer.Models;

namespace MDViewer.Services;

/// <summary>
/// Builds the Reversa usage map (feature 013): the eight starting scenarios
/// with their commands, marked with what the folder proves is done and what
/// comes next, and the page that shows it. Nothing is read here: every fact
/// comes from the <see cref="ReversaGuide"/>. The table is fixed for
/// Reversa 1.3.6.
/// </summary>
public static class ReversaMapService
{
    /// <param name="Phase">Discovery phase (<c>state.json</c> name) the step stands for.</param>
    /// <param name="DoneFrom">Forward stage from which the step is proven done.</param>
    /// <param name="NeedsSync">Done only when the feature also has its addendum.</param>
    private sealed record StepDef(
        string Skill, string Label, bool Optional = false, string? Phase = null,
        FeatureStage? DoneFrom = null, bool NeedsSync = false);

    private sealed record GroupDef(string? Letter, string Title, string Summary, string? Entry, StepDef[] Steps);

    private static readonly GroupDef[] Scenarios =
    {
        new("A", "Legacy system", "I have an existing system and want to understand and document it.", "reversa", new StepDef[]
        {
            new("reversa-scout", "Reconnaissance: maps the surface", Phase: "reconhecimento"),
            new("reversa-archaeologist", "Excavation: analyzes the modules", Phase: "escavacao"),
            new("reversa-detective", "Interpretation: finds the why", Phase: "interpretacao"),
            new("reversa-architect", "Interpretation: draws the architecture", Phase: "interpretacao"),
            new("reversa-writer", "Generation: writes the specifications", Phase: "geracao"),
            new("reversa-reviewer", "Review: validates and lists the gaps", Phase: "revisao"),
            new("reversa-autonomous", "Runs the whole pipeline without stops", Optional: true),
        }),
        new("B", "Raw idea", "I only have an idea and want to explore and structure it.", "reversa-brainstorm", new StepDef[]
        {
            new("reversa-framer", "Frames the problem"),
            new("reversa-explorer", "Explores possibilities"),
            new("reversa-challenger", "Questions and tests the ideas"),
            new("reversa-arbiter", "Converges with trade-offs"),
            new("reversa-pre-spec", "Writes a pre-specification"),
        }),
        new("C", "New project", "I want to create a project from scratch, from the idea to the specs.", "reversa-new", new StepDef[]
        {
            new("reversa-ideator", "Structures the idea"),
            new("reversa-researcher", "Studies the audience and personas"),
            new("reversa-drafter", "Writes the full PRD"),
            new("reversa-spec-sdd", "Turns it into specifications"),
        }),
        new("D", "Implement / evolve a feature", "I have specs and want to implement or evolve a feature.", "reversa-forward", new StepDef[]
        {
            new("reversa-requirements", "Defines the requirements", DoneFrom: FeatureStage.Requirements),
            new("reversa-clarify", "Clears the open questions", Optional: true),
            new("reversa-plan", "Writes the technical plan", DoneFrom: FeatureStage.Plan),
            new("reversa-to-do", "Breaks the plan into atomic actions", DoneFrom: FeatureStage.Coding),
            new("reversa-audit", "Reviews before the execution", Optional: true),
            new("reversa-quality", "Checks the quality of the requirements", Optional: true),
            new("reversa-coding", "Implements, anchored on the specs", DoneFrom: FeatureStage.Done),
            new("reversa-add", "Small amendment after the delivery", Optional: true),
            new("reversa-sync", "Converges the delivery into the specs", DoneFrom: FeatureStage.Done, NeedsSync: true),
        }),
        new("E", "I have a bug", "I want to register, track and fix a bug.", "reversa-debugger", new StepDef[]
        {
            new("reversa-debugger-fix", "Fixes with traceability"),
        }),
        new("F", "Improve without changing behavior", "The system works and I want to improve the code safely.", "reversa-refactor", new StepDef[]
        {
            new("reversa-restructure", "Improves the structure"),
            new("reversa-simplify", "Simplifies the logic"),
            new("reversa-modularize", "Organizes in modules"),
            new("reversa-decouple", "Reduces dependencies"),
            new("reversa-optimize", "Improves performance"),
            new("reversa-standardize", "Standardizes the code"),
            new("reversa-prune", "Removes what is not used"),
        }),
        new("G", "Estimate size / price", "I want an estimate for a feature or a project.", "reversa-pricing", new StepDef[]
        {
            new("reversa-pricing-advisor", "Guides the pricing approach"),
            new("reversa-pricing-profile", "Defines the billing profile"),
            new("reversa-pricing-research", "Researches market references"),
            new("reversa-pricing-product", "Describes the product"),
            new("reversa-pricing-size", "Sizes the feature"),
            new("reversa-pricing-estimate", "Builds the estimate scenarios"),
        }),
        new("H", "n8n workflow", "My legacy system is a workflow exported from n8n.", "reversa-n8n", Array.Empty<StepDef>()),
    };

    private static readonly GroupDef[] Sections =
    {
        new(null, "After discovery", "With the specs extracted, choose the destination.", null, new StepDef[]
        {
            new("reversa-forward", "Evolve: implement features on the existing code"),
            new("reversa-migrate", "Rebuild the system on a modern stack"),
            new("reversa-docs", "Generate the visual documentation site"),
        }),
        new(null, "Independent agents", "Call them when you need.", null, new StepDef[]
        {
            new("reversa-extract-soul", "Executive essence of the project"),
            new("reversa-data-master", "Database structure"),
            new("reversa-design-system", "Tokens and style of the system"),
            new("reversa-visor", "Screens and interface, from screenshots"),
            new("reversa-reconstructor", "Bottom-up reconstruction of parts"),
        }),
        new(null, "Shortcuts", "Between cycles.", null, new StepDef[]
        {
            new("reversa-resume", "Resumes a paused feature"),
            new("reversa-principles", "Project principles and standards"),
            new("reversa-agents-help", "Catalog of the agents"),
        }),
    };

    /// <summary>
    /// Scenarios and sections in map order, each with its skills in order of
    /// use (entry command first). The Skills view groups by it (feature 014).
    /// </summary>
    internal static IReadOnlyList<(string? Letter, string Title, IReadOnlyList<string> Skills)> Layout { get; } =
        Scenarios.Concat(Sections)
            .Select(g => (g.Letter, g.Title, (IReadOnlyList<string>)(g.Entry == null
                ? g.Steps.Select(s => s.Skill).ToList()
                : g.Steps.Select(s => s.Skill).Prepend(g.Entry).ToList())))
            .ToList();

    /// <summary>"A · Legacy system": how a scenario is named outside the map page.</summary>
    internal static string ScenarioLabel(string letter)
    {
        var scenario = Layout.First(l => l.Letter == letter);
        return $"{scenario.Letter} · {scenario.Title}";
    }

    #region Build

    /// <param name="skillFiles">Installed skills: folder name → <c>SKILL.md</c> path.</param>
    public static ReversaMap Build(ReversaGuide guide, IReadOnlyDictionary<string, string> skillFiles)
    {
        var next = guide.Primary?.Command;
        var withWork = guide.Suggestions
            .Where(s => s.Kind == GuideSuggestionKind.Complementary)
            .Select(s => s.Command)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        MapStep Step(StepDef def) => new(
            $"/{def.Skill}", def.Label,
            skillFiles.TryGetValue(def.Skill, out var path) ? path : null,
            StateOf(def, guide, next), def.Optional);

        MapGroup Group(GroupDef def, bool mayBeCurrent)
        {
            var entry = def.Entry == null ? null : Step(new StepDef(def.Entry, "Start here"));
            var steps = def.Steps.Select(Step).ToList();
            var current = mayBeCurrent && next != null
                          && steps.Prepend(entry).Any(s => s != null && IsCommand(s.Command, next));
            return new MapGroup(def.Letter, def.Title, def.Summary, entry, steps, current,
                entry != null && withWork.Contains(entry.Command));
        }

        // At most one current scenario: the first that holds the next command.
        var scenarios = new List<MapGroup>();
        foreach (var def in Scenarios)
            scenarios.Add(Group(def, mayBeCurrent: !scenarios.Any(s => s.IsCurrent)));

        var sections = Sections.Select(def => Group(def, mayBeCurrent: false)).ToList();

        var nextStep = guide.Primary is { } primary
            ? new MapStep(primary.Command, "Next step", primary.SkillPath, MapStepState.Current, false)
            : null;
        return new ReversaMap(guide.Title, guide.Detail, nextStep, scenarios, sections);
    }

    private static MapStepState StateOf(StepDef def, ReversaGuide guide, string? next)
    {
        if (next != null && IsCommand($"/{def.Skill}", next)) return MapStepState.Current;

        if (def.Phase != null)
        {
            if (guide.CompletedPhases.Contains(def.Phase, StringComparer.OrdinalIgnoreCase))
                return MapStepState.Done;
            return guide.Situation == GuideSituation.DiscoveryInProgress
                   && string.Equals(guide.Phase, def.Phase, StringComparison.OrdinalIgnoreCase)
                ? MapStepState.Current
                : MapStepState.None;
        }

        if (def.DoneFrom is { } from && guide.Stage is { } stage && stage >= from && (!def.NeedsSync || guide.Synced))
            return MapStepState.Done;

        return MapStepState.None;
    }

    private static bool IsCommand(string command, string other) =>
        string.Equals(command, other, StringComparison.OrdinalIgnoreCase);

    #endregion

    #region Page

    /// <summary>
    /// The map as page content for the document template. Every text is
    /// encoded. Commands carry <c>data-skill</c> (open the skill) and
    /// <c>data-copy</c> (copy the command): the page script turns the clicks
    /// into host messages, and the host answers only while the map is shown.
    /// </summary>
    public static string ToHtml(ReversaMap map)
    {
        var html = new StringBuilder();
        html.Append("<div class=\"rv-map\"><h1>Reversa map</h1>");

        html.Append("<section class=\"rv-here\" aria-label=\"Where you are\">")
            .Append("<div class=\"rv-here-title\">").Append(Encode(map.Title)).Append("</div>")
            .Append("<div class=\"rv-here-detail\">").Append(Encode(map.Detail)).Append("</div>");
        if (map.Next is { } next)
        {
            html.Append("<div class=\"rv-next\"><span>Next step:</span> ");
            AppendCommand(html, next);
            html.Append("</div>");
        }
        html.Append("</section>");

        html.Append("<h2>What do you have, or want to do now?</h2><div class=\"rv-grid\">");
        foreach (var scenario in map.Scenarios) AppendGroup(html, scenario);
        html.Append("</div>");

        html.Append("<h2>Also on the map</h2><div class=\"rv-grid\">");
        foreach (var section in map.Sections) AppendGroup(html, section);
        html.Append("</div>");

        html.Append("<p class=\"rv-legend\"><span class=\"rv-mark rv-mark-done\">✓</span> done in this folder ")
            .Append("<span class=\"rv-mark rv-mark-now\">➤</span> next step ")
            .Append("<span class=\"rv-missing\">dimmed</span> not installed in this folder. ")
            .Append("Press F5 to refresh.</p></div>");
        return html.ToString();
    }

    private static void AppendGroup(StringBuilder html, MapGroup group)
    {
        var letter = group.Letter?.ToLowerInvariant();
        var name = group.Letter == null ? group.Title : $"{group.Letter}: {group.Title}";
        if (group.IsCurrent) name += " (you are here)";

        html.Append("<section class=\"rv-card");
        if (letter != null) html.Append(" rv-").Append(letter);
        if (group.IsCurrent) html.Append(" rv-current");
        html.Append("\" aria-label=\"").Append(Encode(name)).Append("\"><div class=\"rv-head\">");
        if (group.Letter != null)
            html.Append("<span class=\"rv-letter\" aria-hidden=\"true\">").Append(Encode(group.Letter)).Append("</span>");
        html.Append("<span class=\"rv-title\">").Append(Encode(group.Title)).Append("</span>");
        if (group.IsCurrent) html.Append("<span class=\"rv-tag\">You are here</span>");
        if (group.HasWork) html.Append("<span class=\"rv-tag rv-tag-work\">Work in this folder</span>");
        html.Append("</div><p class=\"rv-summary\">").Append(Encode(group.Summary)).Append("</p>");

        if (group.Entry is { } entry)
        {
            html.Append("<div class=\"rv-entry").Append(entry.State == MapStepState.Current ? " rv-now" : "").Append("\">");
            AppendMark(html, entry.State);
            AppendCommand(html, entry);
            html.Append("</div>");
        }

        if (group.Steps.Count > 0)
        {
            html.Append("<ol class=\"rv-steps\">");
            foreach (var step in group.Steps)
            {
                html.Append("<li").Append(step.State switch
                {
                    MapStepState.Done => " class=\"rv-done\"",
                    MapStepState.Current => " class=\"rv-now\"",
                    _ => ""
                }).Append('>');
                AppendMark(html, step.State);
                AppendCommand(html, step);
                html.Append("<span class=\"rv-label\">");
                if (step.IsOptional) html.Append("<span class=\"rv-opt\">optional</span> ");
                html.Append(Encode(step.Label)).Append("</span></li>");
            }
            html.Append("</ol>");
        }

        html.Append("</section>");
    }

    private static void AppendMark(StringBuilder html, MapStepState state) =>
        html.Append(state switch
        {
            MapStepState.Done => "<span class=\"rv-mark rv-mark-done\" role=\"img\" aria-label=\"Done\" title=\"Done in this folder\">✓</span>",
            MapStepState.Current => "<span class=\"rv-mark rv-mark-now\" role=\"img\" aria-label=\"Next step\" title=\"Next step\">➤</span>",
            _ => "<span class=\"rv-mark\" aria-hidden=\"true\"></span>"
        });

    private static void AppendCommand(StringBuilder html, MapStep step)
    {
        var command = Encode(step.Command);
        var skill = Encode(step.Skill);
        if (step.SkillPath != null)
        {
            html.Append("<a href=\"#\" class=\"rv-cmd\" data-skill=\"").Append(skill)
                .Append("\" title=\"Open the ").Append(command).Append(" skill\">").Append(command).Append("</a>");
        }
        else
        {
            html.Append("<span class=\"rv-cmd rv-missing\" title=\"Not installed in this folder\">")
                .Append(command).Append("</span>");
        }

        html.Append("<a href=\"#\" class=\"rv-copy\" role=\"button\" data-copy=\"").Append(skill)
            .Append("\" aria-label=\"Copy ").Append(command).Append("\" title=\"Copy ").Append(command)
            .Append("\">Copy</a>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    #endregion
}
