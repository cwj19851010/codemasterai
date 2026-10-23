using System.Text.Json;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Domain.Entities.CodeGen;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class ComplexPageSynchronizationService : IComplexPageSynchronizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ISqlSugarClient _db;
    private readonly IProjectPageService _pageService;

    public ComplexPageSynchronizationService(ISqlSugarClient db, IProjectPageService pageService)
    {
        _db = db;
        _pageService = pageService;
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ComplexPageSyncPreviewDto> PreviewAsync(long pageId)
    {
        var page = await _pageService.GetByIdAsync(pageId)
            ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
        var preview = new ComplexPageSyncPreviewDto { PageId = page.Id, PageKey = page.PageKey };
        foreach (var dependency in page.Dependencies)
        {
            var previous = ReadSnapshot(dependency.SummaryJson);
            var current = await _db.Queryable<EntityField>()
                .ClearFilter()
                .Where(item => item.ModuleEntityId == dependency.EntityId)
                .ToListAsync();
            var currentById = current.ToDictionary(item => item.Id);
            var previousById = previous.Fields.ToDictionary(item => item.Id);

            foreach (var field in current.Where(item => !item.IsDeleted && !previousById.ContainsKey(item.Id)))
                preview.Changes.Add(BuildChange(page, dependency.EntityId, field.Id, "Added", null, field));
            foreach (var field in previous.Fields.Where(item => !currentById.TryGetValue(item.Id, out var currentField) || currentField.IsDeleted))
                preview.Changes.Add(BuildChange(page, dependency.EntityId, field.Id, "Deleted", field, null));
            foreach (var field in current.Where(item => !item.IsDeleted && previousById.ContainsKey(item.Id)))
            {
                var old = previousById[field.Id];
                if (!string.Equals(old.Name, field.Name, StringComparison.Ordinal))
                    preview.Changes.Add(BuildChange(page, dependency.EntityId, field.Id, "Renamed", old, field));
                if (!string.Equals(old.DataType, field.DataType, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(old.FormControlType, field.FormControlType, StringComparison.OrdinalIgnoreCase))
                    preview.Changes.Add(BuildChange(page, dependency.EntityId, field.Id, "TypeOrControlChanged", old, field));
            }

            var currentHash = await _pageService.ComputeEntitySchemaHashAsync(dependency.EntityId);
            if (!string.Equals(currentHash, dependency.SchemaHash, StringComparison.OrdinalIgnoreCase) &&
                preview.Changes.All(item => item.EntityId != dependency.EntityId))
            {
                preview.Changes.Add(new ComplexPageFieldChangeDto
                {
                    EntityId = dependency.EntityId,
                    ChangeType = "SchemaChanged"
                });
            }
        }
        preview.RequiresSync = preview.Changes.Count > 0;
        return preview;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ComplexPageSyncResultDto> SynchronizeAsync(long pageId)
    {
        var result = new ComplexPageSyncResultDto { PageId = pageId };
        try
        {
            var page = await _pageService.GetByIdAsync(pageId)
                ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
            var preview = await PreviewAsync(pageId);
            result.Changes = preview.Changes;
            var layout = PageLayoutTree.Parse(page.LayoutJson);
            var sourceById = page.DataSources.ToDictionary(item => item.Id);
            var bindings = page.Bindings.Select(binding => ToCreateBinding(binding, sourceById[binding.DataSourceId].SourceKey)).ToList();
            var entityIds = page.DataSources.Where(item => item.EntityId.HasValue).Select(item => item.EntityId!.Value).Distinct().ToList();
            var currentFields = entityIds.Count == 0
                ? new List<EntityField>()
                : await _db.Queryable<EntityField>().ClearFilter()
                    .Where(item => entityIds.Contains(item.ModuleEntityId))
                    .OrderBy(item => item.OrderNum)
                    .ToListAsync();
            var currentById = currentFields.ToDictionary(item => item.Id);

            foreach (var binding in bindings.Where(item => item.FieldId.HasValue))
            {
                if (!currentById.TryGetValue(binding.FieldId!.Value, out var field) || field.IsDeleted)
                {
                    binding.BindingStatus = PageBindingStatus.Invalid;
                    result.InvalidBindings++;
                    continue;
                }
                var changed = preview.Changes.Any(item => item.FieldId == field.Id && item.ChangeType == "TypeOrControlChanged");
                if (changed && !string.IsNullOrWhiteSpace(binding.ControlType) &&
                    !string.Equals(binding.ControlType, field.FormControlType, StringComparison.OrdinalIgnoreCase))
                {
                    binding.BindingStatus = PageBindingStatus.NeedsReview;
                    result.ReviewBindings++;
                }
                else
                {
                    binding.BindingStatus = PageBindingStatus.Active;
                }
                binding.LastKnownPath = field.Name;
            }

            foreach (var container in bindings.Where(item =>
                         item.AutoIncludeNewFields && item.SyncMode == PageSyncMode.Auto &&
                         item.BindingRole is PageBindingRole.Table or PageBindingRole.Form or PageBindingRole.Detail).ToList())
            {
                var source = sourceById[page.Bindings.First(item => item.ComponentId == container.ComponentId).DataSourceId];
                if (source.SyncMode != PageSyncMode.Auto || !source.EntityId.HasValue) continue;
                var childIds = layout.Require(container.ComponentId).Children.Select(item => item.ComponentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var alreadyBound = bindings.Where(item => childIds.Contains(item.ComponentId) && item.FieldId.HasValue)
                    .Select(item => item.FieldId!.Value).ToHashSet();
                foreach (var field in currentFields.Where(item => item.ModuleEntityId == source.EntityId && !item.IsDeleted && !alreadyBound.Contains(item.Id)))
                {
                    var componentId = $"{container.ComponentId}_{ToCamelCase(field.Name)}_{field.Id}";
                    var nodeType = container.BindingRole switch
                    {
                        PageBindingRole.Table => "table-column",
                        PageBindingRole.Form => "form-field",
                        _ => "detail-field"
                    };
                    var role = container.BindingRole switch
                    {
                        PageBindingRole.Table => PageBindingRole.Column,
                        PageBindingRole.Form => PageBindingRole.Field,
                        _ => PageBindingRole.Detail
                    };
                    layout.AddComponent(container.ComponentId, nodeType, componentId);
                    var bindingId = $"binding_{componentId}";
                    layout.BindDataSource(componentId, source.Id, bindingId);
                    layout.BindField(componentId, field.Id, bindingId);
                    bindings.Add(new CreatePageComponentBindingDto
                    {
                        ComponentId = componentId,
                        BindingId = bindingId,
                        GeneratedGenId = $"gen_binding_{bindingId}",
                        DataSourceKey = source.SourceKey,
                        FieldId = field.Id,
                        BindingRole = role,
                        BindingStatus = PageBindingStatus.Active,
                        SyncMode = PageSyncMode.Auto,
                        LastKnownPath = field.Name
                    });
                    alreadyBound.Add(field.Id);
                    result.AddedBindings++;
                }
            }

            var updated = await _pageService.SaveDefinitionAsync(page.Id, new UpdateProjectPageDto
            {
                ProjectId = page.ProjectId,
                ModuleId = page.ModuleId,
                PageKey = page.PageKey,
                PageName = page.PageName,
                Description = page.Description,
                RoutePath = page.RoutePath,
                Icon = page.Icon,
                OrderNum = page.OrderNum,
                PageType = page.PageType,
                LayoutJson = layout.ToJson(),
                SyncMode = page.SyncMode,
                IsMenuSynced = page.IsMenuSynced,
                DefinitionVersion = page.DefinitionVersion,
                DataSources = page.DataSources.Select(ToCreateDataSource).ToList(),
                Bindings = bindings
            });
            result.Success = true;
            result.DefinitionVersion = updated.DefinitionVersion;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    [Permission("codegen:projectpage:update")]
    public async Task<List<ComplexPageSyncResultDto>> SynchronizeProjectAsync(long projectId)
    {
        var pageIds = await _db.Queryable<ProjectPage>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted && item.SyncStatus == PageSyncStatus.NeedsSync)
            .OrderBy(item => item.OrderNum)
            .Select(item => item.Id)
            .ToListAsync();
        var results = new List<ComplexPageSyncResultDto>(pageIds.Count);
        foreach (var pageId in pageIds)
            results.Add(await SynchronizeAsync(pageId));
        return results;
    }

    private static ComplexPageFieldChangeDto BuildChange(
        ProjectPageDto page,
        long entityId,
        long fieldId,
        string type,
        FieldSnapshot? previous,
        EntityField? current) => new()
    {
        EntityId = entityId,
        FieldId = fieldId,
        ChangeType = type,
        PreviousName = previous?.Name,
        CurrentName = current?.Name,
        PreviousDataType = previous?.DataType,
        CurrentDataType = current?.DataType,
        BindingIds = page.Bindings.Where(item => item.FieldId == fieldId).Select(item => item.BindingId).ToList()
    };

    private static DependencySnapshot ReadSnapshot(string json)
    {
        try { return JsonSerializer.Deserialize<DependencySnapshot>(json, JsonOptions) ?? new DependencySnapshot(); }
        catch (JsonException) { return new DependencySnapshot(); }
    }

    private static CreatePageDataSourceDto ToCreateDataSource(PageDataSourceDto source) => new()
    {
        SourceKey = source.SourceKey,
        SourceType = source.SourceType,
        ProjectDataSourceId = source.ProjectDataSourceId,
        EntityId = source.EntityId,
        ServiceName = source.ServiceName,
        MethodName = source.MethodName,
        DtoName = source.DtoName,
        DataShape = source.DataShape,
        SyncMode = source.SyncMode,
        ParamsJson = source.ParamsJson,
        OrderNum = source.OrderNum,
        IsEnabled = source.IsEnabled
    };

    private static CreatePageComponentBindingDto ToCreateBinding(PageComponentBindingDto binding, string sourceKey) => new()
    {
        ComponentId = binding.ComponentId,
        BindingId = binding.BindingId,
        GeneratedGenId = binding.GeneratedGenId,
        DataSourceKey = sourceKey,
        FieldId = binding.FieldId,
        BindingRole = binding.BindingRole,
        BindingStatus = binding.BindingStatus,
        SyncMode = binding.SyncMode,
        AutoIncludeNewFields = binding.AutoIncludeNewFields,
        ControlType = binding.ControlType,
        PropsJson = binding.PropsJson,
        LabelOverride = binding.LabelOverride,
        Origin = binding.Origin,
        LastKnownPath = binding.LastKnownPath
    };

    private static string ToCamelCase(string value)
        => string.IsNullOrWhiteSpace(value) || char.IsLower(value[0]) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private sealed class DependencySnapshot
    {
        public List<FieldSnapshot> Fields { get; set; } = new();
    }

    private sealed class FieldSnapshot
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? DataType { get; set; }
        public string? FormControlType { get; set; }
    }
}
