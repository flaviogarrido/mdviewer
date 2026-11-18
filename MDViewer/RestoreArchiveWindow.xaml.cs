using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using MDViewer.Models;
using MDViewer.Services;

namespace MDViewer;

/// <summary>
/// "Restore extraction" (feature 011, D-15): lists the archives (invalid ones
/// disabled, with the reason) and, for the selected one, shows what comes back
/// and what is archived first. Read-only: the restore itself runs in the main
/// window after <see cref="SelectedArchive"/> is set.
/// </summary>
public partial class RestoreArchiveWindow : Window
{
    private readonly string _root;

    // Bumped on every selection: a plan computed for an older selection is dropped (R-08).
    private int _selectionVersion;

    public RestoreArchiveWindow(string root, IReadOnlyList<ArchiveEntry> archives)
    {
        InitializeComponent();
        _root = root;

        var items = archives.Select(a => new ArchiveListItem(a)).ToList();
        ArchiveList.ItemsSource = items;
        SummaryText.Text = items.Any(i => i.IsValid)
            ? "Choose an archived extraction."
            : "No archived extraction can be restored. The list shows why.";

        Loaded += (_, _) =>
        {
            if (items.FirstOrDefault(i => i.IsValid) is { } first)
            {
                ArchiveList.SelectedItem = first;
                ArchiveList.UpdateLayout();
                if (ArchiveList.ItemContainerGenerator.ContainerFromItem(first) is ListBoxItem container)
                {
                    container.Focus();
                    return;
                }
            }
            ArchiveList.Focus();
        };
    }

    /// <summary>The archive to restore; set only when the user chose "Restore".</summary>
    public ArchiveEntry? SelectedArchive { get; private set; }

    private void Window_SourceInitialized(object? sender, EventArgs e) => ThemeApplier.ApplyTitleBar(this);

    private async void ArchiveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var version = ++_selectionVersion;
        RestoreButton.IsEnabled = false;

        if (ArchiveList.SelectedItem is not ArchiveListItem { IsValid: true } item)
        {
            SummaryText.Text = "Choose an archived extraction.";
            return;
        }

        SummaryText.Text = "Calculating…";
        string summary;
        var canRestore = false;
        try
        {
            var plan = await Task.Run(() => ReversaArchiveService.PlanRestore(_root, item.Entry.FullPath, DateTime.Now));
            summary = plan.Error ?? BuildSummary(plan, _root);
            canRestore = plan.Error == null;
        }
        catch (Exception ex)
        {
            summary = $"Cannot restore: {ex.Message}";
        }

        if (version != _selectionVersion) return;
        SummaryText.Text = summary;
        RestoreButton.IsEnabled = canRestore;
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (ArchiveList.SelectedItem is not ArchiveListItem { IsValid: true } item) return;
        SelectedArchive = item.Entry;
        DialogResult = true;
    }

    /// <summary>Both extractions (RN-06): what comes back, what is archived first, and the effect on /reversa.</summary>
    private static string BuildSummary(RestorePlan plan, string root)
    {
        var text = new StringBuilder();
        text.AppendLine($"Will be restored ({ArchiveText.Files(plan.FileCount)}):");
        if (plan.Items.Count == 0)
            text.AppendLine("  No folders. Only the extraction progress in .reversa/state.json comes back.");
        else
            text.Append(ArchiveText.ItemLines(plan.Archive?.FullPath, plan.Items));

        text.AppendLine();
        if (plan.Current is { } current)
        {
            text.AppendLine($"Will be archived first, to {current.Destination} ({ArchiveText.Files(current.FileCount)}):");
            if (current.Items.Count == 0)
                text.AppendLine("  " + ArchiveText.NoFoldersToMove);
            else
                text.Append(ArchiveText.ItemLines(root, current.Items));
            if (current.IsUnfinished)
                text.AppendLine().AppendLine(ArchiveText.NotFinished);
        }
        else
        {
            text.AppendLine("There is no current extraction, so nothing is archived first.");
        }

        text.AppendLine();
        text.Append(plan.Archive?.Phase is { Length: > 0 } phase
            ? $"Nothing is deleted. The next /reversa resumes the restored extraction (phase: {phase})."
            : "Nothing is deleted. The next /reversa starts a new extraction from scratch.");
        return text.ToString();
    }
}

/// <summary>A row of the restore list.</summary>
public sealed class ArchiveListItem
{
    public ArchiveListItem(ArchiveEntry entry)
    {
        Entry = entry;
        var when = entry.CreatedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? entry.Name;
        Title = entry.IsValid ? when : $"{when} (invalid)";
        Detail = entry.IsValid
            ? $"Phase: {entry.Phase ?? "none"} · {Plural(entry.FeatureCount, "feature")} · {entry.Name}"
            : $"{entry.InvalidReason ?? "Invalid archive"} · {entry.Name}";
        AccessibleName = $"{Title}. {Detail}";
    }

    public ArchiveEntry Entry { get; }
    public bool IsValid => Entry.IsValid;
    public string Title { get; }
    public string Detail { get; }
    public string AccessibleName { get; }

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}

/// <summary>Texts shared by the archive confirmation and the restore window (English, like the rest of the UI).</summary>
internal static class ArchiveText
{
    public const string NotFinished = "The extraction is not finished.";

    public const string NoFoldersToMove =
        "No folders to move. Only the extraction progress in .reversa/state.json is archived and reset.";

    public static string Files(int count) => count == 1 ? "1 file" : $"{count:N0} files";

    /// <summary>One bullet per item (relative to <paramref name="baseDir"/>); folders end with "/".</summary>
    public static string ItemLines(string? baseDir, IEnumerable<string> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            var isFolder = baseDir != null
                           && Directory.Exists(Path.Combine(baseDir, item.Replace('/', Path.DirectorySeparatorChar)));
            text.AppendLine($"  • {item}{(isFolder ? "/" : "")}");
        }
        return text.ToString();
    }
}
