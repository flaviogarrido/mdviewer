using MDViewer.Services;

namespace MDViewer.Models;

/// <summary>Where a Reversa project stands (feature 012). The first match wins, in this order.</summary>
public enum GuideSituation
{
    /// <summary><c>.reversa/state.json</c> exists but is not a JSON object.</summary>
    StateUnreadable,
    /// <summary>The active feature is not finished yet.</summary>
    FeatureInProgress,
    /// <summary>The active feature has every action done but no addendum.</summary>
    FeatureDelivered,
    /// <summary>The <c>/reversa</c> extraction started and is not finished.</summary>
    DiscoveryInProgress,
    /// <summary>The extraction is finished and no feature is waiting.</summary>
    DiscoveryCompleted,
    /// <summary>Reversa is installed and nothing was extracted.</summary>
    NoExtraction
}

public enum GuideSuggestionKind
{
    /// <summary>The next command; at most one.</summary>
    Primary,
    /// <summary>Another way to go from the same situation.</summary>
    Alternative,
    /// <summary>Side work found in the folder (bugs, refactoring, pricing, paused features).</summary>
    Complementary
}

/// <summary>A Reversa command proposed as next step.</summary>
/// <param name="Command">Exactly what is copied, e.g. <c>/reversa-plan</c>.</param>
/// <param name="Hint">Short text shown with the command.</param>
/// <param name="SkillPath">The installed <c>SKILL.md</c> of the command; null when it is not installed.</param>
public sealed record GuideSuggestion(string Command, string Hint, string? SkillPath, GuideSuggestionKind Kind)
{
    public bool IsPrimary => Kind == GuideSuggestionKind.Primary;
    public bool CanOpenSkill => SkillPath != null;
    public string CopyLabel => $"Copy {Command}";
    public string OpenToolTip => SkillPath != null ? $"Open the {Command} skill" : $"{Command} is not installed in this folder";
}

/// <summary>
/// "Where am I / next step" of a Reversa project, as read by
/// <see cref="ReversaGuideService"/>. Read-only snapshot, rebuilt with the
/// <see cref="ReversaProject"/> on every folder open and refresh.
/// </summary>
/// <param name="Suggestions">Primary first, then alternatives, then complementary ones.</param>
public sealed record ReversaGuide(
    GuideSituation Situation, string Title, string Detail, IReadOnlyList<GuideSuggestion> Suggestions)
{
    public GuideSuggestion? Primary => Suggestions.FirstOrDefault(s => s.IsPrimary);
}
