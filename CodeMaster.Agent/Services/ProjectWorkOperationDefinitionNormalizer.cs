using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using SqlSugar;

namespace CodeMaster.Agent.Services;

internal interface IProjectWorkOperationDefinitionNormalizer
{
    Task<AiProjectWorkBatchOperation> NormalizeAsync(long projectId, AiProjectWorkBatchOperation operation);
}

internal sealed partial class ProjectWorkOperationDefinitionNormalizer : IProjectWorkOperationDefinitionNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = AgentJsonSerializer.Options;
    private readonly ICodeMasterProjectBlueprintService _blueprintService;
    private readonly IProjectChangeSetDefinitionNormalizer? _changeSetDefinitionNormalizer;
    private readonly ISqlSugarClient? _db;

    public ProjectWorkOperationDefinitionNormalizer(
        ICodeMasterProjectBlueprintService blueprintService,
        IProjectChangeSetDefinitionNormalizer? changeSetDefinitionNormalizer = null,
        ISqlSugarClient? db = null)
    {
        _blueprintService = blueprintService;
        _changeSetDefinitionNormalizer = changeSetDefinitionNormalizer;
        _db = db;
    }

    public async Task<AiProjectWorkBatchOperation> NormalizeAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.DefinitionJson))
            return operation;

        string normalized;
        if (string.Equals(operation.OperationKind, "project_change_set", StringComparison.OrdinalIgnoreCase))
        {
            var proposal = _changeSetDefinitionNormalizer == null
                ? ProjectChangeSetDefinitionNormalizer.NormalizeDefinition(
                    operation.TaskKey,
                    operation.DefinitionJson,
                    await _blueprintService.GetProjectAsync(projectId))
                : await _changeSetDefinitionNormalizer.NormalizeAsync(
                    projectId,
                    operation.TaskKey,
                    operation.DefinitionJson);
            normalized = JsonSerializer.Serialize(proposal, JsonOptions);
        }
        else if (string.Equals(operation.OperationKind, "view_definition", StringComparison.OrdinalIgnoreCase))
        {
            var blueprint = await _blueprintService.GetProjectAsync(projectId);
            var replaceExistingManagedView = await HasManagedViewDefinitionAsync(
                projectId,
                ReadViewName(operation.TaskKey, operation.DefinitionJson));
            normalized = NormalizeViewDefinition(
                projectId,
                operation.TaskKey,
                operation.DefinitionJson,
                blueprint,
                replaceExistingManagedView);
        }
        else if (string.Equals(operation.OperationKind, "complex_page", StringComparison.OrdinalIgnoreCase))
        {
            normalized = await NormalizeComplexPageAsync(projectId, operation.TaskKey, operation.DefinitionJson);
        }
        else
        {
            return operation;
        }

        return new AiProjectWorkBatchOperation
        {
            TaskId = operation.TaskId,
            TaskKey = operation.TaskKey,
            OperationKind = operation.OperationKind,
            DefinitionJson = normalized,
            PayloadHash = HashText(normalized)
        };
    }

    private async Task<string> NormalizeComplexPageAsync(
        long projectId,
        string taskKey,
        string definitionJson)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(definitionJson) as JsonObject
                ?? throw new InvalidOperationException($"Task '{taskKey}' complex-page definition must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Task '{taskKey}' has invalid complex-page JSON: {ex.Message}", ex);
        }

        if (root["definition"] is JsonObject)
        {
            NormalizeCanonicalComplexPagePlan(root, taskKey);
            return root.ToJsonString(JsonOptions);
        }
        if (LooksLikeComplexPageDefinition(root))
        {
            var definition = root;
            root = new JsonObject
            {
                ["summary"] = ReadString(definition, "description") ?? ReadString(definition, "pageName") ?? taskKey,
                ["existingPageId"] = null,
                ["expectedDefinitionVersion"] = null,
                ["definition"] = definition,
                ["decisions"] = new JsonArray(),
                ["backendChanges"] = new JsonArray(),
                ["generateAfterApproval"] = false,
                ["incremental"] = true,
                ["buildAfterGeneration"] = false
            };
            NormalizeCanonicalComplexPagePlan(root, taskKey);
            return root.ToJsonString(JsonOptions);
        }
        if (_db == null)
            throw new InvalidOperationException($"Task '{taskKey}' cannot resolve an existing complex page because the metadata database is unavailable.");

        var pageId = ReadLong(root, "existingPageId");
        if (pageId <= 0)
            throw ComplexPagePathError(taskKey, "$.existingPageId", "is required for a simplified existing-page patch");
        var page = await _db.Queryable<ProjectPageRecord>()
            .Where(item => item.Id == pageId && item.ProjectId == projectId && item.IsDeleted == false)
            .FirstAsync()
            ?? throw ComplexPagePathError(taskKey, "$.existingPageId", $"references missing page '{pageId}'");

        var dataSources = await _db.Queryable<PageDataSourceRecord>()
            .Where(item => item.PageId == pageId && item.IsDeleted == false)
            .OrderBy(item => item.OrderNum)
            .ToListAsync();
        var sourceKeysById = dataSources.ToDictionary(item => item.Id, item => item.SourceKey);
        var bindings = await _db.Queryable<PageBindingRecord>()
            .Where(item => item.PageId == pageId && item.IsDeleted == false)
            .OrderBy(item => item.Id)
            .ToListAsync();
        var labelOverrides = ReadStringMap(root, "labelOverrides");

        var dataSourceNodes = new JsonArray();
        var decisions = new JsonArray();
        foreach (var source in dataSources)
        {
            dataSourceNodes.Add(new JsonObject
            {
                ["sourceKey"] = source.SourceKey,
                ["sourceType"] = source.SourceType,
                ["projectDataSourceId"] = source.ProjectDataSourceId,
                ["entityId"] = source.EntityId,
                ["serviceName"] = source.ServiceName,
                ["methodName"] = source.MethodName,
                ["dtoName"] = source.DtoName,
                ["dataShape"] = source.DataShape,
                ["syncMode"] = source.SyncMode,
                ["paramsJson"] = source.ParamsJson,
                ["orderNum"] = source.OrderNum,
                ["isEnabled"] = source.IsEnabled
            });
            decisions.Add(new JsonObject
            {
                ["target"] = $"dataSource:{source.SourceKey}",
                ["reason"] = "复用现有数据形状和 DTO；本次已确认变更仅更新页面显示名称与绑定标签"
            });
        }

        var bindingNodes = new JsonArray();
        var knownBindings = bindings.Select(item => item.BindingId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in labelOverrides.Where(pair => !knownBindings.Contains(pair.Key)))
            throw ComplexPagePathError(taskKey, $"$.labelOverrides.{pair.Key}", "references a missing page binding");

        foreach (var binding in bindings)
        {
            labelOverrides.TryGetValue(binding.BindingId, out var labelOverride);
            bindingNodes.Add(new JsonObject
            {
                ["componentId"] = binding.ComponentId,
                ["bindingId"] = binding.BindingId,
                ["generatedGenId"] = binding.GeneratedGenId,
                ["dataSourceKey"] = binding.DataSourceId.HasValue && sourceKeysById.TryGetValue(binding.DataSourceId.Value, out var sourceKey)
                    ? sourceKey
                    : null,
                ["dataSourceId"] = null,
                ["fieldId"] = binding.FieldId,
                ["bindingRole"] = binding.BindingRole,
                ["bindingStatus"] = binding.BindingStatus,
                ["syncMode"] = binding.SyncMode,
                ["autoIncludeNewFields"] = binding.AutoIncludeNewFields,
                ["controlType"] = binding.ControlType,
                ["propsJson"] = binding.PropsJson,
                ["labelOverride"] = labelOverride ?? binding.LabelOverride,
                ["origin"] = binding.Origin,
                ["lastKnownPath"] = binding.LastKnownPath
            });
            if (!string.IsNullOrWhiteSpace(labelOverride))
            {
                decisions.Add(new JsonObject
                {
                    ["target"] = $"binding:{binding.BindingId}:labelOverride",
                    ["reason"] = "根据已确认标准 JSON 更新用户可见中文标签"
                });
            }
        }

        var pageName = ReadString(root, "pageName") ?? page.PageName;
        var description = ReadString(root, "description") ?? page.Description;
        var routePath = ReadString(root, "routePath") ?? page.RoutePath;
        var summary = ReadString(root, "summary") ?? description ?? pageName;
        var normalized = new JsonObject
        {
            ["summary"] = summary,
            ["existingPageId"] = page.Id,
            ["expectedDefinitionVersion"] = ReadNullableInt(root, "expectedDefinitionVersion") ?? page.DefinitionVersion,
            ["definition"] = new JsonObject
            {
                ["projectId"] = page.ProjectId,
                ["moduleId"] = page.ModuleId,
                ["pageKey"] = ReadString(root, "pageKey") ?? page.PageKey,
                ["pageName"] = pageName,
                ["description"] = description,
                ["routePath"] = routePath,
                ["icon"] = page.Icon,
                ["orderNum"] = page.OrderNum,
                ["pageType"] = page.PageType,
                ["layoutPresetId"] = page.LayoutPresetId,
                ["layoutJson"] = page.LayoutJson,
                ["syncMode"] = page.SyncMode,
                ["isMenuSynced"] = page.IsMenuSynced,
                ["dataSources"] = dataSourceNodes,
                ["bindings"] = bindingNodes
            },
            ["decisions"] = decisions,
            ["backendChanges"] = new JsonArray(),
            ["generateAfterApproval"] = false,
            ["incremental"] = true,
            ["buildAfterGeneration"] = false
        };
        return normalized.ToJsonString(JsonOptions);
    }

    private static bool LooksLikeComplexPageDefinition(JsonObject root)
        => ReadLong(root, "projectId") > 0 &&
           ReadLong(root, "moduleId") > 0 &&
           !string.IsNullOrWhiteSpace(ReadString(root, "pageKey")) &&
           root["dataSources"] is JsonArray &&
           root["bindings"] is JsonArray;

    private static void NormalizeCanonicalComplexPagePlan(JsonObject plan, string taskKey)
    {
        if (plan["definition"] is not JsonObject definition)
            throw ComplexPagePathError(taskKey, "$.definition", "must be an object");

        var decisions = plan["decisions"] as JsonArray ?? new JsonArray();
        plan["decisions"] = decisions;
        var identities = NormalizeComplexPageBindings(definition);
        NormalizeComplexPageDecisionTargets(decisions, identities);
        NormalizeComplexPageParameters(definition, decisions);
        NormalizeComplexPageLayout(definition, taskKey, identities.ComponentIds);
        var decisionTargets = decisions
            .OfType<JsonObject>()
            .Select(item => ReadString(item, "target"))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (definition["dataSources"] is JsonArray sources)
        {
            foreach (var source in sources.OfType<JsonObject>())
            {
                var sourceKey = ReadString(source, "sourceKey");
                if (string.IsNullOrWhiteSpace(sourceKey)) continue;
                var target = $"dataSource:{sourceKey}";
                if (decisionTargets.Add(target))
                {
                    decisions.Add(new JsonObject
                    {
                        ["target"] = target,
                        ["reason"] = $"使用任务定义中已选择的 {ReadString(source, "dtoName") ?? "DTO"} 与 {ReadString(source, "dataShape") ?? "数据形态"}，保持真实服务契约不变"
                    });
                }
            }
        }

        if (definition["bindings"] is JsonArray bindings)
        {
            foreach (var binding in bindings.OfType<JsonObject>())
            {
                var controlType = ReadString(binding, "controlType");
                if (string.IsNullOrWhiteSpace(controlType)) continue;
                var identity = ReadString(binding, "bindingId") ?? ReadString(binding, "componentId");
                if (string.IsNullOrWhiteSpace(identity)) continue;
                var target = $"binding:{identity}:control";
                if (decisionTargets.Add(target))
                {
                    decisions.Add(new JsonObject
                    {
                        ["target"] = target,
                        ["reason"] = $"按结构化页面定义使用 {controlType} 呈现该绑定，保留已确认的数据源和字段语义"
                    });
                }
            }
        }

        plan["backendChanges"] ??= new JsonArray();
        plan["summary"] ??= ReadString(definition, "description") ?? ReadString(definition, "pageName") ?? taskKey;
        // Work-plan approval applies structured metadata only. Project-wide
        // generation/build is performed by the final materialization task.
        plan["generateAfterApproval"] = false;
        plan["incremental"] = true;
        plan["buildAfterGeneration"] = false;
    }

    private static ComplexPageIdentityMap NormalizeComplexPageBindings(JsonObject definition)
    {
        var result = new ComplexPageIdentityMap();
        if (definition["bindings"] is not JsonArray bindings) return result;

        var usedComponentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedBindingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in bindings.ToList())
        {
            if (node is not JsonObject binding) continue;
            var sourceKey = ReadString(binding, "dataSourceKey");
            if (string.IsNullOrWhiteSpace(sourceKey) && ReadLong(binding, "dataSourceId") <= 0)
            {
                bindings.Remove(node);
                continue;
            }

            var oldComponentId = ReadString(binding, "componentId") ?? "component";
            var componentId = EnsureUniqueStableId(oldComponentId, "component", usedComponentIds);
            result.ComponentIds[oldComponentId] = componentId;
            binding["componentId"] = componentId;

            var oldBindingId = ReadString(binding, "bindingId");
            if (!string.IsNullOrWhiteSpace(oldBindingId))
            {
                var bindingId = EnsureUniqueStableId(oldBindingId, $"binding-{componentId}", usedBindingIds);
                result.BindingIds[oldBindingId] = bindingId;
                binding["bindingId"] = bindingId;
            }
        }
        return result;
    }

    private static void NormalizeComplexPageDecisionTargets(JsonArray decisions, ComplexPageIdentityMap identities)
    {
        foreach (var decision in decisions.OfType<JsonObject>())
        {
            var target = ReadString(decision, "target");
            if (string.IsNullOrWhiteSpace(target) || !target.StartsWith("binding:", StringComparison.OrdinalIgnoreCase))
                continue;
            var suffixIndex = target.IndexOf(':', "binding:".Length);
            if (suffixIndex < 0) continue;
            var identity = target["binding:".Length..suffixIndex];
            var normalized = identities.BindingIds.TryGetValue(identity, out var bindingId)
                ? bindingId
                : identities.ComponentIds.TryGetValue(identity, out var componentId)
                    ? componentId
                    : NormalizeStableId(identity, "binding");
            decision["target"] = $"binding:{normalized}{target[suffixIndex..]}";
        }
    }

    private static void NormalizeComplexPageParameters(JsonObject definition, JsonArray decisions)
    {
        if (definition["dataSources"] is not JsonArray sources) return;
        foreach (var source in sources.OfType<JsonObject>())
        {
            var paramsJson = ReadString(source, "paramsJson");
            if (string.IsNullOrWhiteSpace(paramsJson) || !ContainsImplicitParentReference(paramsJson)) continue;
            source["paramsJson"] = "{}";
            var sourceKey = ReadString(source, "sourceKey");
            if (string.IsNullOrWhiteSpace(sourceKey)) continue;
            var target = $"dataSource:{sourceKey}";
            var existing = decisions.OfType<JsonObject>().LastOrDefault(item =>
                string.Equals(ReadString(item, "target"), target, StringComparison.OrdinalIgnoreCase));
            var reason = "移除不受支持的隐式 parent 引用，改用可真实执行的独立数据列表；后续如需主从联动必须使用结构化页面或事件参数引用";
            if (existing == null)
                decisions.Add(new JsonObject { ["target"] = target, ["reason"] = reason });
            else
                existing["reason"] = reason;
        }
    }

    private static bool ContainsImplicitParentReference(string json)
    {
        try
        {
            return ContainsImplicitParentReference(JsonNode.Parse(json));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsImplicitParentReference(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var normalized = text.Trim().TrimStart('$', '{').TrimEnd('}');
            return normalized.StartsWith("parent.", StringComparison.OrdinalIgnoreCase);
        }
        if (node is JsonObject map)
            return map.Any(item => ContainsImplicitParentReference(item.Value));
        return node is JsonArray array && array.Any(ContainsImplicitParentReference);
    }

    private static void NormalizeComplexPageLayout(
        JsonObject definition,
        string taskKey,
        IReadOnlyDictionary<string, string> componentIds)
    {
        var layoutJson = ReadString(definition, "layoutJson");
        if (string.IsNullOrWhiteSpace(layoutJson))
        {
            definition["layoutJson"] = BuildComplexPageLayout(definition).ToJsonString(JsonOptions);
            return;
        }

        JsonNode layout;
        try
        {
            layout = JsonNode.Parse(layoutJson)
                ?? throw ComplexPagePathError(taskKey, "$.definition.layoutJson", "must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Task '{taskKey}' complex-page path $.definition.layoutJson contains invalid JSON: {ex.Message}", ex);
        }

        if (layout is not JsonObject layoutObject || layoutObject["root"] is not JsonObject)
        {
            layout = BuildComplexPageLayout(definition);
        }
        else
        {
            NormalizeComplexPageLayoutNode(layout, componentIds, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var layoutIds = CollectComplexPageLayoutIds(layout);
            var bindingIds = (definition["bindings"] as JsonArray)?.OfType<JsonObject>()
                .Select(item => ReadString(item, "componentId"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToList() ?? [];
            if (bindingIds.Any(item => !layoutIds.Contains(item)))
                layout = BuildComplexPageLayout(definition);
        }
        definition["layoutJson"] = layout.ToJsonString(JsonOptions);
    }

    private static void NormalizeComplexPageLayoutNode(
        JsonNode? node,
        IReadOnlyDictionary<string, string> componentIds,
        HashSet<string> usedIds)
    {
        if (node is JsonObject value)
        {
            var originalId = ReadString(value, "componentId") ?? ReadString(value, "id") ?? "component";
            var preferredId = componentIds.TryGetValue(originalId, out var mapped) ? mapped : originalId;
            value["componentId"] = EnsureUniqueStableId(preferredId, "component", usedIds);

            // Bindings are the authoritative data-binding contract. Models often
            // repeat these fields inside the layout tree, but PageLayoutTree
            // requires them to be materialized together by ProjectPageService.
            value.Remove("dataSourceKey");
            value.Remove("dataSourceId");
            value.Remove("fieldId");
            value.Remove("bindingId");

            foreach (var child in value.ToList())
                NormalizeComplexPageLayoutNode(child.Value, componentIds, usedIds);
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var child in array)
                NormalizeComplexPageLayoutNode(child, componentIds, usedIds);
        }
    }

    private static HashSet<string> CollectComplexPageLayoutIds(JsonNode? node)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectComplexPageLayoutIds(node, result);
        return result;
    }

    private static void CollectComplexPageLayoutIds(JsonNode? node, HashSet<string> result)
    {
        if (node is JsonObject value)
        {
            var componentId = ReadString(value, "componentId");
            if (!string.IsNullOrWhiteSpace(componentId)) result.Add(componentId);
            foreach (var child in value)
                CollectComplexPageLayoutIds(child.Value, result);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                CollectComplexPageLayoutIds(child, result);
        }
    }

    private static JsonObject BuildComplexPageLayout(JsonObject definition)
    {
        var pageKey = NormalizeStableId(ReadString(definition, "pageKey") ?? "page", "page");
        var root = new JsonObject
        {
            ["type"] = "page",
            ["componentId"] = $"{pageKey}-root",
            ["children"] = new JsonArray()
        };
        var bindings = (definition["bindings"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var groups = bindings
            .Select((binding, index) => new { binding, index })
            .GroupBy(item => ReadComplexPageSectionKey(item.binding, item.index), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootChildren = (JsonArray)root["children"]!;
        foreach (var group in groups)
        {
            var sectionKey = NormalizeStableId(group.Key, "section");
            var groupBindings = group.Select(item => item.binding).ToList();
            var content = new JsonArray();
            var consumed = new HashSet<JsonObject>();
            foreach (var table in groupBindings.Where(item => ReadBindingRole(item) == "Table"))
            {
                var tableNode = CreateComplexPageLayoutBindingNode(table);
                var tableChildren = (JsonArray)tableNode["children"]!;
                var sourceKey = ReadString(table, "dataSourceKey");
                foreach (var column in groupBindings.Where(item =>
                             ReadBindingRole(item) == "Column" &&
                             string.Equals(ReadString(item, "dataSourceKey"), sourceKey, StringComparison.OrdinalIgnoreCase)))
                {
                    tableChildren.Add(CreateComplexPageLayoutBindingNode(column));
                    consumed.Add(column);
                }
                content.Add(tableNode);
                consumed.Add(table);
            }
            foreach (var binding in groupBindings.Where(item => !consumed.Contains(item)))
                content.Add(CreateComplexPageLayoutBindingNode(binding));

            rootChildren.Add(new JsonObject
            {
                ["type"] = "row",
                ["componentId"] = $"{pageKey}-{sectionKey}-row",
                ["children"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "column",
                        ["componentId"] = $"{pageKey}-{sectionKey}-column",
                        ["span"] = 24,
                        ["children"] = content
                    }
                }
            });
        }
        return new JsonObject { ["version"] = 1, ["root"] = root };
    }

    private static JsonObject CreateComplexPageLayoutBindingNode(JsonObject binding)
        => new()
        {
            ["type"] = ReadBindingRole(binding) switch
            {
                "Table" => "table",
                "Column" => "table-column",
                "Form" => "form",
                "Detail" => "detail",
                "Field" => "form-field",
                "Event" => "button",
                _ => "component"
            },
            ["componentId"] = ReadString(binding, "componentId"),
            ["children"] = new JsonArray()
        };

    private static string ReadComplexPageSectionKey(JsonObject binding, int index)
    {
        var path = ReadString(binding, "lastKnownPath");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var match = Regex.Match(path, @"sections\.([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value;
        }
        return ReadString(binding, "dataSourceKey") ?? $"section-{index + 1}";
    }

    private static string ReadBindingRole(JsonObject binding)
    {
        var role = ReadString(binding, "bindingRole");
        if (!string.IsNullOrWhiteSpace(role)) return role;
        return ReadLong(binding, "bindingRole") switch
        {
            2 => "Field",
            3 => "Table",
            4 => "Column",
            5 => "Form",
            6 => "Detail",
            7 => "Options",
            8 => "Event",
            _ => "Container"
        };
    }

    private static string EnsureUniqueStableId(string? value, string fallback, HashSet<string> usedIds)
    {
        var baseId = NormalizeStableId(value, fallback);
        var result = baseId;
        var suffix = 2;
        while (!usedIds.Add(result)) result = $"{baseId}-{suffix++}";
        return result;
    }

    private static string NormalizeStableId(string? value, string fallback)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim(), "[^A-Za-z0-9_-]+", "-").Trim('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    internal static string NormalizeViewDefinitionForTest(
        long projectId,
        string taskKey,
        string definitionJson,
        AiProjectBlueprintDto blueprint)
        => NormalizeViewDefinition(projectId, taskKey, definitionJson, blueprint, false);

    private static string NormalizeViewDefinition(
        long projectId,
        string taskKey,
        string definitionJson,
        AiProjectBlueprintDto blueprint,
        bool replaceExistingManagedView)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(definitionJson) as JsonObject
                ?? throw new InvalidOperationException($"Task '{taskKey}' view definition must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Task '{taskKey}' has invalid view-definition JSON: {ex.Message}", ex);
        }

        if (root["query"] is JsonObject && root["entityFields"] is JsonArray canonicalFields && ReadLong(root, "moduleId") > 0)
        {
            NormalizeCanonicalViewConsumer(root, canonicalFields, blueprint, taskKey);
            return root.ToJsonString(JsonOptions);
        }

        var sql = ReadString(root, "sql");
        if (string.IsNullOrWhiteSpace(sql))
            throw PathError(taskKey, "$.sql", "is required for a simplified view definition");
        var moduleName = ReadString(root, "moduleName");
        var module = blueprint.Modules.FirstOrDefault(item =>
            string.Equals(item.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
        if (module == null)
            throw PathError(taskKey, "$.moduleName", $"references missing module '{moduleName}'");
        var entityName = ReadString(root, "entityName");
        var viewName = ReadString(root, "viewName");
        var existingConsumer = module.Entities.FirstOrDefault(item =>
            item.IsReadOnly &&
            string.Equals(item.Name, entityName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.TableName, viewName, StringComparison.OrdinalIgnoreCase));
        sql = NormalizePostgreSqlSelectAliases(
            taskKey,
            sql,
            blueprint.Project.DatabaseType,
            existingConsumer);

        var selectItems = ParseSelectItems(taskKey, sql);
        var aliasEntities = ResolveSqlAliases(sql, blueprint);
        var fields = new JsonArray();
        for (var index = 0; index < selectItems.Count; index++)
        {
            var item = selectItems[index];
            var inferred = InferField(item.Expression, item.Alias, aliasEntities);
            fields.Add(new JsonObject
            {
                ["name"] = ToPascalIdentifier(item.Alias),
                ["displayName"] = item.Alias,
                ["columnName"] = item.Alias,
                ["dataType"] = inferred.DataType,
                ["isNullable"] = inferred.IsNullable,
                ["maxLength"] = inferred.MaxLength,
                ["precision"] = inferred.Precision,
                ["scale"] = inferred.Scale,
                ["showInList"] = true,
                ["showInDetail"] = true,
                ["showInSearch"] = inferred.ShowInSearch,
                ["isSortable"] = true,
                ["formControlType"] = inferred.ControlType,
                ["orderNum"] = index + 1
            });
        }

        var displayName = ReadString(root, "displayName") ?? entityName ?? taskKey;
        var summary = ReadString(root, "summary") ?? displayName;
        var reason = ReadString(root, "reason") ?? $"创建{displayName}只读业务视图";
        var normalized = new JsonObject
        {
            ["summary"] = summary,
            ["reason"] = reason,
            ["query"] = new JsonObject
            {
                ["projectId"] = projectId,
                ["sql"] = sql,
                ["entities"] = new JsonArray(),
                ["joins"] = new JsonArray(),
                ["includeInferredJoins"] = false
            },
            ["entityFields"] = fields,
            ["viewName"] = viewName,
            ["replaceExistingView"] = ReadBoolean(root, "replaceExistingView") || replaceExistingManagedView,
            ["moduleId"] = module.Id,
            ["existingEntityId"] = existingConsumer?.Id,
            ["entityName"] = entityName,
            ["displayName"] = displayName,
            ["keyColumn"] = ReadString(root, "keyColumn"),
            ["generateFrontend"] = ReadBoolean(root, "generateFrontend", true),
            ["generateAfterApproval"] = false,
            ["incremental"] = true,
            ["buildAfterGeneration"] = false
        };
        return normalized.ToJsonString(JsonOptions);
    }

    private static void NormalizeCanonicalViewConsumer(
        JsonObject root,
        JsonArray fields,
        AiProjectBlueprintDto blueprint,
        string taskKey)
    {
        var moduleId = ReadLong(root, "moduleId");
        var entityName = ReadString(root, "entityName");
        var viewName = ReadString(root, "viewName");
        var existingConsumer = blueprint.Modules
            .FirstOrDefault(item => item.Id == moduleId)?
            .Entities.FirstOrDefault(item =>
                item.IsReadOnly &&
                string.Equals(item.Name, entityName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.TableName, viewName, StringComparison.OrdinalIgnoreCase));
        if (root["query"] is JsonObject query)
        {
            var sql = ReadString(query, "sql");
            if (!string.IsNullOrWhiteSpace(sql))
            {
                sql = NormalizePostgreSqlSelectAliases(
                    taskKey,
                    sql,
                    blueprint.Project.DatabaseType,
                    existingConsumer);
                query["sql"] = sql;
                if (IsPostgreSql(blueprint.Project.DatabaseType))
                {
                    var aliases = ParseSelectItems(taskKey, sql).Select(item => item.Alias).ToList();
                    var fieldList = fields.OfType<JsonObject>().ToList();
                    for (var index = 0; index < Math.Min(aliases.Count, fieldList.Count); index++)
                        fieldList[index]["columnName"] = aliases[index];
                }
            }
        }

        foreach (var field in fields.OfType<JsonObject>())
        {
            var columnName = ReadString(field, "columnName") ?? ReadString(field, "name");
            var requestedName = ReadString(field, "name");
            var existingField = existingConsumer?.Fields.FirstOrDefault(item =>
                string.Equals(NormalizeName(item.Name), NormalizeName(requestedName ?? string.Empty), StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(item.ColumnName) &&
                 string.Equals(NormalizeName(item.ColumnName), NormalizeName(columnName ?? string.Empty), StringComparison.OrdinalIgnoreCase)));
            if (existingField != null)
            {
                field["name"] = existingField.Name;
                if (!string.IsNullOrWhiteSpace(existingField.ColumnName))
                    field["columnName"] = existingField.ColumnName;
            }
            else if (!string.IsNullOrWhiteSpace(columnName))
            {
                field["name"] = ToPascalIdentifier(columnName);
            }
        }

        if (existingConsumer != null)
            root["existingEntityId"] = existingConsumer.Id;
    }

    private static string NormalizePostgreSqlSelectAliases(
        string taskKey,
        string sql,
        string? databaseType,
        AiEntityBlueprintDto? existingConsumer = null)
    {
        if (!IsPostgreSql(databaseType)) return sql;
        var selectMatch = SelectClauseRegex().Match(sql);
        if (!selectMatch.Success)
            throw PathError(taskKey, "$.sql", "must contain a supported SELECT ... FROM statement");
        var clauseGroup = selectMatch.Groups[1];
        var rewritten = SplitTopLevel(clauseGroup.Value)
            .Select((item, index) =>
            {
                var alias = SelectAliasRegex().Match(item);
                if (!alias.Success)
                    throw PathError(taskKey, $"$.sql.select[{index}]", "requires an explicit AS alias");
                var aliasName = alias.Groups[2].Value;
                var existingField = existingConsumer?.Fields.FirstOrDefault(field =>
                    string.Equals(
                        NormalizeName(field.Name),
                        NormalizeName(ToPascalIdentifier(aliasName)),
                        StringComparison.OrdinalIgnoreCase));
                var physicalColumn = !string.IsNullOrWhiteSpace(existingField?.ColumnName)
                    ? existingField.ColumnName
                    : ToSnakeIdentifier(aliasName);
                return $"{alias.Groups[1].Value.Trim()} AS {physicalColumn}";
            });
        var clause = string.Join(", ", rewritten);
        return sql[..clauseGroup.Index] + clause + sql[(clauseGroup.Index + clauseGroup.Length)..];
    }

    private static bool IsPostgreSql(string? databaseType)
        => string.Equals(databaseType, "PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(databaseType, "Postgres", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(databaseType, "Npgsql", StringComparison.OrdinalIgnoreCase);

    private static string ToSnakeIdentifier(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().Trim('"', '`', '[', ']'))
        {
            if (!char.IsLetterOrDigit(character))
            {
                if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
                continue;
            }
            if (char.IsUpper(character) && builder.Length > 0 && builder[^1] != '_') builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString().Trim('_');
    }

    private static string ToPascalIdentifier(string value)
    {
        var parts = Regex.Split(value.Trim(), "[^A-Za-z0-9]+")
            .Where(item => item.Length > 0)
            .ToList();
        if (parts.Count == 0)
            return value.Trim();
        return string.Concat(parts.Select(part =>
            char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static List<SelectItem> ParseSelectItems(string taskKey, string sql)
    {
        var selectMatch = SelectClauseRegex().Match(sql);
        if (!selectMatch.Success)
            throw PathError(taskKey, "$.sql", "must contain a supported SELECT ... FROM statement");
        var clause = selectMatch.Groups[1].Value;
        var parts = SplitTopLevel(clause);
        var result = new List<SelectItem>();
        for (var index = 0; index < parts.Count; index++)
        {
            var match = SelectAliasRegex().Match(parts[index]);
            if (!match.Success)
                throw PathError(taskKey, $"$.sql.select[{index}]", "requires an explicit AS alias");
            result.Add(new SelectItem(match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()));
        }
        return result;
    }

    private static List<string> SplitTopLevel(string value)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (quote != '\0')
            {
                if (current == quote && (index == 0 || value[index - 1] != '\\'))
                    quote = '\0';
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == '(') depth++;
            else if (current == ')') depth--;
            else if (current == ',' && depth == 0)
            {
                result.Add(value[start..index].Trim());
                start = index + 1;
            }
        }
        result.Add(value[start..].Trim());
        return result.Where(item => item.Length > 0).ToList();
    }

    private static Dictionary<string, AiEntityBlueprintDto> ResolveSqlAliases(string sql, AiProjectBlueprintDto blueprint)
    {
        var entities = blueprint.Modules.SelectMany(item => item.Entities).ToList();
        var result = new Dictionary<string, AiEntityBlueprintDto>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TableAliasRegex().Matches(sql))
        {
            var table = StripIdentifier(match.Groups[1].Value.Split('.').Last());
            var alias = StripIdentifier(match.Groups[2].Value);
            var entity = entities.FirstOrDefault(item =>
                string.Equals(StripIdentifier(item.TableName ?? string.Empty), table, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ToSnakePlural(item.Name), table, StringComparison.OrdinalIgnoreCase));
            if (entity != null)
                result[alias] = entity;
        }
        return result;
    }

    private static InferredField InferField(
        string expression,
        string alias,
        IReadOnlyDictionary<string, AiEntityBlueprintDto> aliasEntities)
    {
        var direct = DirectColumnRegex().Match(expression.Trim());
        if (direct.Success && aliasEntities.TryGetValue(direct.Groups[1].Value, out var entity))
        {
            var column = StripIdentifier(direct.Groups[2].Value);
            var field = entity.Fields.FirstOrDefault(item =>
                string.Equals(NormalizeName(item.Name), NormalizeName(column), StringComparison.OrdinalIgnoreCase));
            if (field != null)
                return FromField(field);
            if (string.Equals(column, "id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(column, "tenant_id", StringComparison.OrdinalIgnoreCase))
                return new("long", false, null, null, null, true, "number");
        }

        var referenced = ColumnReferenceRegex().Matches(expression)
            .Cast<Match>()
            .Select(match =>
            {
                if (!aliasEntities.TryGetValue(match.Groups[1].Value, out var source)) return null;
                var column = match.Groups[2].Value;
                return source.Fields.FirstOrDefault(item =>
                    string.Equals(NormalizeName(item.Name), NormalizeName(column), StringComparison.OrdinalIgnoreCase));
            })
            .Where(item => item != null)
            .Cast<AiFieldBlueprintDto>()
            .ToList();
        if (referenced.Any(item => IsDecimalType(item.DataType)))
            return new("decimal", referenced.Any(item => item.IsNullable), null, 18, 4, false, "number");
        if (alias.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || string.Equals(alias, "Id", StringComparison.OrdinalIgnoreCase))
            return new("long", false, null, null, null, true, "number");
        if (alias.EndsWith("Count", StringComparison.OrdinalIgnoreCase) || alias.EndsWith("Days", StringComparison.OrdinalIgnoreCase))
            return new("int", false, null, null, null, false, "number");
        if (alias.StartsWith("Is", StringComparison.OrdinalIgnoreCase) || alias.StartsWith("Has", StringComparison.OrdinalIgnoreCase) || alias.StartsWith("Needs", StringComparison.OrdinalIgnoreCase))
            return new("bool", false, null, null, null, true, "switch");
        if (referenced.Count > 0)
            return FromField(referenced[0]);
        return new("string", true, 500, null, null, true, "input");
    }

    private static InferredField FromField(AiFieldBlueprintDto field)
    {
        var dataType = field.DataType;
        var control = dataType switch
        {
            "DateOnly" => "date",
            "DateTime" or "DateTimeOffset" => "datetime",
            "bool" => "switch",
            _ when IsNumericType(dataType) => "number",
            _ => "input"
        };
        return new(dataType, field.IsNullable, field.MaxLength, field.Precision, field.Scale,
            dataType is "string" or "DateOnly" or "DateTime" or "DateTimeOffset" or "bool" || field.IsPrimaryKey,
            control);
    }

    private static bool IsNumericType(string value)
        => value is "byte" or "short" or "int" or "long" or "float" or "double" or "decimal";

    private static bool IsDecimalType(string value)
        => value is "float" or "double" or "decimal";

    private static string NormalizeName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string StripIdentifier(string value)
        => value.Trim().Trim('"', '`', '[', ']');

    private static string ToSnakePlural(string value)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index])) builder.Append('_');
            builder.Append(char.ToLowerInvariant(value[index]));
        }
        if (!builder.ToString().EndsWith('s')) builder.Append('s');
        return builder.ToString();
    }

    private static string? ReadString(JsonObject value, string propertyName)
        => value.FirstOrDefault(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value is JsonValue node &&
           node.TryGetValue<string>(out var result) ? result : null;

    private static long ReadLong(JsonObject value, string propertyName)
    {
        var node = value.FirstOrDefault(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value;
        if (node is not JsonValue jsonValue) return 0;
        if (jsonValue.TryGetValue<long>(out var result)) return result;
        return jsonValue.TryGetValue<string>(out var text) && long.TryParse(text, out result) ? result : 0;
    }

    private static bool ReadBoolean(JsonObject value, string propertyName, bool defaultValue = false)
        => value.FirstOrDefault(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value is JsonValue node &&
           node.TryGetValue<bool>(out var result) ? result : defaultValue;

    private static int? ReadNullableInt(JsonObject value, string propertyName)
    {
        var node = value.FirstOrDefault(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value;
        if (node is not JsonValue jsonValue) return null;
        if (jsonValue.TryGetValue<int>(out var result)) return result;
        return jsonValue.TryGetValue<string>(out var text) && int.TryParse(text, out result) ? result : null;
    }

    private static Dictionary<string, string> ReadStringMap(JsonObject value, string propertyName)
    {
        var node = value.FirstOrDefault(item => string.Equals(item.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value;
        if (node == null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is not JsonObject map)
            throw new InvalidOperationException($"Complex-page path $.{propertyName} must be an object.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in map)
        {
            if (pair.Value is not JsonValue item || !item.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"Complex-page path $.{propertyName}.{pair.Key} requires a non-empty string.");
            result[pair.Key] = text;
        }
        return result;
    }

    private async Task<bool> HasManagedViewDefinitionAsync(long projectId, string viewName)
    {
        if (_db == null || string.IsNullOrWhiteSpace(viewName))
            return false;
        try
        {
            return await _db.Queryable<ManagedViewDefinitionRecord>()
                .AnyAsync(item => item.ProjectId == projectId &&
                                  item.ViewName == viewName &&
                                  item.IsDeleted == false);
        }
        catch
        {
            // The optional UI-composition module may be absent. Its adapter will
            // report that capability error later; do not make the normalizer
            // depend on an optional table existing.
            return false;
        }
    }

    private static string ReadViewName(string taskKey, string definitionJson)
    {
        try
        {
            var root = JsonNode.Parse(definitionJson) as JsonObject;
            return root == null ? string.Empty : ReadString(root, "viewName") ?? string.Empty;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Task '{taskKey}' has invalid view-definition JSON: {ex.Message}", ex);
        }
    }

    private static string HashText(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static InvalidOperationException PathError(string taskKey, string path, string message)
        => new($"Task '{taskKey}' view-definition path {path} {message}.");

    private static InvalidOperationException ComplexPagePathError(string taskKey, string path, string message)
        => new($"Task '{taskKey}' complex-page path {path} {message}.");

    private sealed record SelectItem(string Expression, string Alias);
    private sealed record InferredField(
        string DataType,
        bool IsNullable,
        int? MaxLength,
        int? Precision,
        int? Scale,
        bool ShowInSearch,
        string ControlType);

    private sealed class ComplexPageIdentityMap
    {
        public Dictionary<string, string> ComponentIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BindingIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [SugarTable("sys_project_page")]
    private sealed class ProjectPageRecord
    {
        public long Id { get; set; }
        [SugarColumn(ColumnName = "project_id")] public long ProjectId { get; set; }
        [SugarColumn(ColumnName = "module_id")] public long ModuleId { get; set; }
        [SugarColumn(ColumnName = "page_key")] public string PageKey { get; set; } = string.Empty;
        [SugarColumn(ColumnName = "page_name")] public string PageName { get; set; } = string.Empty;
        public string? Description { get; set; }
        [SugarColumn(ColumnName = "route_path")] public string RoutePath { get; set; } = string.Empty;
        public string? Icon { get; set; }
        [SugarColumn(ColumnName = "order_num")] public int OrderNum { get; set; }
        [SugarColumn(ColumnName = "page_type")] public int PageType { get; set; }
        [SugarColumn(ColumnName = "layout_preset_id")] public long? LayoutPresetId { get; set; }
        [SugarColumn(ColumnName = "layout_json")] public string LayoutJson { get; set; } = string.Empty;
        [SugarColumn(ColumnName = "definition_version")] public int DefinitionVersion { get; set; }
        [SugarColumn(ColumnName = "sync_mode")] public int SyncMode { get; set; }
        [SugarColumn(ColumnName = "is_menu_synced")] public bool IsMenuSynced { get; set; }
        [SugarColumn(ColumnName = "is_deleted")] public bool IsDeleted { get; set; }
    }

    [SugarTable("sys_page_data_source")]
    private sealed class PageDataSourceRecord
    {
        public long Id { get; set; }
        [SugarColumn(ColumnName = "page_id")] public long PageId { get; set; }
        [SugarColumn(ColumnName = "source_key")] public string SourceKey { get; set; } = string.Empty;
        [SugarColumn(ColumnName = "source_type")] public int SourceType { get; set; }
        [SugarColumn(ColumnName = "project_data_source_id")] public long? ProjectDataSourceId { get; set; }
        [SugarColumn(ColumnName = "entity_id")] public long? EntityId { get; set; }
        [SugarColumn(ColumnName = "service_name")] public string? ServiceName { get; set; }
        [SugarColumn(ColumnName = "method_name")] public string? MethodName { get; set; }
        [SugarColumn(ColumnName = "dto_name")] public string? DtoName { get; set; }
        [SugarColumn(ColumnName = "data_shape")] public int DataShape { get; set; }
        [SugarColumn(ColumnName = "sync_mode")] public int SyncMode { get; set; }
        [SugarColumn(ColumnName = "params_json")] public string? ParamsJson { get; set; }
        [SugarColumn(ColumnName = "order_num")] public int OrderNum { get; set; }
        [SugarColumn(ColumnName = "is_enabled")] public bool IsEnabled { get; set; }
        [SugarColumn(ColumnName = "is_deleted")] public bool IsDeleted { get; set; }
    }

    [SugarTable("sys_page_component_binding")]
    private sealed class PageBindingRecord
    {
        public long Id { get; set; }
        [SugarColumn(ColumnName = "page_id")] public long PageId { get; set; }
        [SugarColumn(ColumnName = "component_id")] public string ComponentId { get; set; } = string.Empty;
        [SugarColumn(ColumnName = "binding_id")] public string BindingId { get; set; } = string.Empty;
        [SugarColumn(ColumnName = "generated_gen_id")] public string? GeneratedGenId { get; set; }
        [SugarColumn(ColumnName = "data_source_id")] public long? DataSourceId { get; set; }
        [SugarColumn(ColumnName = "field_id")] public long? FieldId { get; set; }
        [SugarColumn(ColumnName = "binding_role")] public int BindingRole { get; set; }
        [SugarColumn(ColumnName = "binding_status")] public int BindingStatus { get; set; }
        [SugarColumn(ColumnName = "sync_mode")] public int SyncMode { get; set; }
        [SugarColumn(ColumnName = "auto_include_new_fields")] public bool AutoIncludeNewFields { get; set; }
        [SugarColumn(ColumnName = "control_type")] public string? ControlType { get; set; }
        [SugarColumn(ColumnName = "props_json")] public string? PropsJson { get; set; }
        [SugarColumn(ColumnName = "label_override")] public string? LabelOverride { get; set; }
        public int Origin { get; set; }
        [SugarColumn(ColumnName = "last_known_path")] public string? LastKnownPath { get; set; }
        [SugarColumn(ColumnName = "is_deleted")] public bool IsDeleted { get; set; }
    }

    [SugarTable("sys_project_view_definition")]
    private sealed class ManagedViewDefinitionRecord
    {
        [SugarColumn(ColumnName = "project_id")]
        public long ProjectId { get; set; }

        [SugarColumn(ColumnName = "view_name")]
        public string ViewName { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "is_deleted")]
        public bool IsDeleted { get; set; }
    }

    [GeneratedRegex(@"(?is)^\s*SELECT\s+(.*?)\s+FROM\s+")]
    private static partial Regex SelectClauseRegex();

    [GeneratedRegex(@"(?is)^(.*?)\s+AS\s+[\""`\[]?([A-Za-z_][A-Za-z0-9_]*)[\""`\]]?\s*$")]
    private static partial Regex SelectAliasRegex();

    [GeneratedRegex(@"(?i)\b(?:FROM|JOIN)\s+([A-Za-z0-9_\.\""`\[\]]+)\s+(?:AS\s+)?([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TableAliasRegex();

    [GeneratedRegex(@"(?i)^[\""`\[]?([A-Za-z_][A-Za-z0-9_]*)[\""`\]]?\.[\""`\[]?([A-Za-z_][A-Za-z0-9_]*)[\""`\]]?$")]
    private static partial Regex DirectColumnRegex();

    [GeneratedRegex(@"(?i)([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ColumnReferenceRegex();
}
