using CodeMaster.Agent.Contracts;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Entities;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;
using SqlSugar;

namespace CodeMaster.Agent.Services;

public interface ICodeMasterProjectChangeSetService
{
    Task<ProjectChangeSetValidationResult> ValidateAsync(long projectId, ProjectChangeSetProposal proposal);
    Task<ProjectChangeSetExecutionResult> ExecuteAsync(long projectId, ProjectChangeSetProposal proposal);

    /// <summary>
    /// Applies the deterministic fix-ups a model-authored change set needs before it can be
    /// validated. Exposed as a service step so every entry point — the standalone approval tool and
    /// the work-batch adapter alike — repairs the proposal the same way.
    /// </summary>
    Task<List<string>> RepairKnownIssuesAsync(long projectId, ProjectChangeSetProposal proposal);
}

internal sealed class CodeMasterProjectChangeSetService : ICodeMasterProjectChangeSetService
{
    private readonly ICodeMasterProjectBlueprintService _blueprintService;
    private readonly IProjectModuleService _moduleService;
    private readonly IModuleEntityService _entityService;
    private readonly IProjectService _projectService;
    private readonly ISqlSugarClient _db;

    public CodeMasterProjectChangeSetService(
        ICodeMasterProjectBlueprintService blueprintService,
        IProjectModuleService moduleService,
        IModuleEntityService entityService,
        IProjectService projectService,
        ISqlSugarClient db)
    {
        _blueprintService = blueprintService;
        _moduleService = moduleService;
        _entityService = entityService;
        _projectService = projectService;
        _db = db;
    }

    public async Task<ProjectChangeSetValidationResult> ValidateAsync(long projectId, ProjectChangeSetProposal proposal)
    {
        await ProjectChangeSetFieldCopyExpander.ExpandAsync(
            proposal,
            query => _blueprintService.GetEntityAsync(projectId, query.EntityId, query.EntityName));
        var blueprint = await _blueprintService.GetProjectAsync(projectId);

        // Every entry point — the standalone proposal tool, the work-batch adapter's readiness
        // check and the execution path itself — reaches the rules through here, so the known-issue
        // repairs have to run here too. Validating the raw model output before repair used to reject
        // a change set that the execution path would have repaired a moment later: the readiness
        // check failed, the tool execution was recorded as Failed, and the plan was superseded.
        ApplyKnownIssueRepairs(proposal, blueprint);

        // Module names become namespace segments in the generated project, so one that reuses the
        // project name has to be refused here rather than repaired after generation.
        var project = await _projectService.GetByIdAsync(projectId);
        return CodeMasterProjectChangeSetRules.Validate(blueprint, proposal, project?.ProjectName);
    }

    public async Task<List<string>> RepairKnownIssuesAsync(long projectId, ProjectChangeSetProposal proposal)
    {
        var blueprint = await _blueprintService.GetProjectAsync(projectId);
        return ApplyKnownIssueRepairs(proposal, blueprint);
    }

    /// <summary>
    /// Idempotent by construction: every repair either rewrites a value into its canonical shape or
    /// removes the offending entry, so a second pass (ValidateAsync runs again inside ExecuteAsync)
    /// returns no additional repairs.
    /// </summary>
    private static List<string> ApplyKnownIssueRepairs(
        ProjectChangeSetProposal proposal,
        AiProjectBlueprintDto blueprint)
    {
        var repairs = ProjectChangeSetIssueRepairs.RepairImplicitPrimaryKeys(proposal);
        repairs.AddRange(ProjectChangeSetIssueRepairs.NormalizeOneToManyRelations(proposal));
        repairs.AddRange(ProjectChangeSetIssueRepairs.ResolveIdlessModuleUpdates(proposal, blueprint.Modules));
        repairs.AddRange(ProjectChangeSetIssueRepairs.AssignSingleCandidateModule(proposal, blueprint.Modules));
        repairs.AddRange(ProjectChangeSetIssueRepairs.DeferUnresolvedRelatedTargets(
            proposal,
            blueprint.Modules.SelectMany(module => module.Entities).Select(entity => entity.Name)));
        return repairs;
    }

