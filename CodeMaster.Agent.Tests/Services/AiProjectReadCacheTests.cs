using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiProjectReadCacheTests
{
    [Fact]
    public async Task Shares_overview_project_catalog_and_entity_reads_within_one_turn()
    {
        var service = new CountingBlueprintService();
        var cache = new AiProjectReadCache(service);

        _ = await cache.GetOverviewAsync(10);
        _ = await cache.GetOverviewAsync(10);
        _ = await cache.GetCatalogAsync();
        _ = await cache.GetCatalogAsync();
        _ = await cache.GetProjectAsync(10);
        _ = await cache.GetProjectAsync(10);
        _ = await cache.GetEntityAsync(10, 20, null);
        _ = await cache.GetEntityAsync(10, 20, null);

        Assert.Equal(1, service.OverviewCalls);
        Assert.Equal(1, service.ProjectCalls);
        Assert.Equal(1, service.CatalogCalls);
        Assert.Equal(0, service.EntityCalls);
    }

    [Fact]
    public async Task Invalidating_after_metadata_write_forces_fresh_reads()
    {
        var service = new CountingBlueprintService();
        var cache = new AiProjectReadCache(service);

        _ = await cache.GetOverviewAsync(10);
        cache.InvalidateMetadata();
        _ = await cache.GetOverviewAsync(10);

        Assert.Equal(2, service.OverviewCalls);
    }

    private sealed class CountingBlueprintService : ICodeMasterProjectBlueprintService
    {
        public int OverviewCalls { get; private set; }
        public int ProjectCalls { get; private set; }
        public int CatalogCalls { get; private set; }
        public int EntityCalls { get; private set; }

        public Task<AiProjectOverviewDto> GetProjectOverviewAsync(long projectId)
        {
            OverviewCalls++;
            return Task.FromResult(new AiProjectOverviewDto());
        }

        public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId)
            => GetProjectAsync(projectId, null);

        public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId, AiCodegenCatalogDto? catalog)
        {
            ProjectCalls++;
            return Task.FromResult(new AiProjectBlueprintDto
            {
                Modules =
                [
                    new AiModuleBlueprintDto
                    {
                        Entities =
                        [
                            new AiEntityBlueprintDto { Id = 20, Name = "Project" }
                        ]
                    }
                ]
            });
        }

        public Task<AiCodegenCatalogDto> GetCatalogAsync()
        {
            CatalogCalls++;
            return Task.FromResult(new AiCodegenCatalogDto());
        }

        public Task<AiEntityBlueprintDto> GetEntityAsync(long projectId, long? entityId, string? entityName)
        {
            EntityCalls++;
            return Task.FromResult(new AiEntityBlueprintDto
            {
                Id = entityId ?? 0,
                Name = entityName ?? string.Empty
            });
        }

        public Task<AiUiPageBlueprintDto> GetUiPageAsync(
            long projectId,
            long? entityId,
            string? entityName,
            string pageType)
            => Task.FromResult(new AiUiPageBlueprintDto { EntityId = entityId ?? 0, PageType = pageType });
    }
}
