using MDViewer.ViewModels;

namespace MDViewer.Tests;

public class NavigationHistoryTests
{
    [Fact]
    public void Goes_back_in_reverse_order()
    {
        var history = new NavigationHistory();
        history.Push("a.md");
        history.Push("b.md");

        Assert.Equal("b.md", history.GoBack());
        Assert.Equal("a.md", history.GoBack());
        Assert.Null(history.GoBack());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Ignores_no_document_and_consecutive_duplicates()
    {
        var history = new NavigationHistory();
        history.Push(null);
        history.Push(@"C:\a.md");
        history.Push(@"c:\A.MD");

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void Keeps_non_consecutive_repeats()
    {
        var history = new NavigationHistory();
        history.Push("a.md");
        history.Push("b.md");
        history.Push("a.md");

        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void Clear_empties_the_history()
    {
        var history = new NavigationHistory();
        history.Push("a.md");
        history.Clear();

        Assert.False(history.CanGoBack);
    }

    #region Remap (feature 011)

    private static List<string> Drain(NavigationHistory history)
    {
        var result = new List<string>();
        while (history.GoBack() is { } path)
            result.Add(path);
        return result;
    }

    [Fact]
    public void Remap_swaps_the_prefix_and_keeps_the_order()
    {
        using var tmp = new TempFolder();
        var a = tmp.File("old/a.md");
        var b = tmp.File("old/b.md");
        var newA = tmp.File("new/a.md");
        var newB = tmp.File("new/b.md");
        var history = new NavigationHistory();
        history.Push(a);
        history.Push(b);

        history.Remap(p => p.Replace(tmp.PathOf("old"), tmp.PathOf("new")));

        Assert.Equal(new[] { newB, newA }, Drain(history));
    }

    [Fact]
    public void Remap_drops_null_results()
    {
        using var tmp = new TempFolder();
        var a = tmp.File("a.md");
        var b = tmp.File("b.md");
        var history = new NavigationHistory();
        history.Push(a);
        history.Push(b);

        history.Remap(p => p == a ? null : p);

        Assert.Equal(new[] { b }, Drain(history));
    }

    [Fact]
    public void Remap_drops_files_that_do_not_exist()
    {
        using var tmp = new TempFolder();
        var a = tmp.File("a.md");
        var history = new NavigationHistory();
        history.Push(a);
        history.Push(tmp.PathOf("gone.md"));

        history.Remap(p => p);

        Assert.Equal(new[] { a }, Drain(history));
    }

    [Fact]
    public void Remap_collapses_equal_entries_in_a_row()
    {
        using var tmp = new TempFolder();
        var a = tmp.File("a.md");
        var b = tmp.File("b.md");
        var c = tmp.File("c.md");
        var history = new NavigationHistory();
        history.Push(a);
        history.Push(b);
        history.Push(c);

        // b disappears, so a and c (both mapped to a) end up next to each other.
        history.Remap(p => p == b ? null : a);

        Assert.Equal(new[] { a }, Drain(history));
    }

    #endregion
}
