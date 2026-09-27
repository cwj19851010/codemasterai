using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Caches project blueprint reads for one Agent turn.
///
/// A single model turn commonly asks for the summary, completion status,
/// structure, catalog, and one or more entity blueprints. Those tools all read
/// the same CodeMaster metadata. Keeping the cache scoped to the tool factory
/// prevents duplicate database work during that turn without allowing stale
/// metadata to leak into a later request.
/// </summary>
public sealed class AiProjectReadCache
{
    private readonly ICodeMasterProjectBlueprintService _blueprintService;
    private Task<AiProjectOverviewDto>? _overviewTask;
    private Task<AiProjectBlueprintDto>? _projectTask;
    private Task<AiCodegenCatalogDto>? _catalogTask;
    private readonly Dictionary<string, Task<AiEntityBlueprintDto>> _entityTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<AiUiPageBlueprintDto>> _pageTasks = new(StringComparer.OrdinalIgnoreCase);

    public AiProjectReadCache(ICodeMasterProjectBlueprintService blueprintService)
    {
        _blueprintService = blueprintService;
    }

    public Task<AiProjectOverviewDto> GetOverviewAsync(long projectId)
        => _overviewTask ??= _blueprintService.GetProjectOverviewAsync(projectId);

    public async Task<AiProjectBlueprintDto> GetProjectAsync(long projectId)
    {
        if (_projectTask != null)
            return await _projectTask;

        // Pass an already cached catalog into the blueprint service so a
        // structure read after a catalog read does not query the same catalog
        // a second time internally.
        if (_catalogTask != null)
        {
            var catalog = await _catalogTask;
            _projectTask = _blueprintService.GetProjectAsync(projectId, catalog);
        }
        else
        {
            _projectTask = _blueprintService.GetProjectAsync(projectId);
        }

        return await _projectTask;
    }

    public async Task<AiCodegenCatalogDto> GetCatalogAsync()
    {
        if (_catalogTask != null)
            return await _catalogTask;

        // GetProjectAsync already loads the catalog as part of the complete
        // blueprint. Reuse that result rather than querying the catalog again.
        if (_projectTask != null)
        {
            var blueprint = await _projectTask;
            return new AiCodegenCatalogDto
            {
                Controls = blueprint.Controls,
                PageTemplates = blueprint.PageTemplates,
                ChildTemplates = blueprint.ChildTemplates
            };
        }

        _catalogTask = _blueprintService.GetCatalogAsync();
        return await _catalogTask;
    }

    public async Task<AiEntityBlueprintDto> GetEntityAsync(
        long projectId,
        long? entityId,
        string? entityName)
    {
        var key = BuildEntityKey(entityId, entityName);
        if (_entityTasks.TryGetValue(key, out var cached))
            return await cached;

        // If the complete project blueprint is already loaded, resolve the
        // entity from that snapshot instead of issuing another detail query.
        if (_projectTask != null)
        {
            var blueprint = await _projectTask;
            var entity = blueprint.Modules
                .SelectMany(module => module.Entities)
                .FirstOrDefault(candidate =>
                    entityId is > 0
                        ? candidate.Id == entityId.Value
                        : !string.IsNullOrWhiteSpace(entityName) &&
                          string.Equals(candidate.Name, entityName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (entity != null)
                return entity;
        }

        var task = _blueprintService.GetEntityAsync(projectId, entityId, entityName);
        _entityTasks[key] = task;
        return await task;
    }

    public Task<AiUiPageBlueprintDto> GetUiPageAsync(
        long projectId,
        long? entityId,
        string? entityName,
        string pageType)
    {
        var key = $"{entityId?.ToString() ?? string.Empty}|{entityName?.Trim() ?? string.Empty}|{pageType.Trim()}";
        if (_pageTasks.TryGetValue(key, out var cached))
            return cached;

        var task = _blueprintService.GetUiPageAsync(projectId, entityId, entityName, pageType);
        _pageTasks[key] = task;
        return task;
    }

    public void InvalidateMetadata()
    {
        _overviewTask = null;
        _projectTask = null;
        _catalogTask = null;
        _entityTasks.Clear();
        _pageTasks.Clear();
    }

    private static string BuildEntityKey(long? entityId, string? entityName)
        => entityId is > 0
            ? $"id:{entityId.Value}"
            : $"name:{entityName?.Trim() ?? string.Empty}";
}
