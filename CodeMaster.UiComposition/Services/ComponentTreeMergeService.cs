using CodeMaster.Infrastructure.VueParser.Model;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeMaster.Application.Services.CodeGen;

public sealed class ComponentTreeMergeService : IComponentTreeMergeService
{
    private static readonly JsonSerializerOptions TreeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string MergeOrder(string newTreeJson, string oldTreeJson)
    {
        if (string.IsNullOrWhiteSpace(oldTreeJson)) return newTreeJson;

        var oldNode = JsonNode.Parse(oldTreeJson);
        var newNode = JsonNode.Parse(newTreeJson);
        if (oldNode is not JsonArray oldArray || newNode is not JsonArray newArray)
            return newTreeJson;

        var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scriptMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectTreeScriptSections(oldArray, scriptMap);
        for (var index = 0; index < oldArray.Count; index++)
        {
            var genId = oldArray[index]?["genId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(genId) && !orderMap.ContainsKey(genId))
                orderMap[genId] = index;
        }

        if (scriptMap.Count > 0)
            ApplyTreeScriptSections(newArray, scriptMap);
        if (orderMap.Count > 0)
            ReorderByOldMap(newArray, orderMap);

        return newNode.ToJsonString(TreeJsonOptions);
    }

    public string MergeIncremental(string newTreeJson, string oldTreeJson, ComponentTreeChangeSet changes)
    {
        if (string.IsNullOrWhiteSpace(oldTreeJson)) return newTreeJson;

        var newTree = DeserializeTree(newTreeJson);
        var oldTree = DeserializeTree(oldTreeJson);
        if (newTree == null || oldTree == null) return newTreeJson;

        return JsonSerializer.Serialize(MergeComponentList(newTree, oldTree, changes), TreeJsonOptions);
    }

    private static List<Component>? MergeComponentList(
        List<Component>? newNodes,
        List<Component>? oldNodes,
        ComponentTreeChangeSet changes,
        bool preferNewShell = false)
    {
        if (newNodes == null || newNodes.Count == 0)
            return preferNewShell ? null : PreserveCustomOldNodes(oldNodes);
        if (oldNodes == null || oldNodes.Count == 0)
            return CloneComponents(newNodes);

        var merged = new List<Component>();
        var usedOldIndexes = new HashSet<int>();
        for (var index = 0; index < newNodes.Count; index++)
        {
            var newNode = newNodes[index];
            var genId = OwnGenId(newNode);
            var oldIndex = !string.IsNullOrWhiteSpace(genId)
                ? FindOldIndexByGenId(oldNodes, usedOldIndexes, genId)
                : FindOldSemanticIndex(oldNodes, usedOldIndexes, newNode);
            if (oldIndex < 0 && string.IsNullOrWhiteSpace(genId))
                oldIndex = FindOldStructuralIndex(oldNodes, usedOldIndexes, newNode, index);

            if (oldIndex < 0)
            {
                merged.Add(Clone(newNode));
                continue;
            }

            usedOldIndexes.Add(oldIndex);
            var oldNode = oldNodes[oldIndex];
            var generatedNodeChanged = !string.IsNullOrWhiteSpace(genId) &&
                                       IsKnownGeneratedGenId(genId) &&
                                       changes.IsChangedGeneratedId(genId);
            if (!preferNewShell &&
                !string.IsNullOrWhiteSpace(genId) &&
                IsKnownGeneratedGenId(genId) &&
                !IsGeneratedContainerGenId(genId) &&
                !generatedNodeChanged)
            {
                merged.Add(Clone(oldNode));
                continue;
            }

            var preferOldShell = !preferNewShell &&
                                 string.IsNullOrWhiteSpace(genId) &&
                                 !oldNode.IsDeleted &&
                                 string.Equals(newNode.Tag, oldNode.Tag, StringComparison.OrdinalIgnoreCase);
            var mergedNode = preferOldShell ? Clone(oldNode) : Clone(newNode);
            if (!preferOldShell)
                PreserveScriptSection(mergedNode, oldNode);

            var preferNewDescendants = preferNewShell || generatedNodeChanged;
            mergedNode.Children = MergeComponentList(newNode.Children, oldNode.Children, changes, preferNewDescendants);
            mergedNode.UseSlots = MergeSlots(newNode.UseSlots, oldNode.UseSlots, changes, preferNewDescendants);
            merged.Add(mergedNode);
        }

        for (var index = 0; index < oldNodes.Count; index++)
        {
            if (usedOldIndexes.Contains(index)) continue;
            var oldNode = oldNodes[index];
            var semanticIdentity = SemanticIdentity(oldNode);
            if (!string.IsNullOrWhiteSpace(semanticIdentity) &&
                newNodes.Any(node => string.Equals(SemanticIdentity(node), semanticIdentity, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (ShouldPreserveCustomOldNode(oldNode))
                merged.Add(Clone(oldNode));
        }

        return merged;
    }

    private static List<ComponentSlot>? MergeSlots(
        List<ComponentSlot>? newSlots,
        List<ComponentSlot>? oldSlots,
        ComponentTreeChangeSet changes,
        bool preferNewShell)
    {
        if (newSlots == null || newSlots.Count == 0)
            return preferNewShell ? null : PreserveCustomOldSlots(oldSlots);
        if (oldSlots == null || oldSlots.Count == 0)
            return oldSlots == null ? CloneSlots(newSlots) : CloneSlots(newSlots);

        var merged = new List<ComponentSlot>();
        var usedOldIndexes = new HashSet<int>();
        foreach (var newSlot in newSlots)
        {
            var oldIndex = FindOldSlotIndex(oldSlots, usedOldIndexes, newSlot);
            if (oldIndex < 0)
            {
                merged.Add(Clone(newSlot));
                continue;
            }

            usedOldIndexes.Add(oldIndex);
            var oldSlot = oldSlots[oldIndex];
            var mergedSlot = Clone(newSlot);
            mergedSlot.Components = MergeComponentList(newSlot.Components, oldSlot.Components, changes, preferNewShell);
            merged.Add(mergedSlot);
        }

        for (var index = 0; index < oldSlots.Count; index++)
        {
            if (!usedOldIndexes.Contains(index) && !ContainsGeneratedIdentity(oldSlots[index]))
                merged.Add(Clone(oldSlots[index]));
        }

        return merged.Count == 0 ? null : merged;
    }

    private static List<Component>? DeserializeTree(string treeJson)
    {
        return string.IsNullOrWhiteSpace(treeJson)
            ? null
            : JsonSerializer.Deserialize<List<Component>>(treeJson, TreeJsonOptions);
    }

    private static int FindOldIndexByGenId(List<Component> oldNodes, HashSet<int> used, string genId)
    {
        for (var index = 0; index < oldNodes.Count; index++)
        {
            if (!used.Contains(index) && string.Equals(OwnGenId(oldNodes[index]), genId, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return -1;
    }

    private static int FindOldSemanticIndex(List<Component> oldNodes, HashSet<int> used, Component newNode)
    {
        var identity = SemanticIdentity(newNode);
        if (string.IsNullOrWhiteSpace(identity)) return -1;

        for (var index = 0; index < oldNodes.Count; index++)
        {
            if (used.Contains(index) ||
                !string.Equals(SemanticIdentity(oldNodes[index]), identity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(newNode.Tag, oldNodes[index].Tag, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        for (var index = 0; index < oldNodes.Count; index++)
        {
            if (!used.Contains(index) &&
                string.Equals(SemanticIdentity(oldNodes[index]), identity, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindOldStructuralIndex(List<Component> oldNodes, HashSet<int> used, Component newNode, int preferredIndex)
    {
        if (preferredIndex >= 0 && preferredIndex < oldNodes.Count &&
            !used.Contains(preferredIndex) && StructuralMatch(newNode, oldNodes[preferredIndex]))
        {
            return preferredIndex;
        }

        for (var index = 0; index < oldNodes.Count; index++)
        {
            if (!used.Contains(index) && StructuralMatch(newNode, oldNodes[index]))
                return index;
        }

        return -1;
    }

    private static int FindOldSlotIndex(List<ComponentSlot> oldSlots, HashSet<int> used, ComponentSlot newSlot)
    {
        for (var index = 0; index < oldSlots.Count; index++)
        {
            if (used.Contains(index)) continue;
            var oldSlot = oldSlots[index];
            if (string.Equals(oldSlot.Name, newSlot.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(oldSlot.Parameter ?? string.Empty, newSlot.Parameter ?? string.Empty, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string? SemanticIdentity(Component component)
    {
        var model = component.Instructions?.FirstOrDefault(instruction =>
            string.Equals(instruction.Name, "v-model", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(instruction.Value));
        return model == null ? null : $"v-model:{model.Value!.Trim()}";
    }

    private static bool StructuralMatch(Component newNode, Component oldNode)
    {
        return string.IsNullOrWhiteSpace(OwnGenId(oldNode)) &&
               string.Equals(newNode.Tag, oldNode.Tag, StringComparison.OrdinalIgnoreCase);
    }

    private static string? OwnGenId(Component component) =>
        string.IsNullOrWhiteSpace(component.GenId) ? null : component.GenId;

    private static bool IsKnownGeneratedGenId(string genId)
    {
        return genId.StartsWith("gen_field_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_col_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_child_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_owned_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_action_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_component_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_binding_", StringComparison.OrdinalIgnoreCase) ||
               genId is "gen_toolbar" or "gen_operations" or "gen_pagination" or
                   "gen_search_area" or "gen_list_area" or "gen_form_area" or
                   "gen_detail_area" or "gen_form_actions";
    }

    private static bool IsGeneratedContainerGenId(string genId)
    {
        return genId is "gen_search_area" or "gen_list_area" or "gen_form_area" or "gen_detail_area" ||
               genId.StartsWith("gen_component_", StringComparison.OrdinalIgnoreCase) ||
               genId.StartsWith("gen_child_", StringComparison.OrdinalIgnoreCase) ||
               (genId.StartsWith("gen_owned_", StringComparison.OrdinalIgnoreCase) &&
                !genId.Contains("_field_", StringComparison.OrdinalIgnoreCase) &&
                !genId.Contains("_col_", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsGeneratedIdentity(Component component)
    {
        if (!string.IsNullOrWhiteSpace(component.GenId) && IsKnownGeneratedGenId(component.GenId)) return true;
        if (!string.IsNullOrWhiteSpace(component.EntityField) || !string.IsNullOrWhiteSpace(component.EntityTable)) return true;
        if (component.Children?.Any(ContainsGeneratedIdentity) == true) return true;
        return component.UseSlots?.Any(ContainsGeneratedIdentity) == true;
    }

    private static bool ContainsGeneratedIdentity(ComponentSlot slot) =>
        slot.Components?.Any(ContainsGeneratedIdentity) == true;

    private static List<Component>? PreserveCustomOldNodes(List<Component>? oldNodes)
    {
        var custom = oldNodes?.Where(ShouldPreserveCustomOldNode).Select(Clone).ToList();
        return custom?.Count > 0 ? custom : null;
    }

    private static List<ComponentSlot>? PreserveCustomOldSlots(List<ComponentSlot>? oldSlots)
    {
        var custom = oldSlots?.Where(slot => !ContainsGeneratedIdentity(slot)).Select(Clone).ToList();
        return custom?.Count > 0 ? custom : null;
    }

    private static bool ShouldPreserveCustomOldNode(Component oldNode)
    {
        return !oldNode.IsDeleted &&
               !string.Equals(oldNode.Tag, "text", StringComparison.OrdinalIgnoreCase) &&
               !ContainsGeneratedIdentity(oldNode);
    }

    private static void PreserveScriptSection(Component target, Component source)
    {
        if (string.IsNullOrWhiteSpace(target.ScriptSection) &&
            !string.IsNullOrWhiteSpace(source.ScriptSection) &&
            IsMeaningfulTreeScriptSection(source.ScriptSection))
        {
            target.ScriptSection = source.ScriptSection;
        }
    }

    private static List<Component> CloneComponents(IEnumerable<Component> components) => components.Select(Clone).ToList();

    private static List<ComponentSlot> CloneSlots(IEnumerable<ComponentSlot> slots) => slots.Select(Clone).ToList();

    private static Component Clone(Component component)
    {
        var json = JsonSerializer.Serialize(component, TreeJsonOptions);
        return JsonSerializer.Deserialize<Component>(json, TreeJsonOptions) ?? new Component();
    }

    private static ComponentSlot Clone(ComponentSlot slot)
    {
        var json = JsonSerializer.Serialize(slot, TreeJsonOptions);
        return JsonSerializer.Deserialize<ComponentSlot>(json, TreeJsonOptions) ?? new ComponentSlot();
    }

    private static void CollectTreeScriptSections(JsonArray nodes, Dictionary<string, string> scriptMap)
    {
        foreach (var node in nodes)
        {
            var genId = node?["genId"]?.GetValue<string>();
            var scriptSection = node?["scriptSection"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(genId) && !string.IsNullOrWhiteSpace(scriptSection) &&
                IsMeaningfulTreeScriptSection(scriptSection) && !scriptMap.ContainsKey(genId))
            {
                scriptMap[genId] = scriptSection;
            }

            if (node?["children"] is JsonArray children)
                CollectTreeScriptSections(children, scriptMap);
            if (node?["useSlots"] is not JsonArray slots) continue;
            foreach (var slot in slots)
            {
                if (slot?["components"] is JsonArray components)
                    CollectTreeScriptSections(components, scriptMap);
            }
        }
    }

    private static void ApplyTreeScriptSections(JsonArray nodes, IReadOnlyDictionary<string, string> scriptMap)
    {
        foreach (var node in nodes)
        {
            var genId = node?["genId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(genId) && scriptMap.TryGetValue(genId, out var scriptSection))
                node!["scriptSection"] = scriptSection;
            if (node?["children"] is JsonArray children)
                ApplyTreeScriptSections(children, scriptMap);
            if (node?["useSlots"] is not JsonArray slots) continue;
            foreach (var slot in slots)
            {
                if (slot?["components"] is JsonArray components)
                    ApplyTreeScriptSections(components, scriptMap);
            }
        }
    }

    private static void ReorderByOldMap(JsonArray parent, IReadOnlyDictionary<string, int> orderMap)
    {
        var indexed = new List<(int Position, JsonNode? Node)>();
        var unindexed = new List<JsonNode?>();
        foreach (var child in parent)
        {
            var genId = child?["genId"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(genId) && orderMap.TryGetValue(genId, out var position))
                indexed.Add((position, child));
            else
                unindexed.Add(child);
        }

        if (indexed.Count == 0) return;
        indexed.Sort((left, right) => left.Position.CompareTo(right.Position));
        parent.Clear();
        foreach (var item in indexed) parent.Add(item.Node);
        foreach (var item in unindexed) parent.Add(item);

        foreach (var child in parent)
        {
            if (child?["children"] is JsonArray children)
                ReorderByOldMap(children, orderMap);
            if (child?["useSlots"] is not JsonArray slots) continue;
            foreach (var slot in slots)
            {
                if (slot?["components"] is JsonArray components)
                    ReorderByOldMap(components, orderMap);
            }
        }
    }

    private static bool IsMeaningfulTreeScriptSection(string scriptSection)
    {
        try
        {
            var scriptJson = ExtractTreeScriptJson(scriptSection);
            if (string.IsNullOrWhiteSpace(scriptJson)) return false;
            using var document = JsonDocument.Parse(scriptJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return true;
            foreach (var propertyName in new[]
                     {
                         "imports", "uses", "consts", "lets", "refs", "reactives", "functions",
                         "hooks", "computed", "watches", "dictRefs"
                     })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var property) &&
                    property.ValueKind == JsonValueKind.Array && property.GetArrayLength() > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static string ExtractTreeScriptJson(string raw)
    {
        var scriptJson = raw;
        for (var index = 0; index < 3; index++)
        {
            if (string.IsNullOrWhiteSpace(scriptJson)) return string.Empty;
            using var document = JsonDocument.Parse(scriptJson);
            if (document.RootElement.ValueKind == JsonValueKind.String)
            {
                scriptJson = document.RootElement.GetString() ?? string.Empty;
                continue;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("script", out var inner))
            {
                scriptJson = inner.ValueKind == JsonValueKind.String
                    ? inner.GetString() ?? string.Empty
                    : inner.GetRawText();
                continue;
            }

            return document.RootElement.GetRawText();
        }

        return scriptJson;
    }
}
