using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Migrator.Persistence.EfCore;
using Microsoft.EntityFrameworkCore;

namespace CodeMaster.UiComposition.Migrator.Persistence;

/// <summary>
/// Promotes page-local data-source definitions to project-level assets while
/// preserving the identifiers used by component bindings and dependencies.
/// The operation is intentionally repeatable: the page/source binding acts as
/// the durable checkpoint when a process is restarted after a partial run.
/// </summary>
internal static class UiCompositionLegacyDataMigrator
{
    public static async Task MigrateAsync(
        CodeMasterDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var legacySources = await dbContext.Set<PageDataSource>()
            .OrderBy(source => source.IsDeleted)
            .ThenBy(source => source.Id)
            .ToListAsync(cancellationToken);
        if (legacySources.Count == 0)
        {
            return;
        }

        var pages = await dbContext.Set<ProjectPage>().ToDictionaryAsync(
            page => page.Id,
            cancellationToken);
        var projectSources = await dbContext.Set<ProjectDataSource>().ToListAsync(cancellationToken);
        var pageBindings = await dbContext.Set<PageDataSourceBinding>().ToListAsync(cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var targetsByKey = projectSources
            .GroupBy(source => (source.ProjectId, source.SourceKey), EqualityComparer<(long, string)>.Default)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(source => source.IsDeleted).ThenBy(source => source.Id).First(),
                EqualityComparer<(long, string)>.Default);
        var sourceIdMap = new Dictionary<long, long>();
        var newSources = new List<ProjectDataSource>();
        var usedIds = projectSources.Select(source => source.Id)
            .Concat(legacySources.Select(source => source.Id))
            .Concat(pageBindings.Select(binding => binding.Id))
            .ToHashSet();
        var nextId = usedIds.Count == 0 ? 1 : usedIds.Max() + 1;

        foreach (var legacySource in legacySources)
        {
            if (!pages.TryGetValue(legacySource.PageId, out var page))
            {
                throw new InvalidOperationException(
                    $"Cannot migrate legacy data source {legacySource.Id}: page {legacySource.PageId} does not exist.");
            }

            if (legacySource.TenantId != page.TenantId)
            {
                throw new InvalidOperationException(
                    $"Cannot migrate legacy data source {legacySource.Id}: page and source belong to different tenants.");
            }

            var key = (page.ProjectId, legacySource.SourceKey);
            if (!targetsByKey.TryGetValue(key, out var target))
            {
                var targetId = usedIds.Contains(legacySource.Id) && projectSources.Any(source => source.Id == legacySource.Id)
                    ? TakeNextId(usedIds, ref nextId)
                    : legacySource.Id;
                usedIds.Add(targetId);
                target = new ProjectDataSource
                {
                    Id = targetId,
                    TenantId = legacySource.TenantId,
                    ProjectId = page.ProjectId,
                    SourceKey = legacySource.SourceKey,
                    DisplayName = legacySource.SourceKey,
                    SourceType = legacySource.SourceType,
                    EntityId = legacySource.EntityId,
                    ServiceName = legacySource.ServiceName,
                    MethodName = legacySource.MethodName,
                    DtoName = legacySource.DtoName,
                    DataShape = legacySource.DataShape,
                    SyncMode = legacySource.SyncMode,
                    ParamsJson = legacySource.ParamsJson,
                    SchemaHash = legacySource.SchemaHash,
                    LastSyncedTime = legacySource.LastSyncedTime,
                    OrderNum = legacySource.OrderNum,
                    IsEnabled = legacySource.IsEnabled,
                    IsDeleted = legacySource.IsDeleted,
                    CreateBy = legacySource.CreateBy,
                    CreateUserId = legacySource.CreateUserId,
                    CreateTime = legacySource.CreateTime,
                    UpdateBy = legacySource.UpdateBy,
                    UpdateUserId = legacySource.UpdateUserId,
                    UpdateTime = legacySource.UpdateTime,
                    Remark = legacySource.Remark,
                    DeleteBy = legacySource.DeleteBy,
                    DeleteTime = legacySource.DeleteTime,
                    DeleteUserId = legacySource.DeleteUserId
                };
                targetsByKey.Add(key, target);
                newSources.Add(target);
            }
            else if (!legacySource.IsDeleted && target.IsDeleted)
            {
                target.IsDeleted = false;
                target.DeleteBy = null;
                target.DeleteTime = null;
                target.DeleteUserId = null;
            }

            sourceIdMap[legacySource.Id] = target.Id;
        }

        if (newSources.Count > 0)
        {
            await dbContext.Set<ProjectDataSource>().AddRangeAsync(newSources, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var mapping in sourceIdMap.Where(mapping => mapping.Key != mapping.Value))
        {
            await dbContext.Set<PageComponentBinding>()
                .Where(binding => binding.DataSourceId == mapping.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(binding => binding.DataSourceId, mapping.Value), cancellationToken);
            await dbContext.Set<PageEntityDependency>()
                .Where(dependency => dependency.DataSourceId == mapping.Key)
                .ExecuteUpdateAsync(setters => setters.SetProperty(dependency => dependency.DataSourceId, mapping.Value), cancellationToken);
            await RemapExistingPageBindingsAsync(dbContext, mapping.Key, mapping.Value, cancellationToken);
        }

        pageBindings = await dbContext.Set<PageDataSourceBinding>().ToListAsync(cancellationToken);
        var bindingsToInsert = new List<PageDataSourceBinding>();
        foreach (var legacySource in legacySources.Where(source => !source.IsDeleted))
        {
            var page = pages[legacySource.PageId];
            var targetId = sourceIdMap[legacySource.Id];
            var existingBinding = pageBindings.FirstOrDefault(binding =>
                binding.PageId == page.Id
                && binding.ModuleEntityId == null
                && binding.PageType == null
                && binding.DataSourceId == targetId);
            if (existingBinding != null)
            {
                if (existingBinding.IsDeleted)
                {
                    existingBinding.IsDeleted = false;
                    existingBinding.DeleteBy = null;
                    existingBinding.DeleteTime = null;
                    existingBinding.DeleteUserId = null;
                    existingBinding.BindingStatus = PageBindingStatus.Active;
                    existingBinding.SyncMode = legacySource.SyncMode;
                    existingBinding.LastKnownSchemaHash = targetsByKey[(page.ProjectId, legacySource.SourceKey)].SchemaHash;
                }
                continue;
            }

            var target = targetsByKey[(page.ProjectId, legacySource.SourceKey)];
            bindingsToInsert.Add(new PageDataSourceBinding
            {
                Id = TakeNextId(usedIds, ref nextId),
                TenantId = legacySource.TenantId,
                PageId = page.Id,
                DataSourceId = target.Id,
                BindingRole = PageBindingRole.Container,
                BindingStatus = PageBindingStatus.Active,
                SyncMode = legacySource.SyncMode,
                LastKnownSchemaHash = target.SchemaHash,
                CreateBy = legacySource.CreateBy,
                CreateUserId = legacySource.CreateUserId,
                CreateTime = legacySource.CreateTime
            });
            pageBindings.Add(bindingsToInsert[^1]);
        }

        if (bindingsToInsert.Count > 0)
        {
            await dbContext.Set<PageDataSourceBinding>().AddRangeAsync(bindingsToInsert, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task RemapExistingPageBindingsAsync(
        CodeMasterDbContext dbContext,
        long oldDataSourceId,
        long newDataSourceId,
        CancellationToken cancellationToken)
    {
        var oldBindings = await dbContext.Set<PageDataSourceBinding>()
            .Where(binding => binding.DataSourceId == oldDataSourceId && !binding.IsDeleted)
            .ToListAsync(cancellationToken);
        foreach (var oldBinding in oldBindings)
        {
            var duplicate = await dbContext.Set<PageDataSourceBinding>()
                .AnyAsync(binding => !binding.IsDeleted
                    && binding.Id != oldBinding.Id
                    && binding.PageId == oldBinding.PageId
                    && binding.ModuleEntityId == oldBinding.ModuleEntityId
                    && binding.PageType == oldBinding.PageType
                    && binding.DataSourceId == newDataSourceId,
                    cancellationToken);
            if (duplicate)
            {
                oldBinding.IsDeleted = true;
                oldBinding.DeleteTime = DateTime.UtcNow;
            }
            else
            {
                oldBinding.DataSourceId = newDataSourceId;
            }
        }

        if (oldBindings.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static long TakeNextId(HashSet<long> usedIds, ref long nextId)
    {
        while (usedIds.Contains(nextId))
        {
            nextId = checked(nextId + 1);
        }

        var result = nextId;
        usedIds.Add(result);
        nextId = checked(nextId + 1);
        return result;
    }
}
