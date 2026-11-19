using MDViewer.ViewModels;

namespace MDViewer.Tests;

public class FullscreenLayoutTests
{
    [Fact]
    public void Starts_inactive()
    {
        var layout = new FullscreenLayout();

        Assert.False(layout.IsActive);
        Assert.Null(layout.Exit());
    }

    [Fact]
    public void Entering_keeps_the_tree_width_and_leaving_returns_it()
    {
        var layout = new FullscreenLayout();

        Assert.True(layout.Enter(320));
        Assert.True(layout.IsActive);
        Assert.Equal(320, layout.Exit());
        Assert.False(layout.IsActive);
    }

    [Fact]
    public void Entering_twice_keeps_the_first_width()
    {
        // The second call would see the hidden column (width 0).
        var layout = new FullscreenLayout();
        layout.Enter(320);

        Assert.False(layout.Enter(0));
        Assert.Equal(320, layout.SavedTreeWidth);
    }

    [Fact]
    public void Width_to_persist_is_the_saved_one_while_active()
    {
        var layout = new FullscreenLayout();
        Assert.Equal(280, layout.WidthToPersist(280));

        layout.Enter(320);
        Assert.Equal(320, layout.WidthToPersist(0));

        layout.Exit();
        Assert.Equal(300, layout.WidthToPersist(300));
    }
}
