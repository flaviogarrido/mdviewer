using System.Text.RegularExpressions;
using MDViewer.Services;

namespace MDViewer.Tests;

public class MarkdownRenderServiceTests
{
    private readonly MarkdownRenderService _service = new();

    private static string Nonce(string html) =>
        Regex.Match(html, "'nonce-([^']+)'").Groups[1].Value;

    [Fact]
    public void Renders_markdown_inside_the_template()
    {
        var html = _service.RenderToHtml("# Title\n\nText", isDarkTheme: false);

        Assert.Contains("<h1 id=\"title\">Title</h1>", html);
        Assert.DoesNotContain("{{", html.Replace("{{CONTENT}}", "")); // no placeholder left
    }

    [Fact]
    public void Every_render_gets_a_fresh_nonce_used_by_the_app_scripts()
    {
        var first = _service.RenderToHtml("x", false);
        var second = _service.RenderToHtml("x", false);

        var nonce = Nonce(first);
        Assert.False(string.IsNullOrEmpty(nonce));
        Assert.NotEqual(nonce, Nonce(second));
        Assert.Equal(3, Regex.Matches(first, $"<script nonce=\"{Regex.Escape(nonce)}\"").Count);
    }

    [Fact]
    public void Document_scripts_do_not_receive_the_nonce()
    {
        var html = _service.RenderToHtml("<script>alert(1)</script>", false);

        Assert.Contains("<script>alert(1)</script>", html); // present but blocked by the CSP
        Assert.Contains("Content-Security-Policy", html);
    }

    [Theory]
    [InlineData("{{NONCE}}")]
    [InlineData("{{BASE_HREF}}")]
    [InlineData("{{MERMAID_THEME}}")]
    [InlineData("{{FLOW_FOOTER}}")]
    public void Placeholder_text_in_a_document_stays_literal(string placeholder)
    {
        var html = _service.RenderToHtml($"before {placeholder} after", false, "https://mddoc.local/", ("p", "n"));

        Assert.Contains($"before {placeholder} after", html);
    }

    [Fact]
    public void Dark_theme_marks_the_html_element_only()
    {
        var html = _service.RenderToHtml("<html lang=\"en\">", isDarkTheme: true);

        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\" data-theme=\"dark\">", html.ReplaceLineEndings("\n"));
        Assert.Single(Regex.Matches(html, "data-theme=\"dark\">"));
        Assert.Contains("theme: 'dark'", html);
    }

    [Fact]
    public void Flow_footer_labels_are_html_encoded()
    {
        var html = _service.RenderToHtml("x", false, flowFooter: ("<b>prev</b>", "a&b"));

        Assert.Contains("← Previous: &lt;b&gt;prev&lt;/b&gt;", html);
        Assert.Contains("Next: a&amp;b →", html);
        Assert.Contains("data-flow=\"prev\"", html);
    }

    [Fact]
    public void Footer_is_omitted_without_neighbors()
    {
        Assert.DoesNotContain("<nav class=\"flow-footer\"", _service.RenderToHtml("x", false));
        Assert.DoesNotContain("<nav class=\"flow-footer\"", _service.RenderToHtml("x", false, flowFooter: (null, null)));

        var onlyNext = _service.RenderToHtml("x", false, flowFooter: (null, "n"));
        Assert.DoesNotContain("data-flow=\"prev\"", onlyNext);
        Assert.Contains("data-flow=\"next\"", onlyNext);
    }

    [Fact]
    public void Base_href_is_encoded()
    {
        var html = _service.RenderToHtml("x", false, "https://mddoc.local/\"x");

        Assert.Contains("<base href=\"https://mddoc.local/&quot;x\">", html);
    }

    // Feature 016: the same template serves the viewer and the browser page.

    [Fact]
    public void Viewer_page_keeps_its_virtual_hosts_and_title()
    {
        var html = _service.RenderToHtml("x", false, "https://mddoc.local/");

        Assert.Contains("base-uri https://mddoc.local;", html);
        Assert.Contains("src=\"https://assets.local/mermaid.min.js\"", html);
        Assert.Contains("<title>MDViewer</title>", html);
    }

    [Theory]
    [InlineData("{{BASE_URI}}")]
    [InlineData("{{MERMAID_SRC}}")]
    [InlineData("{{TITLE}}")]
    public void New_placeholder_text_in_a_document_stays_literal(string placeholder)
    {
        var viewer = _service.RenderToHtml($"before {placeholder} after", false);
        var browser = _service.RenderForBrowser($"before {placeholder} after", false, @"C:\docs\a.md", @"C:\t\mermaid.min.js");

        Assert.Contains($"before {placeholder} after", viewer);
        Assert.Contains($"before {placeholder} after", browser);
    }

    [Fact]
    public void Browser_page_points_to_local_files()
    {
        var html = _service.RenderForBrowser("![x](img/a.png)", false,
            @"C:\My docs\notes\read me.md", @"C:\Temp\MDViewer_Assets\mermaid.min.js");

        Assert.Contains("<base href=\"file:///C:/My%20docs/notes/\">", html);
        Assert.Contains("base-uri file:;", html);
        Assert.Contains("src=\"file:///C:/Temp/MDViewer_Assets/mermaid.min.js\"", html);
        Assert.Contains("<title>read me.md</title>", html);
        Assert.DoesNotContain("mddoc.local", html);
        Assert.DoesNotContain("assets.local", html);
    }

    [Fact]
    public void Browser_page_keeps_the_script_policy()
    {
        var html = _service.RenderForBrowser("<script>alert(1)</script>", false, @"C:\d\a.md", @"C:\t\m.js");

        var nonce = Nonce(html);
        Assert.False(string.IsNullOrEmpty(nonce));
        Assert.Equal(3, Regex.Matches(html, $"<script nonce=\"{Regex.Escape(nonce)}\"").Count);
        Assert.Contains("<script>alert(1)</script>", html); // present but blocked by the CSP
        Assert.DoesNotContain("<nav class=\"flow-footer\"", html);
    }

    [Fact]
    public void Browser_page_title_cannot_become_a_placeholder()
    {
        var html = _service.RenderForBrowser("body", false, @"C:\d\{{CONTENT}}.md", @"C:\t\m.js");

        Assert.Contains("<title>&#123;&#123;CONTENT}}.md</title>", html);
        Assert.Single(Regex.Matches(html, "<p>body</p>"));
    }

    [Fact]
    public void Template_scripts_work_without_the_viewer_bridge()
    {
        var html = _service.RenderToHtml("x", false);

        Assert.Contains("var host = window.chrome && window.chrome.webview;", html);
        Assert.Contains("if (!host) return;", html);
        Assert.Contains("if (host) host.postMessage('render-complete');", html);
        Assert.DoesNotContain("window.chrome.webview.postMessage", html);
    }
}
