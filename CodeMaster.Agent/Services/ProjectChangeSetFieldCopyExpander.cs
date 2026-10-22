using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

public static class ProjectChangeSetFieldCopyExpander
{
    public static async Task ExpandAsync(
        ProjectChangeSetProposal proposal,
        Func<EntityBlueprintQuery, Task<AiEntityBlueprintDto>> loadEntityAsync)
    {
        var cache = new Dictionary<string, AiEntityBlueprintDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in proposal.Entities)
        {
            var sourceId = entity.CopyFieldsFromEntityId;
            var sourceName = NormalizeOptional(entity.CopyFieldsFromEntityName);
            if (!sourceId.HasValue && sourceName == null)
                continue;
            if (sourceId.HasValue && sourceName != null)
            {
                throw new ArgumentException(
                    $"Entity '{entity.Name}' must set only one copied-field source: id or name.");
            }

            var key = sourceId.HasValue ? $"id:{sourceId.Value}" : $"name:{sourceName}";
            if (!cache.TryGetValue(key, out var source))
            {
                source = await loadEntityAsync(new EntityBlueprintQuery
                {
                    EntityId = sourceId ?? 0,
                    EntityName = sourceName
                });
                cache[key] = source;
            }

            var copiedFields = source.Fields
                .Where(field => !field.IsSystemField && !field.IsPrimaryKey)
                .Select(MapField)
                .ToList();
            if (copiedFields.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Entity '{source.Name}' has no business fields that can be copied.");
            }
            if (copiedFields.Any(IsAggregateField))
            {
                throw new InvalidOperationException(
                    $"Entity '{source.Name}' contains aggregate fields tied to an existing relation. " +
                    "Create those aggregate fields explicitly after creating the new relation.");
            }

            MergeExplicitFields(copiedFields, entity.Fields);
            entity.Fields = copiedFields;
            entity.CopyFieldsFromEntityId = null;
            entity.CopyFieldsFromEntityName = null;
        }
    }

    private static void MergeExplicitFields(
        List<CreateEntityFieldProposal> copiedFields,
        IReadOnlyCollection<CreateEntityFieldProposal> explicitFields)
    {
        foreach (var field in explicitFields)
        {
            var existingIndex = copiedFields.FindIndex(item =>
                string.Equals(item.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                copiedFields[existingIndex] = field;
            else
                copiedFields.Add(field);
        }
    }

    private static bool IsAggregateField(CreateEntityFieldProposal field) =>
        string.Equals(field.FieldCategory, "Aggregate", StringComparison.OrdinalIgnoreCase);

    private static CreateEntityFieldProposal MapField(AiFieldBlueprintDto field)
    {
        return new CreateEntityFieldProposal
        {
            Name = field.Name,
            Description = field.Description,
            BusinessDescription = field.BusinessDescription,
            DataType = field.DataType,
            IsNullable = field.IsNullable,
            MaxLength = field.MaxLength,
            Precision = field.Precision,
            Scale = field.Scale,
            DefaultValue = field.DefaultValue,
            IsIgnore = field.IsIgnore,
            IsPrimaryKey = false,
            IsRequired = field.IsRequired,
            MinValue = field.MinValue,
            MaxValue = field.MaxValue,
            RegexPattern = field.RegexPattern,
            IsEmail = field.IsEmail,
            IsPhone = field.IsPhone,
            ShowInList = field.ShowInList,
            ShowInDetail = field.ShowInDetail,
            ShowInAddForm = field.ShowInAddForm,
            ShowInEditForm = field.ShowInEditForm,
            ShowInSearch = field.ShowInSearch,
            FormControlType = field.FormControlType,
            ListWidth = field.ListWidth,
            OrderNum = field.OrderNum,
            SelectDataSource = field.SelectDataSource,
            SelectOptions = field.SelectOptions,
            IsMultiple = field.IsMultiple,
            RelatedEntityName = field.RelatedEntityName,
            RelatedEntityIdField = field.RelatedEntityIdField,
            RelatedEntityDisplayFields = field.RelatedEntityDisplayFields,
            ResultMappings = field.ResultMappings,
            FieldCategory = string.IsNullOrWhiteSpace(field.FieldCategory) ? "Normal" : field.FieldCategory,
            Formula = field.Formula,
            AggregateType = field.AggregateType,
            AggregateChildEntityId = field.AggregateChildEntityId,
            AggregateChildFieldName = field.AggregateChildFieldName,
            AggregateSeparator = field.AggregateSeparator,
            Remark = field.Remark
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
