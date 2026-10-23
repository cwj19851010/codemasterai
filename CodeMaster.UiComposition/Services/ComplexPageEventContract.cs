using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;

namespace CodeMaster.Application.Services.CodeGen;

internal static class ComplexPageEventContract
{
    public const string PropertyName = "_events";
    private static readonly Regex EventNamePattern = new("^[a-z][a-z0-9:-]*(?:\\.[a-z]+)?$", RegexOptions.Compiled);
    private static readonly Regex KeyPattern = new("^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "refreshDataSource", "navigate", "showMessage", "setPageParam", "setQueryParam",
        "resetQuery", "openDialog", "closeDialog", "toggleComponent", "stop"
    };
    private static readonly HashSet<string> MessageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "success", "warning", "error", "info"
    };

    public static IReadOnlyList<ComplexPageEventDefinitionDto> Read(PageLayoutNode node)
    {
        if (!node.Props.TryGetValue(PropertyName, out var json) || string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<ComplexPageEventDefinitionDto>>(json, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Component '{node.ComponentId}' has invalid event JSON.", exception);
        }
    }

    public static void Validate(PageLayoutTree layout, IReadOnlyCollection<string> sourceKeys)
    {
        var nodeIds = Enumerate(layout.Root).Select(item => item.ComponentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = sourceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Enumerate(layout.Root))
        {
            var events = Read(node);
            if (events.GroupBy(item => item.EventName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new ArgumentException($"Component '{node.ComponentId}' has duplicate event definitions.");
            foreach (var definition in events)
            {
                if (!EventNamePattern.IsMatch(definition.EventName ?? string.Empty))
                    throw new ArgumentException($"Component '{node.ComponentId}' has an unsafe event name.");
                if (definition.Actions.Count == 0)
                    throw new ArgumentException($"Event '{definition.EventName}' on '{node.ComponentId}' requires at least one action.");
                foreach (var action in definition.Actions)
                    ValidateAction(node.ComponentId, action, sources, nodeIds);
            }
        }
    }

    public static HashSet<string> GetVisibilityTargets(PageLayoutNode root)
        => Enumerate(root)
            .SelectMany(Read)
            .SelectMany(item => item.Actions)
            .Where(item => item.Type is "openDialog" or "closeDialog" or "toggleComponent")
            .Select(item => item.TargetComponentId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void ValidateAction(
        string componentId,
        ComplexPageActionDefinitionDto action,
        IReadOnlySet<string> sourceKeys,
        IReadOnlySet<string> nodeIds)
    {
        if (!ActionTypes.Contains(action.Type ?? string.Empty))
            throw new ArgumentException($"Component '{componentId}' uses unsupported action '{action.Type}'.");
        if (string.Equals(action.Type, "refreshDataSource", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(action.SourceKey) || !sourceKeys.Contains(action.SourceKey)))
            throw new ArgumentException($"Component '{componentId}' refreshes an unknown data source.");
        if (string.Equals(action.Type, "navigate", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(action.Path))
            throw new ArgumentException($"Component '{componentId}' navigate action requires a path.");
        if (string.Equals(action.Type, "showMessage", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(action.Message))
                throw new ArgumentException($"Component '{componentId}' message action requires text.");
            if (!string.IsNullOrWhiteSpace(action.MessageType) && !MessageTypes.Contains(action.MessageType))
                throw new ArgumentException($"Component '{componentId}' uses an unsupported message type.");
        }
        if (action.Type is "setPageParam" or "setQueryParam" &&
            (string.IsNullOrWhiteSpace(action.Key) || !KeyPattern.IsMatch(action.Key)))
            throw new ArgumentException($"Component '{componentId}' parameter action requires a safe key.");
        if (action.Type is "openDialog" or "closeDialog" or "toggleComponent" &&
            (string.IsNullOrWhiteSpace(action.TargetComponentId) || !nodeIds.Contains(action.TargetComponentId)))
            throw new ArgumentException($"Component '{componentId}' targets an unknown component.");
    }

    private static IEnumerable<PageLayoutNode> Enumerate(PageLayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Enumerate(child))
                yield return descendant;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