    public async Task<ProjectChangeSetExecutionResult> ExecuteAsync(long projectId, ProjectChangeSetProposal proposal)
    {
        var validation = await ValidateAsync(projectId, proposal);
        if (!validation.IsValid)
            throw new InvalidOperationException("The project change set is invalid: " + string.Join(" ", validation.Errors));

        var ownsTransaction = _db.Ado.Transaction == null;
        if (ownsTransaction)
            _db.Ado.BeginTran();

        try
        {
        var result = new ProjectChangeSetExecutionResult
        {
            ProjectId = projectId,
            Summary = proposal.Summary.Trim()
        };

        // A previous run can leave a field pointing at an entity that no longer exists. Saving an
        // entity re-validates every field it already owns, so one stale link aborts the whole change
        // set with "Related entity 'X' does not exist." before anything is written.
        result.AutomaticRepairs.AddRange(await RepairDanglingFieldLinksAsync(projectId, null));
        result.AutomaticRepairs.AddRange(await RepairDanglingOneToManyRelationsAsync(projectId));

        // Soft-deleted rows are included on purpose. Resetting a project soft-deletes its modules,
        // and the module-name unique index does not cover IsDeleted, so a deleted module keeps
        // blocking a re-creation of the same name. The global soft-delete query filter hides those
        // rows, which would make the reuse branch below miss them and trip the unique constraint.
        var modules = await QueryModulesIncludingDeletedAsync(projectId);
        var deletedModuleIds = modules.Where(x => x.IsDeleted).Select(x => x.Id).ToHashSet();
        var moduleIdsByName = modules.ToDictionary(x => x.ModuleName, x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var module in proposal.Modules)
        {
            var name = module.ModuleName.Trim();

            // A plan is compiled against the blueprint as it looked at compile time, so a module it
            // intends to create can already exist by the time the batch runs — an earlier attempt
            // created it before failing later in the same change set, or the model created it
            // directly. Creating it again trips the module-name unique constraint and rolls the
            // whole change set back, so reuse the existing module instead.
            if (moduleIdsByName.TryGetValue(name, out var adoptedId))
            {
                // A soft-deleted module still holds the name. Bring it back instead of leaving the
                // project with a module that nothing can see.
                if (deletedModuleIds.Contains(adoptedId))
                {
                    await RestoreModuleAsync(adoptedId, module);
                    deletedModuleIds.Remove(adoptedId);
                }

                // Reported alongside created ones: this change set guarantees these modules exist.
                result.CreatedModules.Add(new ProjectChangeCreatedItem { Id = adoptedId, Name = name });
                continue;
            }

            var id = await _moduleService.CreateAsync(new CreateProjectModuleDto
            {
                ProjectId = projectId,
                ModuleName = name,
                ModuleDescription = module.ModuleDescription.Trim(),
                Icon = NormalizeOptional(module.Icon),
                OrderNum = module.OrderNum,
                RoutePath = NormalizeOptional(module.RoutePath),
                Remark = NormalizeOptional(module.Remark)
            });
            moduleIdsByName[name] = id;
            result.CreatedModules.Add(new ProjectChangeCreatedItem { Id = id, Name = name });
        }

        foreach (var update in proposal.ModuleUpdates)
        {
            var current = await _moduleService.GetByIdAsync(update.ModuleId)
                ?? throw new KeyNotFoundException($"Module '{update.ModuleId}' could not be reloaded.");
            var name = MergeRequired(update.ModuleName, current.ModuleName);
            await _moduleService.UpdateAsync(update.ModuleId, new UpdateProjectModuleDto
            {
                ModuleName = name,
                ModuleDescription = MergeRequired(update.ModuleDescription, current.ModuleDescription),
                Icon = MergeOptional(update.Icon, current.Icon),
                OrderNum = update.OrderNum ?? current.OrderNum,
                RoutePath = MergeOptional(update.RoutePath, current.RoutePath),
                Remark = MergeOptional(update.Remark, current.Remark)
            });

            moduleIdsByName.Remove(current.ModuleName);
            moduleIdsByName[name] = current.Id;
            result.UpdatedModules.Add(new ProjectChangeCreatedItem { Id = current.Id, Name = name });
        }

        var existingEntities = await _entityService.GetListAsync(new ModuleEntityQueryDto
        {
            ProjectId = projectId,
            PageNum = 1,
            PageSize = 10000
        });
        // Same soft-delete reasoning as the modules above: a deleted entity still owns its name in
        // the unique index while staying invisible to the default query filter.
        var allEntities = await QueryEntitiesIncludingDeletedAsync(projectId);
        var deletedEntityIds = allEntities.Where(x => x.IsDeleted).Select(x => x.Id).ToHashSet();
        var entityIdsByName = allEntities.ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
        // Every module that is either alive or has just been brought back. Anything outside this set
        // is still deleted, and entities filed under it stay invisible to the blueprint.
        var liveModuleIds = moduleIdsByName.Values.Where(id => !deletedModuleIds.Contains(id)).ToHashSet();

        foreach (var entity in proposal.Entities)
        {
            var name = entity.Name.Trim();

            // Same reasoning as the modules above: a plan is compiled against the blueprint as it
            // looked at compile time, so an entity it intends to create can already exist by the
            // time the batch runs — an earlier attempt created it before failing later in the same
            // change set. Creating it again trips the entity-name unique constraint and rolls the
            // whole change set back. Reroute it through the update path instead, which merges the
            // description and adds only the fields the existing entity is still missing.
            if (entityIdsByName.TryGetValue(name, out var adoptedEntityId))
            {
                // Bring a soft-deleted entity back before reloading it, otherwise the filtered read
                // below returns null and the name looks free — and the insert then hits the unique
                // constraint the deleted row is still holding.
                if (deletedEntityIds.Contains(adoptedEntityId))
                {
                    await RestoreEntityAsync(projectId, adoptedEntityId);
                    deletedEntityIds.Remove(adoptedEntityId);
                }

                var current = await _entityService.GetByIdAsync(adoptedEntityId);
                if (current != null)
                {
                    // An adopted entity can still be filed under a module that a previous reset
                    // deleted. The blueprint only walks live modules, so an entity whose module is
                    // gone is invisible to every later verification even though the row is alive —
                    // which reads as "reported success but metadata verification found missing
                    // entities". Re-home it to the module this change set actually targets.
                    var targetModuleId = ResolveModuleId(entity, moduleIdsByName);
                    if (deletedModuleIds.Contains(targetModuleId))
                    {
                        await RestoreModuleAsync(targetModuleId, null);
                        deletedModuleIds.Remove(targetModuleId);
                    }
                    if (current.ModuleId != targetModuleId && !liveModuleIds.Contains(current.ModuleId))
                        await MoveEntityToModuleAsync(adoptedEntityId, targetModuleId);

                    var existingFieldNames = current.Fields
                        .Select(field => field.Name.Trim())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    proposal.EntityUpdates.Add(new UpdateEntityProposal
                    {
                        EntityId = adoptedEntityId,
                        Name = name,
                        Description = entity.Description,
                        BusinessDescription = entity.BusinessDescription,
                        NewFields = entity.Fields
                            .Where(field => !existingFieldNames.Contains(field.Name.Trim()))
                            .ToList()
                    });
                    // Reported alongside created ones: this change set guarantees these entities exist.
                    result.CreatedEntities.Add(new ProjectChangeCreatedItem { Id = adoptedEntityId, Name = name });
                    continue;
                }

                // The id no longer resolves; treat the name as free and create fresh below.
                entityIdsByName.Remove(name);
            }

            var moduleId = ResolveModuleId(entity, moduleIdsByName);
            if (deletedModuleIds.Contains(moduleId))
            {
                await RestoreModuleAsync(moduleId, null);
                deletedModuleIds.Remove(moduleId);
            }

            var id = await _entityService.CreateAsync(new CreateModuleEntityDto
            {
                ProjectId = projectId,
                ModuleId = moduleId,
                Name = name,
                Description = entity.Description.Trim(),
                BusinessDescription = NormalizeOptional(entity.BusinessDescription),
                HasPrimaryKey = entity.HasPrimaryKey,
                TableName = NormalizeOptional(entity.TableName),
                IsTree = entity.IsTree,
                IsReadOnly = entity.IsReadOnly,
                HasTenant = entity.HasTenant,
                HasDataPermission = entity.HasDataPermission,
                HasAudit = entity.HasAudit,
                HasSoftDelete = entity.HasSoftDelete,
                GenerateFrontend = entity.GenerateFrontend,
                IsChildTable = entity.IsChildTable,
                FrontendRoute = NormalizeOptional(entity.FrontendRoute),
                MenuIcon = NormalizeOptional(entity.MenuIcon),
                OrderNum = entity.OrderNum,
                Remark = NormalizeOptional(entity.Remark),
                Fields = MapFields(entity)
            });
            entityIdsByName[name] = id;
            result.CreatedEntities.Add(new ProjectChangeCreatedItem { Id = id, Name = name });
        }

        foreach (var update in proposal.EntityUpdates.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            var current = allEntities.First(x => x.Id == update.EntityId);
            entityIdsByName.Remove(current.Name);
            entityIdsByName[update.Name!.Trim()] = current.Id;
        }

        foreach (var update in proposal.EntityUpdates)
        {
            var current = await _entityService.GetByIdAsync(update.EntityId)
                ?? throw new KeyNotFoundException($"Entity '{update.EntityId}' could not be reloaded.");
            var dto = CreateUpdateDto(current, update, entityIdsByName);
            await _entityService.UpdateAsync(update.EntityId, dto);
            result.UpdatedEntities.Add(new ProjectChangeCreatedItem
            {
                Id = update.EntityId,
                Name = dto.Name
            });
            result.CreatedRelationCount += dto.NewRelations.Count + dto.NewEntityRelations.Count;
        }

        foreach (var entity in proposal.Entities.Where(x => x.Relations.Count > 0))
        {
            var sourceEntityId = entityIdsByName[entity.Name.Trim()];
            var sourceEntity = await _entityService.GetByIdAsync(sourceEntityId)
                ?? throw new KeyNotFoundException($"Entity '{sourceEntityId}' could not be reloaded.");
            var oneToManyRelations = entity.Relations
                .Where(IsLegacyOneToMany)
                .Select(relation => MapOneToManyRelation(relation, entityIdsByName))
                .ToList();
            var entityRelations = entity.Relations
                .Where(relation => !IsLegacyOneToMany(relation))
                .Select(relation => MapRelation(relation, entityIdsByName))
                .ToList();
            await _entityService.UpdateAsync(
                sourceEntityId,
                CreateRelationOnlyUpdateDto(sourceEntity, oneToManyRelations, entityRelations));
            result.CreatedRelationCount += oneToManyRelations.Count + entityRelations.Count;
        }

        // Dictionaries target the generated project's database and must run after
        // entity metadata has been committed in the same ordered approval.
        //
        // A dictionary is decoration: a missing dictionary only leaves a select empty. The target
        // database, however, is created by the generated project's own migration — a project that
        // has not been initialised yet has no sys_dict_types table at all. Failing the whole change
        // set (and with it the plan stage) over a table that does not exist yet would be far more
        // damaging than an empty select, so the failure is recorded and execution continues.
        foreach (var dictionary in proposal.Dictionaries)
        {
            try
            {
                await _projectService.SaveProjectDictionaryAsync(projectId, new SaveProjectDictionaryDto
                {
                    TenantId = dictionary.TenantId,
                    DictType = dictionary.DictType,
                    DictName = dictionary.DictName,
                    LangKey = dictionary.LangKey,
                    Status = dictionary.Status,
                    Sort = dictionary.Sort,
                    Remark = dictionary.Remark,
                    Items = dictionary.Items.Select(item => new SaveProjectDictionaryItemDto
                    {
                        Label = item.Label,
                        Value = item.Value,
                        LangKey = item.LangKey,
                        IsDefault = item.IsDefault,
                        Status = item.Status,
                        Sort = item.Sort,
                        Remark = item.Remark
                    }).ToList()
                });
                result.CreatedDictionaryCount++;
            }
            catch (Exception ex)
            {
                result.AutomaticRepairs.Add(
                    $"字典 '{dictionary.DictType}' 未写入生成项目数据库：{ex.Message.Trim()} " +
                    "（通常是项目尚未初始化/迁移；初始化后在字典页补建即可）");
            }
        }

        foreach (var entityId in proposal.DeleteEntityIds.Distinct())
        {
            await _entityService.DeleteAsync(entityId);
            result.DeletedEntityIds.Add(entityId);
        }

        foreach (var moduleId in proposal.DeleteModuleIds.Distinct())
        {
            await _moduleService.DeleteAsync(moduleId);
            result.DeletedModuleIds.Add(moduleId);
        }

        AddClientActions(projectId, proposal, existingEntities, result);
        if (ownsTransaction)
            _db.Ado.CommitTran();
        return result;
        }
        catch
        {
            if (ownsTransaction)
                _db.Ado.RollbackTran();
            throw;
        }
    }

