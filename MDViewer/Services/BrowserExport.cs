using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MDViewer.Services;

/// <summary>
/// Where the page for "Open in Browser" (feature 016) goes and how it points
/// back to the document. The page lives in the user's temp folder, never in
/// the document folder: one file per document, overwritten on each use.
/// </summary>
public static class BrowserExport
{
    public const string FolderName = "MDViewer_Browser";

    /// <summary>
    /// "&lt;file name&gt;-&lt;8 hex of the full path&gt;.html": stable for a
    /// document, different for same-named documents in other folders.
    /// </summary>
    public static string PathFor(string exportFolder, string documentPath)
    {
        var fullPath = Path.GetFullPath(documentPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant()));
        var name = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(exportFolder, $"{name}-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}.html");
    }

    /// <summary>file: URI of the folder holding the document, ending in "/".</summary>
    public static string FolderUri(string documentPath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(documentPath))
            ?? throw new ArgumentException("The document has no folder.", nameof(documentPath));
        return FileUri(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar);
    }

    /// <summary>file: URI of a local path, with spaces and accents escaped.</summary>
    public static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}
