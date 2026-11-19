using System.IO;
using Markdig;

namespace MDViewer.Services;

public class MarkdownRenderService
{
    private readonly MarkdownPipeline _pipeline;
    private readonly string _htmlTemplate;

    public MarkdownRenderService()
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        _htmlTemplate = LoadEmbeddedResource("MDViewer.Assets.template.html");
    }

    /// <param name="flowFooter">
    /// Labels of the previous/next documents of the Reversa Flow (null: no
    /// footer; a null label: no link on that side).
    /// </param>
    public string RenderToHtml(string markdown, bool isDarkTheme, string? baseHref = null,
        (string? PrevLabel, string? NextLabel)? flowFooter = null)
    {
        var htmlContent = Markdown.ToHtml(markdown, _pipeline);
        return ApplyTemplate(htmlContent, isDarkTheme, baseHref, flowFooter, ViewerPage);
    }

    /// <summary>
    /// A page built by the app itself (the Reversa map), already HTML: only
    /// the template is applied. Never pass document content here.
    /// </summary>
    public string RenderHtmlPage(string htmlContent, bool isDarkTheme) =>
        ApplyTemplate(htmlContent, isDarkTheme, baseHref: null, flowFooter: null, ViewerPage);

    /// <summary>
    /// The document as a standalone page for the default browser (feature
    /// 016): same render and script policy as the viewer, with file: URIs for
    /// the document folder and for <paramref name="mermaidPath"/>, the document
    /// name as title and no Flow footer.
    /// </summary>
    public string RenderForBrowser(string markdown, bool isDarkTheme, string documentPath, string mermaidPath)
    {
        var htmlContent = Markdown.ToHtml(markdown, _pipeline);
        var page = new PageSettings(
            BaseUri: "file:",
            MermaidSrc: BrowserExport.FileUri(mermaidPath),
            Title: System.IO.Path.GetFileName(documentPath));
        return ApplyTemplate(htmlContent, isDarkTheme, BrowserExport.FolderUri(documentPath), flowFooter: null, page);
    }

    /// <summary>Template values that differ between the viewer and the browser page.</summary>
    private sealed record PageSettings(string BaseUri, string MermaidSrc, string Title);

    private static readonly PageSettings ViewerPage = new(
        BaseUri: "https://mddoc.local",
        MermaidSrc: "https://assets.local/mermaid.min.js",
        Title: "MDViewer");

    private string ApplyTemplate(string htmlContent, bool isDarkTheme, string? baseHref,
        (string? PrevLabel, string? NextLabel)? flowFooter, PageSettings page)
    {
        var mermaidTheme = isDarkTheme ? "dark" : "default";

        var baseTag = string.IsNullOrEmpty(baseHref)
            ? ""
            : $"<base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\">";

        // Fresh per render, so a document can never guess it and run a script.
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));

        // Every template placeholder is replaced before the content goes in, so
        // a document that contains placeholder text (e.g. "{{NONCE}}") never
        // receives the value.
        var template = isDarkTheme
            ? _htmlTemplate.Replace("<html lang=\"en\">", "<html lang=\"en\" data-theme=\"dark\">")
            : _htmlTemplate;

        return template
            .Replace("{{FLOW_FOOTER}}", BuildFlowFooter(flowFooter))
            .Replace("{{BASE_HREF}}", baseTag)
            .Replace("{{BASE_URI}}", page.BaseUri)
            .Replace("{{MERMAID_SRC}}", System.Net.WebUtility.HtmlEncode(page.MermaidSrc))
            // Braces encoded: a file named "{{CONTENT}}.md" must not become a placeholder.
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(page.Title).Replace("{", "&#123;"))
            .Replace("{{MERMAID_THEME}}", mermaidTheme)
            .Replace("{{NONCE}}", nonce)
            .Replace("{{CONTENT}}", htmlContent);
    }

    /// <summary>
    /// Previous/next links of the Reversa Flow. The links carry no path: the
    /// page's click handler sends {type:'flow', dir} to the host.
    /// </summary>
    private static string BuildFlowFooter((string? PrevLabel, string? NextLabel)? footer)
    {
        if (footer is not { } f || (f.PrevLabel == null && f.NextLabel == null))
            return "";

        var prev = f.PrevLabel == null
            ? ""
            : $"<a href=\"#\" class=\"flow-prev\" data-flow=\"prev\">← Previous: {System.Net.WebUtility.HtmlEncode(f.PrevLabel)}</a>";
        var next = f.NextLabel == null
            ? ""
            : $"<a href=\"#\" class=\"flow-next\" data-flow=\"next\">Next: {System.Net.WebUtility.HtmlEncode(f.NextLabel)} →</a>";
        return $"<nav class=\"flow-footer\" aria-label=\"Reversa flow\">{prev}{next}</nav>";
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(MarkdownRenderService).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