    /// <summary>
    /// Intentionally adds nothing. The agent only writes metadata: code generation, build, and
    /// tests are manual actions the user triggers from the project page, so a change set never
    /// queues generation client actions any more.
    /// </summary>
    internal static void AddClientActions(
        long projectId,
        ProjectChangeSetProposal proposal,
        IReadOnlyCollection<ModuleEntityDto> existingEntities,
        ProjectChangeSetExecutionResult result)
    {
        _ = projectId;
        _ = proposal;
        _ = existingEntities;
        _ = result;
    }

    /// <summary>
    /// Always false: no client-side build action is ever queued by the agent, so the change set
    /// never needs the build permission.
    /// </summary>
    internal static bool RequiresBuildPermission(ProjectChangeSetProposal proposal)
    {
        _ = proposal;
        return false;
    }

    private static long ResolveModuleId(CreateEntityProposal entity, IReadOnlyDictionary<string, long> moduleIdsByName)
    {
        if (entity.ModuleId > 0)
            return entity.ModuleId;

        if (!string.IsNullOrWhiteSpace(entity.ModuleName) && moduleIdsByName.TryGetValue(entity.ModuleName.Trim(), out var moduleId))
            return moduleId;

        throw new InvalidOperationException($"Unable to resolve module for entity '{entity.Name}'.");
    }

