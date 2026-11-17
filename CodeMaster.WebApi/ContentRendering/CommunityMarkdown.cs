using System.Text.RegularExpressions;
using Markdig;

namespace CodeMaster.WebApi.ContentRendering;

/// <summary>
/// Server-side Markdown rendering for community content.
/// Raw HTML is disabled and link/image schemes are restricted to avoid stored XSS.
/// </summary>
public static class CommunityMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseTaskLists()
        .DisableHtml()
        .Build();

    private static readonly Regex UnsafeLinkPattern = new(
        "(href)=\"(?!(?:https?:|mailto:|/|#))[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnsafeImagePattern = new(
        "(src)=\"(?!(?:https?:|/))[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var html = Markdown.ToHtml(markdown, Pipeline);
        html = UnsafeLinkPattern.Replace(html, "$1=\"#\"");
        html = UnsafeImagePattern.Replace(html, "$1=\"\"");
        html = html.Replace("<a href=\"http", "<a target=\"_blank\" rel=\"noopener noreferrer\" href=\"http");
        return html;
    }
}
