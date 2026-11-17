using System.Text.Json;
using System.Text.Json.Nodes;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Application.Services.CodeGen;

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

    // Base-entity columns are text but are never a meaningful label in a picker.
    private static readonly HashSet<string> SystemFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id", "TenantId", "CreateUserId", "CreateBy", "CreateTime",
        "UpdateUserId", "UpdateBy", "UpdateTime", "IsDeleted",
        "DeleteUserId", "DeleteBy", "DeleteTime"
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
                {
                    // A binding whose field no longer exists has nothing to configure, so it is dropped
                    // instead of failing the stage. Control bindings are cosmetic metadata: they are
                    // generated from the plan's declared field list, but the field may legitimately be
                    // gone by the time this task runs — a read-only view entity, for instance, keeps
                    // only the columns its SELECT actually returns, and the compiler has no way to know
                    // that when the plan is written. Throwing here made the whole stage fail and skipped
                    // every task queued behind it, so a single stale binding blocked the entire run.
                    continue;
                }

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

                // A binding compiled from the declared field list can name a label column the target
                // entity never ended up with. The rules reject the whole change set for that single
                // cosmetic mistake, which fails the stage and skips every task behind it. Repair it
                // here, where the blueprint still has the real target columns.
                if (!TryRepairRelatedControlFields(fieldUpdate, entitiesByName))
                    continue;

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

    /// <summary>
    /// Keeps a related control's value/label columns pointing at columns that actually exist on the
    /// target entity.
    /// <para>
    /// The field-control stage of a plan is compiled from the plan's <em>declared</em> field list.
    /// The target entity may have been created with a different list (a column was dropped, renamed,
    /// or never emitted by the model), so a binding such as
    /// <c>ReceiptItemId -&gt; MaterialReceiptItem, display ["Name"]</c> can reference a label that
    /// is simply not there. <c>ValidateRelatedControl</c> then rejects the entire change set with
    /// "references missing field(s) Name on related entity 'X'", the stage fails and every task
    /// queued behind it is skipped — which in practice forced two full plan regenerations.
    /// </para>
    /// <para>
    /// This is cosmetic metadata, so it is repaired rather than refused: drop only the labels that
    /// do not exist, substitute a real text column when none survive, and fall all the way back to
    /// the key column. The picker is preserved (an FK must not degrade to a plain number input).
    /// Returns false only when the target has no usable column at all, meaning the binding has
    /// nothing it can display and should be dropped like any other stale binding.
    /// </para>
    /// </summary>
    private static bool TryRepairRelatedControlFields(
        JsonObject fieldUpdate,
        IReadOnlyDictionary<string, AiEntityBlueprintDto> entitiesByName)
    {
        var relatedName = ReadString(fieldUpdate, "relatedEntityName")?.Trim();
        if (string.IsNullOrWhiteSpace(relatedName))
            return true;

        List<(string Name, string DataType)> targetFields;
        bool hasPrimaryKey;
        if (SystemReferenceEntityCatalog.TryGet(relatedName, out var builtIn))
        {
            targetFields = builtIn.Fields.Select(item => (item.Name, item.DataType)).ToList();
            hasPrimaryKey = builtIn.Fields.Any(item => item.IsPrimaryKey);
        }
        else if (entitiesByName.TryGetValue(relatedName, out var target))
        {
            targetFields = target.Fields.Select(item => (item.Name, item.DataType)).ToList();
            hasPrimaryKey = target.HasPrimaryKey;
        }
        else
        {
            // The target itself is unresolved; DeferUnresolvedRelatedTargets owns that repair.
            return true;
        }

        var available = targetFields.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hasPrimaryKey)
            available.Add("Id");
        if (available.Count == 0)
            return false;

        var valueField = ReadString(fieldUpdate, "relatedEntityIdField");
        // Only rewrite the value column when the right answer is provable. Picking an arbitrary
        // column as a key would silently corrupt the picker, so an unprovable case is left alone.
        if (!string.IsNullOrWhiteSpace(valueField) &&
            !available.Contains(valueField.Trim()) &&
            available.Contains("Id"))
        {
            SetString(fieldUpdate, "relatedEntityIdField", "Id");
        }

        var displayFields = ReadDisplayFields(FindProperty(fieldUpdate, "relatedEntityDisplayFields"));
        var kept = displayFields
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (kept.Count > 0 && kept.Count == displayFields.Count)
            return true;

        if (kept.Count == 0)
            kept.Add(PickDisplayField(targetFields, available) ?? available.First());
        SetString(fieldUpdate, "relatedEntityDisplayFields", JsonSerializer.Serialize(kept, JsonOptions));
        return true;
    }

    private static string? PickDisplayField(
        IReadOnlyList<(string Name, string DataType)> targetFields,
        IReadOnlySet<string> available)
    {
        var textFields = targetFields
            .Where(item => (item.DataType ?? string.Empty).StartsWith("string", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Name)
            .Where(name => !SystemFieldNames.Contains(name))
            .ToList();

        return textFields.FirstOrDefault(name => name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            ?? textFields.FirstOrDefault(name => name.EndsWith("Name", StringComparison.OrdinalIgnoreCase))
            ?? textFields.FirstOrDefault(name => name.EndsWith("Title", StringComparison.OrdinalIgnoreCase))
            ?? textFields.FirstOrDefault(name => name.EndsWith("Code", StringComparison.OrdinalIgnoreCase))
            ?? textFields.FirstOrDefault()
            ?? (available.Contains("Id") ? "Id" : null);
    }

    private static List<string> ReadDisplayFields(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array
                .Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text?.Trim() ?? string.Empty : string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToList();
        }
        if (node is not JsonValue scalar || !scalar.TryGetValue<string>(out var raw) || string.IsNullOrWhiteSpace(raw))
            return new List<string>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw, JsonOptions);
            if (parsed is { Count: > 0 })
            {
                return parsed
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => text.Trim())
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // A comma-separated list is accepted too; handled below.
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static void SetString(JsonObject owner, string propertyName, string value)
    {
        var existingKey = owner
            .Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (existingKey != null)
            owner.Remove(existingKey);
        owner[propertyName] = JsonValue.Create(value);
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
