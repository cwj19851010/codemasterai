using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// The semantic layout tree used by complex pages.  It deliberately stores layout
/// meaning (page/row/column/table/form) instead of generated Vue markup so that the
/// designer, generator, and future Agent can share one contract.
/// </summary>
public sealed class PageLayoutTree
{
    private static readonly Regex StableIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public int Version { get; set; } = 1;
    public PageLayoutNode Root { get; set; } = PageLayoutNode.Create("page", "page_root", PageNodeOrigin.User);

    public static PageLayoutTree Empty() => new();

    public static PageLayoutTree Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty();

        var tree = JsonSerializer.Deserialize<PageLayoutTree>(json, JsonOptions)
            ?? throw new ArgumentException("Layout JSON cannot be null.", nameof(json));
        tree.Root ??= PageLayoutNode.Create("page", "page_root", PageNodeOrigin.User);
        tree.AssignMissingIds();
        tree.Validate();
        return tree;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public PageLayoutNode AddNode(string parentId, string type, string? componentId = null, int? index = null)
    {
        var parent = Require(parentId);
        EnsureContainer(parent);
        var requestedId = componentId ?? NextComponentId(type);
        if (Find(requestedId) != null)
            throw new InvalidOperationException($"Duplicate layout ComponentId '{requestedId}'.");
        var node = PageLayoutNode.Create(type, requestedId, PageNodeOrigin.User);
        InsertChild(parent, node, index);
        Validate();
        return node;
    }

    public PageLayoutNode AddRow(string parentId = "page_root", int? index = null)
        => AddNode(parentId, "row", null, index);

    public PageLayoutNode AddColumn(string rowId, int span = 24, int? index = null)
    {
        var row = Require(rowId);
        RequireType(row, "row");
        var column = AddNode(rowId, "column", null, index);
        column.Span = span;
        Validate();
        return column;
    }

    public PageLayoutNode AddComponent(string parentId, string type, string? componentId = null, int? index = null)
    {
        var parent = Require(parentId);
        EnsureContainer(parent);
        var requestedId = componentId ?? NextComponentId(type);
        if (Find(requestedId) != null)
            throw new InvalidOperationException($"Duplicate layout ComponentId '{requestedId}'.");
        var node = PageLayoutNode.Create(type, requestedId, PageNodeOrigin.User);
        node.Origin = PageNodeOrigin.User;
        InsertChild(parent, node, index);
        Validate();
        return node;
    }

