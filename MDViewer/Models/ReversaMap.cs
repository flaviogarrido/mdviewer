using MDViewer.Services;

namespace MDViewer.Models;

public enum MapStepState
{
    None,
    /// <summary>The folder proves the step was done.</summary>
    Done,
    /// <summary>The step is what the guide suggests next, or the discovery phase in progress.</summary>
    Current
}

/// <summary>A Reversa command or agent of the map.</summary>
/// <param name="Command">Exactly what is copied, e.g. <c>/reversa-plan</c>.</param>
/// <param name="SkillPath">The installed <c>SKILL.md</c>; null when the skill is not installed.</param>
public sealed record MapStep(string Command, string Label, string? SkillPath, MapStepState State, bool IsOptional)
{
    /// <summary>The skill folder name: the command without the slash.</summary>
    public string Skill => Command[1..];
}

/// <summary>
/// A scenario of the map (A to H, with an entry command) or a section of
/// commands outside the scenarios (no letter, no entry).
/// </summary>
/// <param name="IsCurrent">The scenario holds the command the guide suggests next.</param>
/// <param name="HasWork">The folder has work of this scenario (bugs, refactoring, pricing).</param>
public sealed record MapGroup(
    string? Letter, string Title, string Summary, MapStep? Entry, IReadOnlyList<MapStep> Steps,
    bool IsCurrent, bool HasWork)
{
    public IEnumerable<MapStep> AllSteps => Entry == null ? Steps : Steps.Prepend(Entry);
}

/// <summary>
/// The Reversa usage map of a project (feature 013), as built by
/// <see cref="ReversaMapService"/> from the <see cref="ReversaGuide"/>.
/// Read-only snapshot, rebuilt with the <see cref="ReversaProject"/>.
/// </summary>
/// <param name="Title">Where the project stands (the guide title).</param>
/// <param name="Next">The command the guide suggests next; null when there is none.</param>
public sealed record ReversaMap(
    string Title, string Detail, MapStep? Next,
    IReadOnlyList<MapGroup> Scenarios, IReadOnlyList<MapGroup> Sections)
{
    /// <summary>The first step with <paramref name="skill"/> as command, anywhere in the map.</summary>
    public MapStep? Find(string skill) =>
        Scenarios.Concat(Sections)
            .SelectMany(g => g.AllSteps)
            .Concat(Next == null ? Array.Empty<MapStep>() : new[] { Next })
            .FirstOrDefault(s => string.Equals(s.Skill, skill, StringComparison.OrdinalIgnoreCase));
}
