using System.IO;
using MDViewer.Services;

namespace MDViewer.Tests;

public class BrowserExportTests
{
    private const string Export = @"C:\Temp\MDViewer_Browser";

    [Fact]
    public void Path_is_stable_for_a_document()
    {
        var first = BrowserExport.PathFor(Export, @"C:\docs\README.md");

        Assert.Equal(first, BrowserExport.PathFor(Export, @"C:\docs\README.md"));
        // Windows paths ignore case: same page (the name keeps the casing given).
        Assert.Equal(first, BrowserExport.PathFor(Export, @"c:\DOCS\readme.md"), ignoreCase: true);
        Assert.Equal(Export, Path.GetDirectoryName(first));
        Assert.Matches(@"^README-[0-9a-f]{8}\.html$", Path.GetFileName(first));
    }

    [Fact]
    public void Same_named_documents_in_other_folders_get_other_pages()
    {
        Assert.NotEqual(
            BrowserExport.PathFor(Export, @"C:\a\README.md"),
            BrowserExport.PathFor(Export, @"C:\b\README.md"));
    }

    [Fact]
    public void Folder_uri_ends_with_a_slash_and_escapes_the_path()
    {
        Assert.Equal("file:///C:/My%20docs/caf%C3%A9/", BrowserExport.FolderUri(@"C:\My docs\café\a.md"));
    }

    [Fact]
    public void Unc_paths_become_host_uris()
    {
        Assert.Equal("file://server/share/docs/", BrowserExport.FolderUri(@"\\server\share\docs\a.md"));
    }
}