    public bool RemoveNode(string nodeId)
    {
        if (string.Equals(nodeId, Root.ComponentId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The page root cannot be removed.");
        var location = FindLocation(nodeId);
        if (location == null) return false;
        location.Value.Parent.Children.RemoveAt(location.Value.Index);
        Validate();
        return true;
    }

    public void SplitColumn(string columnId, int firstSpan)
    {
        var location = FindLocation(columnId) ?? throw new KeyNotFoundException(columnId);
        RequireType(location.Node, "column");
        var oldSpan = location.Node.Span ?? 24;
        if (firstSpan <= 0 || firstSpan >= oldSpan)
            throw new ArgumentOutOfRangeException(nameof(firstSpan), "The first column span must leave room for the second column.");

        var second = PageLayoutNode.Create("column", NextComponentId("column"), PageNodeOrigin.User);
        second.Span = oldSpan - firstSpan;
        second.Grid = location.Node.Grid?.Clone();
        location.Node.Span = firstSpan;
        location.Parent.Children.Insert(location.Index + 1, second);
        Validate();
    }

    public void SetGrid(string columnId, PageGridSpec grid)
    {
        var node = Require(columnId);
        RequireType(node, "column");
        node.Span = grid.Span;
        node.Grid = grid.Clone();
        Validate();
    }

    public void Move(string nodeId, string newParentId, int? index = null)
    {
        if (string.Equals(nodeId, Root.ComponentId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The page root cannot be moved.");
        var source = FindLocation(nodeId) ?? throw new KeyNotFoundException(nodeId);
        var parent = Require(newParentId);
        EnsureContainer(parent);
        if (IsDescendant(source.Node, parent))
            throw new InvalidOperationException("A node cannot be moved below one of its descendants.");

        source.Parent.Children.RemoveAt(source.Index);
        var targetIndex = index;
        if (ReferenceEquals(source.Parent, parent) && targetIndex.HasValue && targetIndex.Value > source.Index)
            targetIndex--;
        try
        {
            InsertChild(parent, source.Node, targetIndex);
            Validate();
        }
        catch
        {
            parent.Children.Remove(source.Node);
            source.Parent.Children.Insert(Math.Min(source.Index, source.Parent.Children.Count), source.Node);
            throw;
        }
    }

    public void Wrap(string nodeId, string wrapperType, string? wrapperId = null)
    {
        var source = FindLocation(nodeId) ?? throw new KeyNotFoundException(nodeId);
        var wrapper = PageLayoutNode.Create(wrapperType, wrapperId ?? NextComponentId(wrapperType), PageNodeOrigin.User);
        wrapper.Children.Add(source.Node);
        source.Parent.Children[source.Index] = wrapper;
        Validate();
    }

    public void BindDataSource(string componentId, long dataSourceId, string bindingId)
    {
        if (dataSourceId <= 0) throw new ArgumentOutOfRangeException(nameof(dataSourceId));
        if (string.IsNullOrWhiteSpace(bindingId)) throw new ArgumentException("BindingId is required.", nameof(bindingId));
        var node = Require(componentId);
        node.DataSourceId = dataSourceId;
        node.BindingId = bindingId.Trim();
        Validate();
    }

    public void BindField(string componentId, long fieldId, string bindingId)
    {
        if (fieldId <= 0) throw new ArgumentOutOfRangeException(nameof(fieldId));
        if (string.IsNullOrWhiteSpace(bindingId)) throw new ArgumentException("BindingId is required.", nameof(bindingId));
        var node = Require(componentId);
        node.FieldId = fieldId;
        node.BindingId = bindingId.Trim();
        Validate();
    }

    public PageLayoutIdentityMap RegenerateStableIds(string scope)
    {
        Validate();
        var normalizedScope = new string((scope ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedScope)) normalizedScope = "page";

        var result = new PageLayoutIdentityMap();
        var nodes = Enumerate(Root).ToList();
        var componentIndex = 0;
        var bindingIndex = 0;
        foreach (var node in nodes)
        {
            var oldComponentId = node.ComponentId;
            var newComponentId = ReferenceEquals(node, Root)
                ? "page_root"
                : $"{normalizedScope}_{NormalizeIdPart(node.Type)}_{++componentIndex}";
            result.ComponentIds[oldComponentId] = newComponentId;

            var oldBindingId = node.BindingId;
            string? newBindingId = null;
            if (!string.IsNullOrWhiteSpace(oldBindingId))
            {
                newBindingId = $"binding_{normalizedScope}_{++bindingIndex}";
                result.BindingIds[oldBindingId] = newBindingId;
            }

            node.ComponentId = newComponentId;
            node.BindingId = newBindingId;
            if (!string.IsNullOrWhiteSpace(node.GenId))
                node.GenId = newBindingId == null ? $"gen_component_{newComponentId}" : $"gen_binding_{newBindingId}";
            if (node.Props?.ContainsKey("data-gen-id") == true)
                node.Props["data-gen-id"] = newBindingId == null ? $"gen_component_{newComponentId}" : $"gen_binding_{newBindingId}";
        }
        Validate();
        return result;
    }

    public PageLayoutNode Require(string componentId)
        => Find(componentId) ?? throw new KeyNotFoundException($"Layout node '{componentId}' was not found.");

    public PageLayoutNode? Find(string componentId)
    {
        if (string.Equals(Root.ComponentId, componentId, StringComparison.OrdinalIgnoreCase)) return Root;
        return Find(Root, componentId);
    }

    public void Validate()
    {
        if (Root == null) throw new InvalidOperationException("Layout root is required.");
        if (!string.Equals(Root.Type, "page", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Layout root type must be 'page'.");

        ApplyImplicitRowSpans(Root, new HashSet<PageLayoutNode>());

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateNode(Root, ids, bindingIds, genIds, new HashSet<PageLayoutNode>());
    }

    private static void ApplyImplicitRowSpans(PageLayoutNode node, HashSet<PageLayoutNode> visited)
    {
        if (!visited.Add(node))
            return;
        if (node.Type.Equals("row", StringComparison.OrdinalIgnoreCase))
        {
            var columns = (node.Children ?? new List<PageLayoutNode>())
                .Where(child => child.Type.Equals("column", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var implicitColumns = columns
                .Where(child => !child.Span.HasValue && child.Grid?.Span == null)
                .ToList();
            if (implicitColumns.Count > 0)
            {
                var explicitTotal = columns
                    .Where(child => !implicitColumns.Contains(child))
                    .Sum(child => child.Grid?.Span ?? child.Span ?? 0);
                var remaining = Math.Max(implicitColumns.Count, 24 - explicitTotal);
                var defaultSpan = Math.Max(1, remaining / implicitColumns.Count);
                foreach (var column in implicitColumns)
                    column.Span = defaultSpan;
            }
        }

        foreach (var child in node.Children ?? new List<PageLayoutNode>())
            ApplyImplicitRowSpans(child, visited);
    }

    public static PageLayoutTree FromLegacyPreset(string? json) => Parse(json);

    private void AssignMissingIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        AssignMissingIds(Root, ids, counters, isRoot: true);
    }

    private static void AssignMissingIds(PageLayoutNode node, HashSet<string> ids, Dictionary<string, int> counters, bool isRoot)
    {
        if (isRoot && string.IsNullOrWhiteSpace(node.ComponentId))
            node.ComponentId = "page_root";
        if (string.IsNullOrWhiteSpace(node.ComponentId))
        {
            var prefix = new string((node.Type ?? "node").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(prefix)) prefix = "node";
            counters[prefix] = counters.TryGetValue(prefix, out var current) ? current + 1 : 1;
            node.ComponentId = $"{prefix}_{counters[prefix]}";
            while (!ids.Add(node.ComponentId))
            {
                counters[prefix]++;
                node.ComponentId = $"{prefix}_{counters[prefix]}";
            }
        }
        else
        {
            ids.Add(node.ComponentId);
        }
        foreach (var child in node.Children ?? new List<PageLayoutNode>())
            AssignMissingIds(child, ids, counters, isRoot: false);
    }

    private void ValidateNode(
        PageLayoutNode node,
        HashSet<string> ids,
        HashSet<string> bindingIds,
        HashSet<string> genIds,
        HashSet<PageLayoutNode> ancestors)
    {
        if (!ancestors.Add(node))
            throw new InvalidOperationException($"Layout cycle detected at '{node.ComponentId}'.");
        if (string.IsNullOrWhiteSpace(node.Type))
            throw new InvalidOperationException($"Layout node '{node.ComponentId}' requires a type.");
        if (string.IsNullOrWhiteSpace(node.ComponentId))
            throw new InvalidOperationException("Every layout node requires a ComponentId.");
        if (!StableIdPattern.IsMatch(node.ComponentId))
            throw new InvalidOperationException($"Invalid layout ComponentId '{node.ComponentId}'.");
        if (!ids.Add(node.ComponentId))
            throw new InvalidOperationException($"Duplicate layout ComponentId '{node.ComponentId}'.");
        if (!string.IsNullOrWhiteSpace(node.BindingId))
        {
            if (!StableIdPattern.IsMatch(node.BindingId))
                throw new InvalidOperationException($"Invalid BindingId '{node.BindingId}'.");
            if (!bindingIds.Add(node.BindingId))
                throw new InvalidOperationException($"Duplicate layout BindingId '{node.BindingId}'.");
        }
        if (!string.IsNullOrWhiteSpace(node.GenId))
        {
            if (!StableIdPattern.IsMatch(node.GenId))
                throw new InvalidOperationException($"Invalid generation anchor '{node.GenId}'.");
            if (!genIds.Add(node.GenId))
                throw new InvalidOperationException($"Duplicate generation anchor '{node.GenId}'.");
        }
        if (node.Type.Equals("column", StringComparison.OrdinalIgnoreCase))
        {
            var span = node.Span ?? node.Grid?.Span ?? 24;
            ValidateSpan(span, node.ComponentId);
            if (node.Grid != null)
            {
                ValidateSpan(node.Grid.Span, node.ComponentId);
                ValidateResponsiveSpan(node.Grid.Xs, node.ComponentId, "xs");
                ValidateResponsiveSpan(node.Grid.Sm, node.ComponentId, "sm");
                ValidateResponsiveSpan(node.Grid.Md, node.ComponentId, "md");
                ValidateResponsiveSpan(node.Grid.Lg, node.ComponentId, "lg");
            }
        }
        if (node.Type.Equals("row", StringComparison.OrdinalIgnoreCase))
        {
            var columnTotal = (node.Children ?? new List<PageLayoutNode>())
                .Where(child => child.Type.Equals("column", StringComparison.OrdinalIgnoreCase))
                .Sum(child => child.Grid?.Span ?? child.Span ?? 24);
            if (columnTotal > 24)
                throw new InvalidOperationException($"Row '{node.ComponentId}' exceeds the Element Plus 24-column grid.");
        }
        if (node.DataSourceId.HasValue && node.DataSourceId.Value <= 0)
            throw new InvalidOperationException($"Invalid DataSourceId on '{node.ComponentId}'.");
        if (node.FieldId.HasValue && node.FieldId.Value <= 0)
            throw new InvalidOperationException($"Invalid FieldId on '{node.ComponentId}'.");
        if (node.FieldId.HasValue && !node.DataSourceId.HasValue)
            throw new InvalidOperationException($"Field binding on '{node.ComponentId}' requires a DataSourceId.");
        if ((node.DataSourceId.HasValue || node.FieldId.HasValue) && string.IsNullOrWhiteSpace(node.BindingId))
            throw new InvalidOperationException($"Bound layout node '{node.ComponentId}' requires a BindingId.");

        foreach (var child in node.Children ?? new List<PageLayoutNode>())
        {
            if (child == null) throw new InvalidOperationException($"Layout node '{node.ComponentId}' contains a null child.");
            ValidateNode(child, ids, bindingIds, genIds, ancestors);
        }
        ancestors.Remove(node);
    }

    private static void ValidateSpan(int span, string componentId)
    {
        if (span is < 1 or > 24)
            throw new InvalidOperationException($"Column '{componentId}' span must be between 1 and 24.");
    }

    private static void ValidateResponsiveSpan(int? span, string componentId, string breakpoint)
    {
        if (span.HasValue && span.Value is < 1 or > 24)
            throw new InvalidOperationException($"Column '{componentId}' {breakpoint} span must be between 1 and 24.");
    }

    private static void EnsureContainer(PageLayoutNode node)
    {
        if (node.Type.Equals("pagination", StringComparison.OrdinalIgnoreCase) ||
            node.Type.Equals("table-column", StringComparison.OrdinalIgnoreCase) ||
            node.Type.Equals("form-field", StringComparison.OrdinalIgnoreCase) ||
            node.Type.Equals("detail-field", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Node '{node.ComponentId}' cannot contain layout children.");
    }

    private static void RequireType(PageLayoutNode node, string type)
    {
        if (!node.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Node '{node.ComponentId}' must be a {type} node.");
    }

    private void InsertChild(PageLayoutNode parent, PageLayoutNode node, int? index)
    {
        parent.Children ??= new List<PageLayoutNode>();
        var actualIndex = index ?? parent.Children.Count;
        if (actualIndex < 0 || actualIndex > parent.Children.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        parent.Children.Insert(actualIndex, node);
    }

    private string NextComponentId(string type)
    {
        var prefix = new string(type.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "node";
        var index = 1;
        while (Find($"{prefix}_{index}") != null) index++;
        return $"{prefix}_{index}";
    }

    private static PageLayoutNode? Find(PageLayoutNode node, string componentId)
    {
        foreach (var child in node.Children ?? new List<PageLayoutNode>())
        {
            if (string.Equals(child.ComponentId, componentId, StringComparison.OrdinalIgnoreCase)) return child;
            var found = Find(child, componentId);
            if (found != null) return found;
        }
        return null;
    }

    private (PageLayoutNode Parent, PageLayoutNode Node, int Index)? FindLocation(string componentId)
        => FindLocation(Root, componentId);

    private static (PageLayoutNode Parent, PageLayoutNode Node, int Index)? FindLocation(PageLayoutNode parent, string componentId)
    {
        for (var i = 0; i < (parent.Children?.Count ?? 0); i++)
        {
            var child = parent.Children![i];
            if (string.Equals(child.ComponentId, componentId, StringComparison.OrdinalIgnoreCase))
                return (parent, child, i);
            var found = FindLocation(child, componentId);
            if (found.HasValue) return found;
        }
        return null;
    }

    private static bool IsDescendant(PageLayoutNode node, PageLayoutNode candidateParent)
    {
        if (ReferenceEquals(node, candidateParent)) return true;
        foreach (var child in node.Children ?? new List<PageLayoutNode>())
            if (IsDescendant(child, candidateParent)) return true;
        return false;
    }

    private static IEnumerable<PageLayoutNode> Enumerate(PageLayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children ?? new List<PageLayoutNode>())
            foreach (var descendant in Enumerate(child))
                yield return descendant;
    }

    private static string NormalizeIdPart(string value)
    {
        var normalized = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "node" : normalized;
    }
}

public sealed class PageLayoutIdentityMap
{
    public Dictionary<string, string> ComponentIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> BindingIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PageLayoutNode
{
    public string Type { get; set; } = "container";
    public string ComponentId { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string? GenId { get; set; }
    public int? Span { get; set; }
    public PageGridSpec? Grid { get; set; }
    public long? DataSourceId { get; set; }
    public string? BindingId { get; set; }
    public long? FieldId { get; set; }
    public PageNodeOrigin Origin { get; set; } = PageNodeOrigin.Generated;
    public Dictionary<string, string?> Props { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PageLayoutNode> Children { get; set; } = new();

    public static PageLayoutNode Create(string type, string componentId, PageNodeOrigin origin)
        => new() { Type = type.Trim(), ComponentId = componentId.Trim(), Origin = origin };
}

public sealed class PageGridSpec
{
    public int Span { get; set; } = 24;
    public int? Xs { get; set; }
    public int? Sm { get; set; }
    public int? Md { get; set; }
    public int? Lg { get; set; }

    public PageGridSpec Clone() => new() { Span = Span, Xs = Xs, Sm = Sm, Md = Md, Lg = Lg };
}