    /// <summary>
    /// Reads every module of the project, soft-deleted ones included. The global soft-delete query
    /// filter has to be lifted for the read because a deleted module still owns its name in the
    /// unique index and therefore still decides whether the name is free.
    /// </summary>
    private async Task<List<ProjectModule>> QueryModulesIncludingDeletedAsync(long projectId)
    {
        _db.QueryFilter.ClearAndBackup<ISoftDelete>();
        try
        {
            return await _db.Queryable<ProjectModule>()
                .Where(module => module.ProjectId == projectId)
                .ToListAsync();
        }
        finally
        {
            _db.QueryFilter.Restore();
        }
    }

    /// <summary>
    /// Reads every entity of the project, soft-deleted ones included. See
    /// <see cref="QueryModulesIncludingDeletedAsync"/> for why the filter is lifted.
    /// </summary>
    private async Task<List<ModuleEntity>> QueryEntitiesIncludingDeletedAsync(long projectId)
    {
        _db.QueryFilter.ClearAndBackup<ISoftDelete>();
        try
        {
            return await _db.Queryable<ModuleEntity>()
                .Where(entity => entity.ProjectId == projectId)
                .ToListAsync();
        }
        finally
        {
            _db.QueryFilter.Restore();
        }
    }

    /// <summary>
    /// Clears the soft-delete mark left on a module by an earlier reset so the change set can reuse
    /// it. <paramref name="module"/> is optional: when supplied its metadata is applied as well,
    /// otherwise only the deletion state is cleared.
    /// </summary>
    private async Task RestoreModuleAsync(long moduleId, CreateModuleProposal? module)
    {
        var updatable = _db.Updateable<ProjectModule>()
            .SetColumns(item => item.IsDeleted == false)
            .SetColumns(item => item.DeleteTime == (DateTime?)null)
            .SetColumns(item => item.DeleteBy == (string?)null)
            .SetColumns(item => item.DeleteUserId == (long?)null)
            .SetColumns(item => item.UpdateTime == DateTime.UtcNow);

        if (module != null)
        {
            var description = module.ModuleDescription.Trim();
            var icon = NormalizeOptional(module.Icon);
            var routePath = NormalizeOptional(module.RoutePath);
            var remark = NormalizeOptional(module.Remark);
            updatable = updatable
                .SetColumns(item => item.ModuleDescription == description)
                .SetColumns(item => item.Icon == icon)
                .SetColumns(item => item.OrderNum == module.OrderNum)
                .SetColumns(item => item.RoutePath == routePath)
                .SetColumns(item => item.Remark == remark);
        }

        await updatable.Where(item => item.Id == moduleId).ExecuteCommandAsync();
    }

    /// <summary>
    /// Files an adopted entity under the module this change set targets. Entities left under a
    /// module that an earlier reset deleted never show up in the project blueprint.
    /// </summary>
    private async Task MoveEntityToModuleAsync(long entityId, long moduleId)
    {
        await _db.Updateable<ModuleEntity>()
            .SetColumns(item => item.ModuleId == moduleId)
            .SetColumns(item => item.UpdateTime == DateTime.UtcNow)
            .Where(item => item.Id == entityId)
            .ExecuteCommandAsync();
    }

    /// <summary>
    /// Clears the soft-delete mark left on an entity by an earlier reset so the change set can reuse
    /// it instead of failing on the entity-name unique constraint.
    /// </summary>
    private async Task RestoreEntityAsync(long projectId, long entityId)
    {
        await _db.Updateable<ModuleEntity>()
            .SetColumns(item => item.IsDeleted == false)
            .SetColumns(item => item.DeleteTime == (DateTime?)null)
            .SetColumns(item => item.DeleteBy == (string?)null)
            .SetColumns(item => item.DeleteUserId == (long?)null)
            .SetColumns(item => item.UpdateTime == DateTime.UtcNow)
            .Where(item => item.Id == entityId)
            .ExecuteCommandAsync();

        // The row is live again, so its old field metadata is live again too — and that metadata
        // belongs to the run that created it, which may have referenced an entity that has since
        // disappeared. Repair it before anything validates the entity.
        await RepairDanglingFieldLinksAsync(projectId, new[] { entityId });
    }

