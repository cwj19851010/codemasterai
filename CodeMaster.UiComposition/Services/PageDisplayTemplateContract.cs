using System.Text.RegularExpressions;
using System.Text.Json;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Validation and marker handling for stored display fragments.  The public
/// marker engine remains unchanged; complex-page markers are intentionally
/// scoped to this private module.
/// </summary>
public static class PageDisplayTemplateContract
{
    private static readonly Regex TagPattern = new(
        @"<(?<close>/)?(?<tag>[A-Za-z][A-Za-z0-9:-]*)(?<attrs>[^<>]*?)(?<self>/)?>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ContainerItemsMarker = new(
        @"(?:\[\s*items\s*\]|\{\{\s*items\s*\}\})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ContainerItemMarker = new(
        @"(?:\[\s*item\s*\]|\{\{\s*item\s*\}\})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FieldMarker = new(
        @"\brow\.(?:field|[A-Za-z_$][A-Za-z0-9_$]*)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"
    };

    public static void Validate(string content, PageDisplayTemplateScope scope)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("显示模板内容不能为空。", nameof(content));
        if (Regex.IsMatch(content, @"<\s*(script|style|template)\b", RegexOptions.IgnoreCase))
            throw new ArgumentException("显示模板只能包含 Vue 模板片段，不能包含 script、style 或 template 块。", nameof(content));
        if (scope == PageDisplayTemplateScope.Container &&
            Regex.IsMatch(content, @"\[\s*(row\.field|field)\s*\]", RegexOptions.IgnoreCase))
            throw new ArgumentException("容器级显示模板不能使用字段占位符。", nameof(content));

        var stack = new Stack<string>();
        var matches = TagPattern.Matches(content);
        if (matches.Count == 0 && string.IsNullOrWhiteSpace(StripMarkers(content)))
            throw new ArgumentException("显示模板必须包含可解析的 Vue 片段。", nameof(content));

        foreach (Match match in matches)
        {
            var tag = match.Groups["tag"].Value;
            var closing = match.Groups["close"].Success;
            var selfClosing = match.Groups["self"].Success || VoidTags.Contains(tag);
            if (closing)
            {
                if (stack.Count == 0 || !string.Equals(stack.Pop(), tag, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"显示模板标签未正确闭合：{tag}。", nameof(content));
            }
            else if (!selfClosing)
            {
                stack.Push(tag);
            }
        }
        if (stack.Count > 0)
            throw new ArgumentException($"显示模板存在未闭合标签：{stack.Peek()}。", nameof(content));
    }

    public static string ReplaceContainer(string content, IReadOnlyList<string> itemFragments)
    {
        var allItems = string.Join(Environment.NewLine, itemFragments);
        var replaced = ContainerItemsMarker.Replace(content, allItems);
        var index = 0;
        replaced = ContainerItemMarker.Replace(replaced, _ =>
            index < itemFragments.Count ? itemFragments[index++] : string.Empty);
        if (!ContainerItemsMarker.IsMatch(content) && index < itemFragments.Count)
            replaced += Environment.NewLine + string.Join(Environment.NewLine, itemFragments.Skip(index));
        return replaced;
    }

    public static string ReplaceField(string content, string expression, string fieldName)
    {
        var replaced = content.Replace("row.field", expression, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            var specific = new Regex($"\\brow\\.{Regex.Escape(fieldName)}\\b", RegexOptions.Compiled);
            replaced = specific.Replace(replaced, expression);
        }
        return replaced;
    }

    public static long? TryGetTemplateId(string? propsJson)
    {
        if (string.IsNullOrWhiteSpace(propsJson) || propsJson == "{}") return null;
        try
        {
            using var document = JsonDocument.Parse(propsJson);
            if (!document.RootElement.TryGetProperty("displayTemplateId", out var value) &&
                !document.RootElement.TryGetProperty("templateId", out value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        }
        catch (JsonException)
        {
            // The normal binding validation reports malformed props JSON.  A
            // generator reading old data should simply ignore an absent ID.
        }
        return null;
    }

    private static string StripMarkers(string content)
        => ContainerItemsMarker.Replace(ContainerItemMarker.Replace(content, string.Empty), string.Empty)
            .Replace("row.field", string.Empty, StringComparison.Ordinal);
}
