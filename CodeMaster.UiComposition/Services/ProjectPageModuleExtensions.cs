using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using SqlSugar;
using System.Text.Json;

namespace CodeMaster.Application.Services.CodeGen;

public static class ProjectPageModuleContract
{
    public const string Key = "ui-composition";
    public const int Version = 2;
}

public sealed class ProjectPageGenerationBundleContributor : IGenerationBundleContributor
{
    private readonly ISqlSugarClient _db;

    public ProjectPageGenerationBundleContributor(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task ContributeAsync(long projectId, GenerationBundleDto bundle)
    {
        var pages = new List<ProjectPage>();
        var dataSources = new List<PageDataSource>();
        var componentBindings = new List<PageComponentBinding>();
        var entityDependencies = new List<PageEntityDependency>();
        var layoutPresets = new List<PageLayoutPreset>();
        var displayTemplates = new List<PageDisplayTemplate>();
        var projectDataSources = new List<ProjectDataSource>();
        var dataSourceBindings = new List<PageDataSourceBinding>();

        if (_db.DbMaintenance.IsAnyTable("sys_project_page", false))
        {
            pages = await _db.Queryable<ProjectPage>()
                .Where(page => page.ProjectId == projectId && !page.IsDeleted)
                .OrderBy(page => page.OrderNum)
                .ToListAsync();
            var pageIds = pages.Select(page => page.Id).ToList();
            if (pageIds.Count > 0)
            {
                if (_db.DbMaintenance.IsAnyTable("sys_page_data_source", false))
                {
                    dataSources = await _db.Queryable<PageDataSource>()
                        .Where(source => pageIds.Contains(source.PageId) && !source.IsDeleted)
                        .OrderBy(source => source.OrderNum)
                        .ToListAsync();
                }
                if (_db.DbMaintenance.IsAnyTable("sys_page_component_binding", false))
                {
                    componentBindings = await _db.Queryable<PageComponentBinding>()
                        .Where(binding => pageIds.Contains(binding.PageId) && !binding.IsDeleted)
                        .OrderBy(binding => binding.Id)
                        .ToListAsync();
                }
                if (_db.DbMaintenance.IsAnyTable("sys_page_entity_dependency", false))
                {
                    entityDependencies = await _db.Queryable<PageEntityDependency>()
                        .Where(dependency => pageIds.Contains(dependency.PageId) && !dependency.IsDeleted)
                        .OrderBy(dependency => dependency.Id)
                        .ToListAsync();
                }
            }
        }

        if (_db.DbMaintenance.IsAnyTable("sys_page_layout_preset", false))
        {
            layoutPresets = await _db.Queryable<PageLayoutPreset>()
                .Where(preset => !preset.IsDeleted)
                .OrderBy(preset => preset.Sort)
                .ToListAsync();
        }

        if (_db.DbMaintenance.IsAnyTable("sys_page_display_template", false))
        {
            displayTemplates = await _db.Queryable<PageDisplayTemplate>()
                .Where(template => !template.IsDeleted && template.IsEnabled)
                .OrderBy(template => template.Sort)
                .ToListAsync();
        }

        if (_db.DbMaintenance.IsAnyTable("sys_project_data_source", false))
        {
            projectDataSources = await _db.Queryable<ProjectDataSource>()
                .Where(source => source.ProjectId == projectId && !source.IsDeleted)
                .OrderBy(source => source.OrderNum)
                .ToListAsync();
        }
        if (_db.DbMaintenance.IsAnyTable("sys_page_data_source_binding", false))
        {
            var sourceIds = projectDataSources.Select(source => source.Id).ToList();
            if (sourceIds.Count > 0)
            {
                dataSourceBindings = await _db.Queryable<PageDataSourceBinding>()
                    .Where(binding => sourceIds.Contains(binding.DataSourceId) && !binding.IsDeleted)
                    .OrderBy(binding => binding.Id)
                    .ToListAsync();
            }
        }

        bundle.ExtensionVersions[ProjectPageModuleContract.Key] = ProjectPageModuleContract.Version;
        bundle.Extensions[ProjectPageModuleContract.Key] = JsonSerializer.SerializeToElement(new ProjectPageBundleExtension
        {
            Pages = pages,
            DataSources = dataSources,
            ComponentBindings = componentBindings,
            EntityDependencies = entityDependencies,
            LayoutPresets = layoutPresets,
            DisplayTemplates = displayTemplates,
            ProjectDataSources = projectDataSources,
            DataSourceBindings = dataSourceBindings
        });
    }
}

public sealed class ProjectPageExecutionCompletionHandler : ILocalExecutionCompletionHandler
{
    private readonly ISqlSugarClient _db;

    public ProjectPageExecutionCompletionHandler(ISqlSugarClient db)
    {
        _db = db;
    }

    public bool CanHandle(string action) =>
        string.Equals(action, "generateComplexPage", StringComparison.OrdinalIgnoreCase);

    public async Task HandleAsync(LocalExecutionCompleteDto input, DateTime completedAt)
    {
        if (!input.Extensions.TryGetValue(ProjectPageModuleContract.Key, out var payload))
            throw new ArgumentException("UI composition completion metadata is required");
        var completion = payload.Deserialize<ProjectPageExecutionCompletion>()
            ?? throw new ArgumentException("UI composition completion metadata is invalid");
        if (completion.PageId == 0)
            throw new ArgumentException("PageId is required");

        var page = await _db.Queryable<ProjectPage>()
            .Where(item => item.Id == completion.PageId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException("Project page not found");
        if (input.ProjectId.HasValue && input.ProjectId.Value != page.ProjectId)
            throw new InvalidOperationException("Project page does not belong to the supplied project");

        page.SyncStatus = PageSyncStatus.Generated;
        page.LastGeneratedTime = completedAt;
        page.LastError = null;
        if (!string.IsNullOrWhiteSpace(completion.LastGeneratedVueHash))
            page.LastGeneratedVueHash = completion.LastGeneratedVueHash;
        await _db.Updateable(page).ExecuteCommandAsync();
    }
}

public sealed class ProjectPageExecutionCompletion
{
    public long PageId { get; set; }

    public string? LastGeneratedVueHash { get; set; }
}

public sealed class ProjectPageBundleExtension
{
    public List<ProjectPage> Pages { get; set; } = new();
    public List<PageDataSource> DataSources { get; set; } = new();
    public List<PageComponentBinding> ComponentBindings { get; set; } = new();
    public List<PageEntityDependency> EntityDependencies { get; set; } = new();
    public List<PageLayoutPreset> LayoutPresets { get; set; } = new();
    public List<PageDisplayTemplate> DisplayTemplates { get; set; } = new();
    public List<ProjectDataSource> ProjectDataSources { get; set; } = new();
    public List<PageDataSourceBinding> DataSourceBindings { get; set; } = new();
}
