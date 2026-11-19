namespace MDViewer.ViewModels;

/// <summary>
/// Fullscreen mode (feature 015): the document takes the whole width of the
/// window and only the menu and the toolbar stay. Session state only. Keeps
/// the tree panel width of the moment the mode was entered, so leaving the
/// mode and closing the window both use that width instead of the hidden 0.
/// </summary>
public sealed class FullscreenLayout
{
    public bool IsActive { get; private set; }

    /// <summary>Tree panel width when the mode was entered.</summary>
    public double SavedTreeWidth { get; private set; }

    /// <returns>True when the mode was off and is now on.</returns>
    public bool Enter(double currentTreeWidth)
    {
        if (IsActive) return false;
        IsActive = true;
        SavedTreeWidth = currentTreeWidth;
        return true;
    }

    /// <returns>The tree width to restore, or null when the mode was already off.</returns>
    public double? Exit()
    {
        if (!IsActive) return null;
        IsActive = false;
        return SavedTreeWidth;
    }

    /// <summary>The tree width to store when the window closes.</summary>
    public double WidthToPersist(double currentTreeWidth) =>
        IsActive ? SavedTreeWidth : currentTreeWidth;
}
