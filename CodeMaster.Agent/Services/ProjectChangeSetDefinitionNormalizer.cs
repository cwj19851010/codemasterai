using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;

namespace CodeMaster.Agent.Services;

internal interface IProjectChangeSetDefinitionNormalizer
{
    Task<ProjectChangeSetProposal> NormalizeAsync(
        long projectId,
        string taskKey,
        string definitionJson);
}

internal sealed class ProjectChangeSetDefinitionNormalizer : IProjectChangeSetDefinitionNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = AgentJsonSerializer.Options;
    private static readonly HashSet<string> EntityUpdateProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "businessDescription", "tableName", "hasPrimaryKey",
        "isTree", "isReadOnly", "hasTenant", "hasDataPermission", "hasAudit",
        "hasSoftDelete", "generateFrontend", "isChildTable", "frontendRoute",
        "menuIcon", "orderNum", "remark"
    };

    private readonly ICodeMasterProjectBlueprintService _blueprintService;

    public ProjectChangeSetDefinitionNormalizer(ICodeMasterProjectBlueprintService blueprintService)
        => _blueprintService = blueprintService;

    public async Task<ProjectChangeSetProposal> NormalizeAsync(
        long projectId,
        string taskKey,
        string definitionJson)
    {
        var blueprint = await _blueprintService.GetProjectAsync(projectId);
        return Normalize(taskKey, definitionJson, blueprint);
    }

    internal static ProjectChangeSetProposal NormalizeForTest(
        string taskKey,
        string definitionJson,
        AiProjectBlueprintDto blueprint)
        => NormalizeDefinition(taskKey, definitionJson, blueprint);

    internal static ProjectChangeSetProposal NormalizeDefinition(
        string taskKey,
        string definitionJson,
        AiProjectBlueprintDto blueprint)
        => Normalize(taskKey, definitionJson, blueprint);

    private static ProjectChangeSetProposal Normalize(
        string taskKey,
        string definitionJson,
        AiProjectBlueprintDto blueprint)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
            throw new InvalidOperationException($"Task '{taskKey}' has no project change-set definition.");

        JsonObject root;
        try
        {
            root = JsonNode.Parse(definitionJson) as JsonObject
                ?? throw new InvalidOperationException($"Task '{taskKey}' project change-set definition must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Task '{taskKey}' has invalid project change-set JSON: {ex.Message}", ex);
        }

        // Some models serialize typed tool arguments with PascalCase names
        // (Entities, ModuleName, Fields), while this JsonNode normalizer uses
        // the canonical camelCase contract. Normalize the entire input tree
        // before reading it so a valid payload is not reduced to an empty
        // change set.
        root = ToCamelCase(root) as JsonObject
            ?? throw new InvalidOperationException($"Task '{taskKey}' project change-set definition must be a JSON object.");
        NormalizeAliases(root);

        var entities = blueprint.Modules.SelectMany(module => module.Entities).ToList();
        var entitiesById = entities.ToDictionary(entity => entity.Id);
        var entitiesByName = entities
            .GroupBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        root["summary"] = ReadString(root, "summary")
            ?? ReadString(root, "action")
            ?? taskKey;
        root.Remove("action");

        var canonicalCreates = new JsonArray();
        var canonicalUpdates = ReadObjectArray(root, "entityUpdates");
        var updatesByEntityId = new Dictionary<long, JsonObject>();
        foreach (var update in canonicalUpdates.OfType<JsonObject>())
        {
            var entityId = ReadLong(update, "entityId");
            if (entityId > 0)
                updatesByEntityId[entityId] = update;
        }

        if (root["entities"] is JsonArray entityItems)
        {
            for (var index = 0; index < entityItems.Count; index++)
            {
                if (entityItems[index] is not JsonObject entityItem)
                    throw PathError(taskKey, $"$.entities[{index}]", "must be an object");

                var entityId = ReadLong(entityItem, "entityId");
                if (entityId <= 0)
                {
                    if (entityItem["fields"] is JsonObject)
                        throw PathError(taskKey, $"$.entities[{index}].fields", "must be an array when creating an entity");
                    canonicalCreates.Add(entityItem.DeepClone());
                    continue;
                }

                if (!entitiesById.TryGetValue(entityId, out var existingEntity))
                    throw PathError(taskKey, $"$.entities[{index}].entityId", $"references missing entity '{entityId}'");

                var update = GetOrCreateUpdate(entityId, canonicalUpdates, updatesByEntityId);
                CopyRecognizedProperties(entityItem, update, EntityUpdateProperties);
                if (entityItem["fields"] is JsonObject fieldLabels)
                {
                    var updatedFields = GetOrCreateArray(update, "updatedFields");
                    foreach (var pair in fieldLabels)
                    {
                        var field = existingEntity.Fields.FirstOrDefault(item =>
                            string.Equals(item.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
                        if (field == null)
                            throw PathError(taskKey, $"$.entities[{index}].fields.{pair.Key}",
                                $"references missing field '{existingEntity.Name}.{pair.Key}'");
                        var description = pair.Value?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(description))
                            throw PathError(taskKey, $"$.entities[{index}].fields.{pair.Key}", "requires a non-empty display name");
                        MergeFieldUpdate(updatedFields, new JsonObject
                        {
                            ["fieldId"] = field.Id,
                            ["description"] = description
                        });
                    }
                }
                else if (entityItem["fields"] is JsonArray fieldUpdates)
                {
                    var updatedFields = GetOrCreateArray(update, "updatedFields");
                    foreach (var (fieldNode, fieldIndex) in fieldUpdates.Select((value, itemIndex) => (value, itemIndex)))
                    {
                        if (fieldNode is not JsonObject fieldObject)
                            throw PathError(taskKey, $"$.entities[{index}].fields[{fieldIndex}]", "must be an object");
                        var fieldId = ReadLong(fieldObject, "fieldId");
                        var fieldName = ReadString(fieldObject, "name");
                        var field = fieldId > 0
                            ? existingEntity.Fields.FirstOrDefault(item => item.Id == fieldId)
                            : existingEntity.Fields.FirstOrDefault(item => string.Equals(item.Name, fieldName, StringComparison.OrdinalIgnoreCase));
                        if (field == null)
                            throw PathError(taskKey, $"$.entities[{index}].fields[{fieldIndex}]", "does not identify an existing field");
                        var normalized = fieldObject.DeepClone().AsObject();
                        normalized["fieldId"] = field.Id;
                        // Same rule as a control binding: a control the stored type cannot carry is
                        // dropped instead of being written and then rejected by field-control
                        // validation, which would fail the stage for a cosmetic mistake.
                        if (normalized["formControlType"] is JsonNode requestedControl &&
                            !FieldControlCompatibility.Accepts(requestedControl.GetValue<string>(), field.DataType))
                        {
                            normalized.Remove("formControlType");
                        }

                        MergeFieldUpdate(updatedFields, normalized);
                    }
                }
            }
        }
        root["entities"] = canonicalCreates;
        root["entityUpdates"] = canonicalUpdates;

        if (root["controlBindings"] is JsonArray controlBindings)
        {
            for (var index = 0; index < controlBindings.Count; index++)
            {
                if (controlBindings[index] is not JsonObject binding)
                    throw PathError(taskKey, $"$.controlBindings[{index}]", "must be an object");
                var entityName = ReadString(binding, "entity");
                var fieldName = ReadString(binding, "field");
                if (string.IsNullOrWhiteSpace(entityName) || !entitiesByName.TryGetValue(entityName, out var entity))
                    throw PathError(taskKey, $"$.controlBindings[{index}].entity", $"references missing entity '{entityName}'");
                var field = entity.Fields.FirstOrDefault(item => string.Equals(item.Name, fieldName, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    throw PathError(taskKey, $"$.controlBindings[{index}].field", $"references missing field '{entity.Name}.{fieldName}'");

                var fieldUpdate = binding.DeepClone().AsObject();
                fieldUpdate.Remove("entity");
                fieldUpdate.Remove("field");
                fieldUpdate["fieldId"] = field.Id;

                // A binding may ask for a control the stored type cannot carry — "number" on a text
                // column, "switch" on a decimal. Dropping just that property keeps the field's own
                // control, which is what its type already implies. The alternative is worse than a
                // missed cosmetic tweak: the pair survives the write, field-control validation
                // rejects it, and the failed stage skips every task queued behind it.
                if (fieldUpdate["formControlType"] is JsonNode requestedControl &&
                    !FieldControlCompatibility.Accepts(requestedControl.GetValue<string>(), field.DataType))
                {
                    fieldUpdate.Remove("formControlType");
                }

                var update = GetOrCreateUpdate(entity.Id, canonicalUpdates, updatesByEntityId);
                MergeFieldUpdate(GetOrCreateArray(update, "updatedFields"), fieldUpdate);
            }
            root.Remove("controlBindings");
        }

        if (root["relations"] is JsonArray relations)
        {
            for (var index = 0; index < relations.Count; index++)
            {
                if (relations[index] is not JsonObject relation)
                    throw PathError(taskKey, $"$.relations[{index}]", "must be an object");
                var sourceName = ReadString(relation, "sourceEntity");
                if (string.IsNullOrWhiteSpace(sourceName) || !entitiesByName.TryGetValue(sourceName, out var sourceEntity))
                    throw PathError(taskKey, $"$.relations[{index}].sourceEntity", $"references missing entity '{sourceName}'");

                var normalized = relation.DeepClone().AsObject();
                normalized.Remove("sourceEntity");
                if (normalized["targetEntityName"] == null && normalized["targetEntity"] != null)
                    normalized["targetEntityName"] = normalized["targetEntity"]!.DeepClone();
                normalized.Remove("targetEntity");
                var update = GetOrCreateUpdate(sourceEntity.Id, canonicalUpdates, updatesByEntityId);
                GetOrCreateArray(update, "newRelations").Add(normalized);
            }
            root.Remove("relations");
        }

        NormalizeLegacyRelationCompatibility(canonicalUpdates, entitiesById);

        if (root["dictionaries"] is JsonArray dictionaries)
        {
            foreach (var dictionary in dictionaries.OfType<JsonObject>())
            {
                dictionary["summary"] ??= root["summary"]?.DeepClone();
                dictionary["tenantId"] ??= 0;
            }
        }

        try
        {
            return root.Deserialize<ProjectChangeSetProposal>(JsonOptions)
                ?? throw new InvalidOperationException($"Task '{taskKey}' has an invalid project change-set definition.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Task '{taskKey}' project change-set JSON does not match the executable contract at {ex.Path ?? "$"}: {ex.Message}",
                ex);
        }
    }

    private static JsonNode? ToCamelCase(JsonNode? node)
    {
        if (node is JsonObject source)
        {
            var target = new JsonObject();
            foreach (var property in source)
            {
                var key = JsonNamingPolicy.CamelCase.ConvertName(property.Key);
                target[key] = ToCamelCase(property.Value);
            }
            return target;
        }

        if (node is JsonArray array)
        {
            var target = new JsonArray();
            foreach (var item in array)
                target.Add(ToCamelCase(item));
            return target;
        }

        return node?.DeepClone();
    }

    private static void NormalizeAliases(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            CopyAlias(obj, "entityName", "name");
            CopyAlias(obj, "fieldName", "name");
            CopyAlias(obj, "name", "moduleName");
            CopyAlias(obj, "description", "moduleDescription");
            CopyAlias(obj, "moduleDisplayName", "moduleDescription");
            CopyAlias(obj, "displayName", "moduleDescription");
            foreach (var property in obj.Select(item => item.Value).ToList())
                NormalizeAliases(property);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                NormalizeAliases(item);
        }
    }

    private static void CopyAlias(JsonObject obj, string alias, string canonical)
    {
        if (obj[canonical] == null && obj[alias] != null)
            obj[canonical] = obj[alias]!.DeepClone();
    }

    private static JsonArray ReadObjectArray(JsonObject root, string propertyName)
    {
        if (root[propertyName] == null)
            return new JsonArray();
        if (root[propertyName] is not JsonArray array)
            throw new InvalidOperationException($"Project change-set path $.{propertyName} must be an array.");
        return array.DeepClone().AsArray();
    }

    private static void NormalizeLegacyRelationCompatibility(
        JsonArray updates,
        IReadOnlyDictionary<long, AiEntityBlueprintDto> entitiesById)
    {
        foreach (var update in updates.OfType<JsonObject>())
        {
            var entityId = ReadLong(update, "entityId");
            if (entityId <= 0 || !entitiesById.TryGetValue(entityId, out var entity))
                continue;

            if (entity.LegacyOneToManyRelations.Count == 0)
                continue;

            var retainedLegacyRelationIds = RemoveDuplicateLegacyRelationCreates(update, entity);
            if (retainedLegacyRelationIds.Count > 0)
                RemoveLegacyRelationIds(update, "deletedRelationIds", retainedLegacyRelationIds);
        }
    }

    private static void RemoveLegacyRelationIds(
        JsonObject update,
        string propertyName,
        IReadOnlySet<long> legacyRelationIds)
    {
        if (update[propertyName] is not JsonArray ids)
            return;

        for (var index = ids.Count - 1; index >= 0; index--)
        {
            if (TryReadLong(ids[index], out var id) && legacyRelationIds.Contains(id))
                ids.RemoveAt(index);
        }
    }

    private static HashSet<long> RemoveDuplicateLegacyRelationCreates(
        JsonObject update,
        AiEntityBlueprintDto entity)
    {
        var retainedLegacyRelationIds = new HashSet<long>();
        if (update["newRelations"] is not JsonArray relations)
            return retainedLegacyRelationIds;

        for (var index = relations.Count - 1; index >= 0; index--)
        {
            if (relations[index] is not JsonObject relation)
                continue;

            var candidate = relation.Deserialize<CreateEntityRelationProposal>(JsonOptions);
            var legacy = candidate == null
                ? null
                : entity.LegacyOneToManyRelations.FirstOrDefault(item =>
                    CodeMasterProjectChangeSetRules.IsEquivalentLegacyRelation(candidate, item));
            if (legacy == null)
                continue;

            retainedLegacyRelationIds.Add(legacy.Id);
            relations.RemoveAt(index);
        }
        return retainedLegacyRelationIds;
    }

    private static JsonObject GetOrCreateUpdate(
        long entityId,
        JsonArray updates,
        IDictionary<long, JsonObject> updatesByEntityId)
    {
        if (updatesByEntityId.TryGetValue(entityId, out var update))
            return update;
        update = new JsonObject { ["entityId"] = entityId };
        updates.Add(update);
        updatesByEntityId[entityId] = update;
        return update;
    }

    private static JsonArray GetOrCreateArray(JsonObject owner, string propertyName)
    {
        if (owner[propertyName] is JsonArray existing)
            return existing;
        var array = new JsonArray();
        owner[propertyName] = array;
        return array;
    }

    private static void MergeFieldUpdate(JsonArray updates, JsonObject incoming)
    {
        var fieldId = ReadLong(incoming, "fieldId");
        var existing = updates.OfType<JsonObject>().FirstOrDefault(item => ReadLong(item, "fieldId") == fieldId);
        if (existing == null)
        {
            updates.Add(incoming);
            return;
        }
        foreach (var pair in incoming)
        {
            if (!string.Equals(pair.Key, "fieldId", StringComparison.OrdinalIgnoreCase))
                existing[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static void CopyRecognizedProperties(JsonObject source, JsonObject target, IReadOnlySet<string> recognized)
    {
        foreach (var pair in source)
        {
            if (recognized.Contains(pair.Key))
                target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private static string? ReadString(JsonObject value, string propertyName)
    {
        var node = FindProperty(value, propertyName);
        return node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var result) ? result : null;
    }

    private static long ReadLong(JsonObject value, string propertyName)
    {
        var node = FindProperty(value, propertyName);
        if (node is not JsonValue jsonValue)
            return 0;
        if (jsonValue.TryGetValue<long>(out var number))
            return number;
        return jsonValue.TryGetValue<string>(out var text) && long.TryParse(text, out number) ? number : 0;
    }

    private static bool TryReadLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue<long>(out value))
            return true;
        return jsonValue.TryGetValue<string>(out var text) && long.TryParse(text, out value);
    }

    private static JsonNode? FindProperty(JsonObject value, string propertyName)
        => value.FirstOrDefault(pair => string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase)).Value;

    private static InvalidOperationException PathError(string taskKey, string path, string message)
        => new($"Task '{taskKey}' project change-set path {path} {message}.");
}