    /// <summary>
    /// Downgrades fields whose related-entity link points at something the project no longer has.
    /// </summary>
    /// <remarks>
    /// Saving an entity re-validates every field it already owns, not just the ones the change set
    /// adds. A field left behind by an earlier run can therefore block a change set that never
    /// mentions it: the validation walks the stored fields, hits a <c>RelatedEntityName</c> that no
    /// longer resolves, and throws "Related entity 'X' does not exist." — which fails the whole
    /// batch and, because a reset only soft-deletes, keeps failing every later run on the same
    /// project. That is what made the pipeline look randomly flaky: a clean project passes, a
    /// project carrying one stale link never does.
    /// </remarks>
    private async Task<List<string>> RepairDanglingFieldLinksAsync(long projectId, IEnumerable<long>? entityIds)
    {
        var scopedIds = entityIds?.ToList();
        var names = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.Name })
            .ToListAsync();
        if (names.Count == 0)
            return new List<string>();

        var allowed = names.Select(item => item.Id).ToHashSet();
        var targetIds = scopedIds == null ? allowed.ToList() : scopedIds.Where(allowed.Contains).ToList();
        if (targetIds.Count == 0)
            return new List<string>();

        var knownEntityNames = names.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fields = await _db.Queryable<EntityField>()
            .Where(field => targetIds.Contains(field.ModuleEntityId))
            .Where(field => field.RelatedEntityName != null && field.RelatedEntityName != "")
            .ToListAsync();

        var repairs = new List<string>();
        var stale = fields.Where(field =>
                !string.IsNullOrWhiteSpace(field.RelatedEntityName) &&
                !knownEntityNames.Contains(field.RelatedEntityName.Trim()) &&
                !SystemReferenceEntityCatalog.TryGet(field.RelatedEntityName.Trim(), out _))
            .ToList();
        if (stale.Count == 0)
            return repairs;

        var fieldNames = (await _db.Queryable<EntityField>()
                .Where(field => targetIds.Contains(field.ModuleEntityId))
                .Select(field => new { field.Id, field.Name })
                .ToListAsync())
            .ToDictionary(item => item.Id, item => item.Name);

        foreach (var field in stale)
        {
            var target = field.RelatedEntityName!.Trim();
            await _db.Updateable<EntityField>()
                .SetColumns(item => item.RelatedEntityName == null)
                .SetColumns(item => item.RelatedEntityIdField == null)
                .SetColumns(item => item.RelatedEntityDisplayFields == null)
                .SetColumns(item => item.SelectDataSource == null)
                .SetColumns(item => item.SelectOptions == null)
                .SetColumns(item => item.IsMultiple == false)
                .SetColumns(item => item.FormControlType == "input")
                .Where(item => item.Id == field.Id)
                .ExecuteCommandAsync();

            var owner = fieldNames.TryGetValue(field.Id, out var ownerName) ? ownerName : field.ModuleEntityId.ToString();
            repairs.Add(
                $"Cleared the stale '{owner}' link to '{target}': that entity no longer exists, so the control fell back to a plain input.");
        }

        return repairs;
    }

    /// <summary>
    /// Brings the project's one-to-many relations back to a state the generator can consume:
    /// stale children are re-pointed or dropped, and duplicate parent-child pairs collapse to one row.
    /// </summary>
    /// <remarks>
    /// Deleting a module or an entity leaves the one-to-many rows that reference it behind. The row
    /// keeps the child entity name next to its id, so a stale id can usually be repaired by name;
    /// when the name is gone as well the row is dropped. Without this, code generation keeps walking
    /// a child that does not exist and every later generation request for the project fails with
    /// "One-to-many child entity does not exist".
    ///
    /// Two live rows for the same parent and child are just as fatal, only later: the entity
    /// template emits one navigation property per relation, so the child type ends up declared twice
    /// and the generated project fails to compile with CS0102. Collapsing them here keeps the fix at
    /// the data level instead of teaching the generator to silently drop a property.
    /// </remarks>
    private async Task<List<string>> RepairDanglingOneToManyRelationsAsync(long projectId)
    {
        var entities = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new { item.Id, item.Name })
            .ToListAsync();
        if (entities.Count == 0)
            return new List<string>();

        var liveIds = entities.Select(item => item.Id).ToHashSet();
        var idByName = entities
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);

        // 按 Id 排序，保证“保留最早那条、删掉后来的”是确定的。
        var relations = await _db.Queryable<OneToManyRelation>()
            .Where(item => liveIds.Contains(item.ModuleEntityId))
            .OrderBy(item => item.Id)
            .ToListAsync();

        // 已经成立的父子对。重指之前必须查这张表，否则会把一条旧关系改到已经有有效关系的
        // 子实体上，同一对父子出现两条关系，生成的实体就会输出两个同名导航属性（CS0102）。
        var livePairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in relations)
        {
            if (liveIds.Contains(existing.ChildEntityId))
                livePairs.Add(existing.ModuleEntityId + ":" + existing.ChildEntityId);
        }

        var repairs = new List<string>();

        // 先把“父子都还在、但同一对出现了多次”的关系收敛成一条。
        var keptPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in relations)
        {
            if (!liveIds.Contains(relation.ChildEntityId))
                continue;
            if (keptPairs.Add(relation.ModuleEntityId + ":" + relation.ChildEntityId))
                continue;

            await SoftDeleteRelationAsync(relation.Id);
            repairs.Add(
                $"Removed a duplicate one-to-many relation to '{relation.ChildEntityName}', which the parent already declares once.");
        }

        foreach (var relation in relations)
        {
            if (liveIds.Contains(relation.ChildEntityId))
                continue;

            var childName = relation.ChildEntityName?.Trim() ?? string.Empty;
            if (childName.Length > 0 && idByName.TryGetValue(childName, out var resolvedId) &&
                livePairs.Add(relation.ModuleEntityId + ":" + resolvedId))
            {
                await _db.Updateable<OneToManyRelation>()
                    .SetColumns(item => item.ChildEntityId == resolvedId)
                    .Where(item => item.Id == relation.Id)
                    .ExecuteCommandAsync();
                repairs.Add(
                    $"Re-pointed the child table of a one-to-many relation to '{childName}', which now holds a different id than the relation recorded.");
                continue;
            }

            await SoftDeleteRelationAsync(relation.Id);
            repairs.Add(
                childName.Length > 0 && idByName.ContainsKey(childName)
                    ? $"Removed a duplicate one-to-many relation to '{childName}', which is already reachable through an existing relation."
                    : $"Removed a one-to-many relation whose child entity '{childName}' no longer exists in the project.");
        }

        return repairs;
    }

    private async Task SoftDeleteRelationAsync(long relationId)
    {
        await _db.Updateable<OneToManyRelation>()
            .SetColumns(item => item.IsDeleted == true)
            .SetColumns(item => item.DeleteTime == DateTime.UtcNow)
            .Where(item => item.Id == relationId)
            .ExecuteCommandAsync();
    }

    private static CreateEntityFieldDto MapField(CreateEntityFieldProposal field)
    {
        return new CreateEntityFieldDto
        {
            Name = field.Name.Trim(),
            Description = field.Description.Trim(),
            BusinessDescription = NormalizeOptional(field.BusinessDescription),
            DataType = CSharpDataTypeNormalizer.Normalize(field.DataType),
            IsNullable = field.IsNullable,
            MaxLength = field.MaxLength,
            Precision = field.Precision,
            Scale = field.Scale,
            DefaultValue = NormalizeOptional(field.DefaultValue),
            IsIgnore = field.IsIgnore,
            IsPrimaryKey = field.IsPrimaryKey,
            IsRequired = field.IsRequired,
            MinValue = NormalizeOptional(field.MinValue),
            MaxValue = NormalizeOptional(field.MaxValue),
            RegexPattern = NormalizeOptional(field.RegexPattern),
            IsEmail = field.IsEmail,
            IsPhone = field.IsPhone,
            ShowInList = field.ShowInList,
            ShowInDetail = field.ShowInDetail,
            ShowInAddForm = field.ShowInAddForm,
            ShowInEditForm = field.ShowInEditForm,
            ShowInSearch = field.ShowInSearch,
            FormControlType = field.FormControlType.Trim(),
            ListWidth = field.ListWidth,
            OrderNum = field.OrderNum,
            SelectDataSource = NormalizeOptional(field.SelectDataSource),
            SelectOptions = NormalizeOptional(field.SelectOptions),
            IsMultiple = field.IsMultiple,
            RelatedEntityName = NormalizeOptional(field.RelatedEntityName),
            RelatedEntityIdField = NormalizeOptional(field.RelatedEntityIdField),
            RelatedEntityDisplayFields = NormalizeOptional(field.RelatedEntityDisplayFields),
            ResultMappings = NormalizeOptional(field.ResultMappings),
            FieldCategory = string.IsNullOrWhiteSpace(field.FieldCategory) ? "Normal" : field.FieldCategory.Trim(),
            Formula = NormalizeOptional(field.Formula),
            AggregateType = NormalizeOptional(field.AggregateType),
            AggregateChildEntityId = field.AggregateChildEntityId,
            AggregateChildFieldName = NormalizeOptional(field.AggregateChildFieldName),
            AggregateSeparator = NormalizeOptional(field.AggregateSeparator),
            Remark = NormalizeOptional(field.Remark)
        };
    }

    internal static List<CreateEntityFieldDto> MapFields(CreateEntityProposal entity)
    {
        var fields = entity.Fields.Select(MapField).ToList();
        if (entity.HasPrimaryKey && fields.All(field => !field.IsPrimaryKey))
        {
            fields.Insert(0, new CreateEntityFieldDto
            {
                Name = "Id",
                Description = "主键",
                IsSystemField = true,
                DataType = "long",
                IsNullable = false,
                IsPrimaryKey = true,
                IsRequired = true,
                ShowInList = false,
                ShowInDetail = false,
                ShowInAddForm = false,
                ShowInEditForm = false,
                ShowInSearch = false,
                FormControlType = "input",
                OrderNum = 0
            });
        }

        ApplyDefaultSearchFields(fields);
        return fields;
    }

    /// <summary>实体一个查询字段都没有时，最多补这么多个。</summary>
    private const int DefaultSearchFieldCount = 4;

    /// <summary>
    /// 模型几乎不会主动写 <c>showInSearch</c>：<see cref="CreateEntityFieldProposal"/> 里
    /// showInList / showInDetail / showInAddForm / showInEditForm 都默认 true，只有 showInSearch
    /// 默认 false。于是整条链路下来 <c>sys_entity_field.show_in_search</c> 全是 false，
    /// 生成的列表页搜索区只剩两个按钮，一个查询条件都没有。
    /// </summary>
    /// <remarks>
    /// 兜底只在「整个实体一个查询字段都没有」时生效——模型显式指定过任意一个查询字段，
    /// 就认为它想清楚了，完全不插手。
    /// </remarks>
    private static void ApplyDefaultSearchFields(List<CreateEntityFieldDto> fields)
    {
        if (fields.Any(field => field.ShowInSearch))
            return;

        var picked = fields
            .Select((field, index) => (field, index))
            .Where(item => IsDefaultSearchCandidate(item.field))
            .OrderBy(item => item.field.OrderNum)
            .ThenBy(item => item.index)
            .Take(DefaultSearchFieldCount)
            .Select(item => item.field);

        foreach (var field in picked)
            field.ShowInSearch = true;
    }

    private static readonly HashSet<string> NonSearchableControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "textarea", "editor", "file", "image", "upload", "table-column"
    };

    private static bool IsDefaultSearchCandidate(CreateEntityFieldDto field)
    {
        if (field.IsPrimaryKey || field.IsSystemField || field.IsIgnore)
            return false;

        if (!string.Equals(field.FieldCategory, "Normal", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(field.Formula) || !string.IsNullOrWhiteSpace(field.AggregateType))
            return false;

        if (NonSearchableControlTypes.Contains(field.FormControlType ?? string.Empty))
            return false;

        // 长文本塞进查询条件没意义，还会把搜索区撑得很宽。
        return field.MaxLength is null or <= 200;
    }

    private static UpdateEntityFieldWithIdDto MapUpdatedField(
        EntityFieldDto current,
        UpdateEntityFieldProposal update)
    {
        return new UpdateEntityFieldWithIdDto
        {
            Id = current.Id,
            Name = MergeRequired(update.Name, current.Name),
            Description = MergeRequired(update.Description, current.Description),
            BusinessDescription = update.BusinessDescription ?? current.BusinessDescription,
            DataType = CSharpDataTypeNormalizer.Normalize(MergeRequired(update.DataType, current.DataType)),
            IsNullable = update.IsNullable ?? current.IsNullable,
            MaxLength = update.ClearMaxLength ? null : update.MaxLength ?? current.MaxLength,
            Precision = update.ClearPrecision ? null : update.Precision ?? current.Precision,
            Scale = update.ClearScale ? null : update.Scale ?? current.Scale,
            DefaultValue = MergeOptional(update.DefaultValue, current.DefaultValue),
            IsIgnore = update.IsIgnore ?? current.IsIgnore,
            IsPrimaryKey = update.IsPrimaryKey ?? current.IsPrimaryKey,
            IsRequired = update.IsRequired ?? current.IsRequired,
            MinValue = MergeOptional(update.MinValue, current.MinValue),
            MaxValue = MergeOptional(update.MaxValue, current.MaxValue),
            RegexPattern = MergeOptional(update.RegexPattern, current.RegexPattern),
            IsEmail = update.IsEmail ?? current.IsEmail,
            IsPhone = update.IsPhone ?? current.IsPhone,
            ShowInList = update.ShowInList ?? current.ShowInList,
            ShowInDetail = update.ShowInDetail ?? current.ShowInDetail,
            ShowInAddForm = update.ShowInAddForm ?? current.ShowInAddForm,
            ShowInEditForm = update.ShowInEditForm ?? current.ShowInEditForm,
            ShowInSearch = update.ShowInSearch ?? current.ShowInSearch,
            FormControlType = MergeRequired(update.FormControlType, current.FormControlType),
            ListWidth = update.ClearListWidth ? null : update.ListWidth ?? current.ListWidth,
            OrderNum = update.OrderNum ?? current.OrderNum,
            SelectDataSource = MergeOptional(update.SelectDataSource, current.SelectDataSource),
            SelectOptions = MergeOptional(update.SelectOptions, current.SelectOptions),
            IsMultiple = update.IsMultiple ?? current.IsMultiple,
            RelatedEntityName = MergeOptional(update.RelatedEntityName, current.RelatedEntityName),
            RelatedEntityIdField = MergeOptional(update.RelatedEntityIdField, current.RelatedEntityIdField),
            RelatedEntityDisplayFields = MergeOptional(update.RelatedEntityDisplayFields, current.RelatedEntityDisplayFields),
            ResultMappings = MergeOptional(update.ResultMappings, current.ResultMappings),
            FieldCategory = MergeRequired(update.FieldCategory, current.FieldCategory),
            Formula = MergeOptional(update.Formula, current.Formula),
            AggregateType = MergeOptional(update.AggregateType, current.AggregateType),
            AggregateChildEntityId = update.ClearAggregateChildEntityId
                ? null
                : update.AggregateChildEntityId ?? current.AggregateChildEntityId,
            AggregateChildFieldName = MergeOptional(update.AggregateChildFieldName, current.AggregateChildFieldName),
            AggregateSeparator = MergeOptional(update.AggregateSeparator, current.AggregateSeparator),
            Remark = MergeOptional(update.Remark, current.Remark)
        };
    }

    private static CreateEntityRelationDto MapRelation(
        CreateEntityRelationProposal relation,
        IReadOnlyDictionary<string, long> entityIdsByName)
    {
        return new CreateEntityRelationDto
        {
            TargetEntityId = ResolveTargetEntityId(relation.TargetEntityId, relation.TargetEntityName, null, entityIdsByName),
            RelationName = relation.RelationName.Trim(),
            SourceField = relation.SourceField.Trim(),
            TargetField = relation.TargetField.Trim(),
            Cardinality = Enum.Parse<EntityRelationCardinality>(relation.Cardinality, true),
            Ownership = Enum.Parse<EntityRelationOwnership>(relation.Ownership, true),
            IsRequired = relation.IsRequired,
            DeleteBehavior = Enum.Parse<EntityRelationDeleteBehavior>(relation.DeleteBehavior, true),
            OrderNum = relation.OrderNum
        };
    }

    internal static bool IsLegacyOneToMany(CreateEntityRelationProposal relation)
    {
        return Enum.TryParse<EntityRelationCardinality>(relation.Cardinality, true, out var cardinality) &&
               Enum.TryParse<EntityRelationOwnership>(relation.Ownership, true, out var ownership) &&
               cardinality == EntityRelationCardinality.OneToMany &&
               ownership == EntityRelationOwnership.Owned;
    }

    internal static CreateOneToManyRelationDto MapOneToManyRelation(
        CreateEntityRelationProposal relation,
        IReadOnlyDictionary<string, long> entityIdsByName)
    {
        var childEntityId = ResolveTargetEntityId(
            relation.TargetEntityId,
            relation.TargetEntityName,
            null,
            entityIdsByName);
        var childEntityName = !string.IsNullOrWhiteSpace(relation.TargetEntityName)
            ? relation.TargetEntityName.Trim()
            : entityIdsByName.First(item => item.Value == childEntityId).Key;
        return new CreateOneToManyRelationDto
        {
            MasterField = relation.SourceField.Trim(),
            ChildEntityId = childEntityId,
            ChildEntityName = childEntityName,
            ChildForeignKey = relation.TargetField.Trim(),
            OrderNum = relation.OrderNum
        };
    }

    private static UpdateEntityRelationWithIdDto MapUpdatedRelation(
        EntityRelationDto current,
        UpdateEntityRelationProposal update,
        IReadOnlyDictionary<string, long> entityIdsByName)
    {
        return new UpdateEntityRelationWithIdDto
        {
            Id = current.Id,
            TargetEntityId = ResolveTargetEntityId(update.TargetEntityId, update.TargetEntityName, current.TargetEntityId, entityIdsByName),
            RelationName = MergeRequired(update.RelationName, current.RelationName),
            SourceField = MergeRequired(update.SourceField, current.SourceField),
            TargetField = MergeRequired(update.TargetField, current.TargetField),
            Cardinality = update.Cardinality == null
                ? current.Cardinality
                : Enum.Parse<EntityRelationCardinality>(update.Cardinality, true),
            Ownership = update.Ownership == null
                ? current.Ownership
                : Enum.Parse<EntityRelationOwnership>(update.Ownership, true),
            IsRequired = update.IsRequired ?? current.IsRequired,
            DeleteBehavior = update.DeleteBehavior == null
                ? current.DeleteBehavior
                : Enum.Parse<EntityRelationDeleteBehavior>(update.DeleteBehavior, true),
            OrderNum = update.OrderNum ?? current.OrderNum
        };
    }

    private static UpdateModuleEntityDto CreateUpdateDto(
        ModuleEntityDto current,
        UpdateEntityProposal update,
        IReadOnlyDictionary<string, long> entityIdsByName)
    {
        var fieldsById = current.Fields.ToDictionary(x => x.Id);
        var relationsById = current.EntityRelations.ToDictionary(x => x.Id);
        return new UpdateModuleEntityDto
        {
            Name = MergeRequired(update.Name, current.Name),
            Description = MergeRequired(update.Description, current.Description),
            BusinessDescription = update.BusinessDescription ?? current.BusinessDescription,
            HasPrimaryKey = update.HasPrimaryKey ?? current.HasPrimaryKey,
            TableName = MergeOptional(update.TableName, current.TableName),
            IsTree = update.IsTree ?? current.IsTree,
            IsReadOnly = update.IsReadOnly ?? current.IsReadOnly,
            HasTenant = update.HasTenant ?? current.HasTenant,
            HasDataPermission = update.HasDataPermission ?? current.HasDataPermission,
            HasAudit = update.HasAudit ?? current.HasAudit,
            HasSoftDelete = update.HasSoftDelete ?? current.HasSoftDelete,
            GenerateFrontend = update.GenerateFrontend ?? current.GenerateFrontend,
            IsChildTable = update.IsChildTable ?? current.IsChildTable,
            FrontendRoute = MergeOptional(update.FrontendRoute, current.FrontendRoute),
            MenuIcon = MergeOptional(update.MenuIcon, current.MenuIcon),
            OrderNum = update.OrderNum ?? current.OrderNum,
            Remark = MergeOptional(update.Remark, current.Remark),
            NewFields = update.NewFields.Select(MapField).ToList(),
            UpdatedFields = update.UpdatedFields
                .Select(field => MapUpdatedField(fieldsById[field.FieldId], field))
                .ToList(),
            DeletedFieldIds = update.DeletedFieldIds.Distinct().ToList(),
            NewRelations = update.NewRelations
                .Where(IsLegacyOneToMany)
                .Select(relation => MapOneToManyRelation(relation, entityIdsByName))
                .ToList(),
            NewEntityRelations = update.NewRelations
                .Where(relation => !IsLegacyOneToMany(relation))
                .Select(relation => MapRelation(relation, entityIdsByName))
                .ToList(),
            UpdatedEntityRelations = update.UpdatedRelations
                .Select(relation => MapUpdatedRelation(relationsById[relation.RelationId], relation, entityIdsByName))
                .ToList(),
            DeletedEntityRelationIds = update.DeletedRelationIds.Distinct().ToList()
        };
    }

    private static UpdateModuleEntityDto CreateRelationOnlyUpdateDto(
        ModuleEntityDto entity,
        List<CreateOneToManyRelationDto> oneToManyRelations,
        List<CreateEntityRelationDto> entityRelations)
    {
        return new UpdateModuleEntityDto
        {
            Name = entity.Name,
            Description = entity.Description,
            BusinessDescription = entity.BusinessDescription,
            HasPrimaryKey = entity.HasPrimaryKey,
            TableName = entity.TableName,
            IsTree = entity.IsTree,
            IsReadOnly = entity.IsReadOnly,
            HasTenant = entity.HasTenant,
            HasDataPermission = entity.HasDataPermission,
            HasAudit = entity.HasAudit,
            HasSoftDelete = entity.HasSoftDelete,
            GenerateFrontend = entity.GenerateFrontend,
            IsChildTable = entity.IsChildTable,
            FrontendRoute = entity.FrontendRoute,
            MenuIcon = entity.MenuIcon,
            OrderNum = entity.OrderNum,
            Remark = entity.Remark,
            NewRelations = oneToManyRelations,
            NewEntityRelations = entityRelations
        };
    }

    private static long ResolveTargetEntityId(
        long? targetEntityId,
        string? targetEntityName,
        long? currentTargetEntityId,
        IReadOnlyDictionary<string, long> entityIdsByName)
    {
        if (targetEntityId is > 0)
            return targetEntityId.Value;

        if (!string.IsNullOrWhiteSpace(targetEntityName)
            && entityIdsByName.TryGetValue(targetEntityName.Trim(), out var resolvedId))
        {
            return resolvedId;
        }

        if (currentTargetEntityId is > 0 && targetEntityId == null && targetEntityName == null)
            return currentTargetEntityId.Value;

        throw new InvalidOperationException($"Unable to resolve target entity '{targetEntityName ?? targetEntityId?.ToString()}'.");
    }

    private static string MergeRequired(string? value, string current)
        => value == null ? current : value.Trim();

    private static string? MergeOptional(string? value, string? current)
        => value == null ? current : NormalizeOptional(value);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
