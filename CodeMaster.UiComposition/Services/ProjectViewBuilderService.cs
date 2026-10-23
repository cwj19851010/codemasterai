using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen.Relations;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class ProjectViewBuilderService : IProjectViewBuilderService
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private readonly ISqlSugarClient _db;
    private readonly EntityRelationGraphBuilder _relationGraphBuilder;
    private readonly ProjectDataSourceService _projectDataSourceService;

    public ProjectViewBuilderService(ISqlSugarClient db, ProjectDataSourceService? projectDataSourceService = null)
    {
        _db = db;
        _relationGraphBuilder = new EntityRelationGraphBuilder(db);
        _projectDataSourceService = projectDataSourceService ?? new ProjectDataSourceService(db);
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ViewBuilderCatalogDto> GetEntityCatalogAsync(long projectId)
    {
        var project = await GetProjectAsync(projectId);
        var entities = await LoadEntitiesAsync(projectId);
        var fields = await LoadFieldsAsync(entities.Select(item => item.Id).ToArray());
        var fieldsByEntity = fields
            .GroupBy(item => item.ModuleEntityId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var graph = await _relationGraphBuilder.BuildForProjectAsync(projectId);
        var entityIds = entities.Select(item => item.Id).ToHashSet();

        return new ViewBuilderCatalogDto
        {
            ProjectId = project.Id,
            DatabaseType = project.DatabaseType,
            Entities = entities.Select(entity => new ViewBuilderEntityDto
            {
                EntityId = entity.Id,
                EntityName = entity.Name,
                DisplayName = string.IsNullOrWhiteSpace(entity.Description) ? entity.Name : entity.Description,
                TableName = ResolveTableName(entity),
                HasPrimaryKey = entity.HasPrimaryKey,
                IsReadOnly = entity.IsReadOnly,
                Fields = fieldsByEntity.TryGetValue(entity.Id, out var entityFields)
                    ? entityFields.Select(MapField).ToList()
                    : new List<ViewBuilderFieldDto>()
            }).ToList(),
            JoinCandidates = graph.Edges
                .Where(edge => entityIds.Contains(edge.SourceEntityId) && entityIds.Contains(edge.TargetEntityId))
                .Select(MapJoinCandidate)
                .ToList()
        };
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ViewBuilderSqlPreviewDto> PreviewSqlAsync(PreviewViewSqlDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var project = await GetProjectAsync(input.ProjectId);
        if (!string.IsNullOrWhiteSpace(input.Sql))
        {
            ValidateManualSql(input.Sql);
            // 预览要展示"实际会执行"的 SQL：手动 SQL 里写的是实体名/属性名，落库前会被换成物理名。
            var manualEntities = await LoadEntitiesAsync(project.Id);
            var manualFields = await LoadFieldsAsync(manualEntities.Select(item => item.Id).ToArray());
            return new ViewBuilderSqlPreviewDto
            {
                ProjectId = project.Id,
                DatabaseType = project.DatabaseType,
                Sql = ViewSqlTableResolver.Resolve(input.Sql, manualEntities, manualFields),
                SelectedFields = new List<ViewBuilderSelectedFieldDto>(),
                Joins = new List<ViewBuilderJoinDto>()
            };
        }

        var entities = await LoadEntitiesAsync(project.Id);
        var entityById = entities.ToDictionary(item => item.Id);
        var selections = NormalizeSelections(input.Entities, entityById);
        var fields = await LoadFieldsAsync(selections.Select(item => item.EntityId).ToArray());
        var fieldsByEntity = fields
            .GroupBy(item => item.ModuleEntityId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var selectedFields = ResolveSelectedFields(selections, entityById, fieldsByEntity);
        var graph = await _relationGraphBuilder.BuildForProjectAsync(project.Id);
        var joins = ResolveJoins(input, selections, entityById, fieldsByEntity, graph);

        var aliases = selections
            .Select((selection, index) => new { selection.EntityId, Alias = $"t{index}" })
            .ToDictionary(item => item.EntityId, item => item.Alias);
        var selectSql = string.Join(", ", selectedFields.Select(field =>
            $"{aliases[field.EntityId]}.{QuoteIdentifier(field.ColumnName, project.DatabaseType)} AS {QuoteIdentifier(field.Alias, project.DatabaseType)}"));
        var fromSql = $"{QuoteIdentifier(ResolveTableName(entityById[selections[0].EntityId]), project.DatabaseType)} {aliases[selections[0].EntityId]}";
        var joinedEntityIds = new HashSet<long> { selections[0].EntityId };
        var remainingJoins = new List<ViewBuilderJoinDto>(joins);
        var appliedJoins = new List<ViewBuilderJoinDto>();
        var joinClauses = new List<string>();
        while (joinedEntityIds.Count < selections.Count)
        {
            var join = remainingJoins.FirstOrDefault(item =>
                joinedEntityIds.Contains(item.SourceEntityId) ^ joinedEntityIds.Contains(item.TargetEntityId));
            if (join == null)
            {
                var disconnectedEntity = selections.First(selection => !joinedEntityIds.Contains(selection.EntityId));
                var disconnectedId = disconnectedEntity.EntityId;
                joinClauses.Add(
                    $"CROSS JOIN {QuoteIdentifier(ResolveTableName(entityById[disconnectedId]), project.DatabaseType)} {aliases[disconnectedId]}");
                joinedEntityIds.Add(disconnectedId);
                continue;
            }

            var newEntityId = joinedEntityIds.Contains(join.SourceEntityId)
                ? join.TargetEntityId
                : join.SourceEntityId;
            var sourceColumn = ResolveJoinColumnName(join.SourceEntityId, join.SourceField, fieldsByEntity);
            var targetColumn = ResolveJoinColumnName(join.TargetEntityId, join.TargetField, fieldsByEntity);
            var joinKeyword = string.Equals(join.JoinType, "Inner", StringComparison.OrdinalIgnoreCase)
                ? "INNER JOIN"
                : "LEFT JOIN";
            joinClauses.Add(
                $"{joinKeyword} {QuoteIdentifier(ResolveTableName(entityById[newEntityId]), project.DatabaseType)} {aliases[newEntityId]} " +
                $"ON {aliases[join.SourceEntityId]}.{QuoteIdentifier(sourceColumn, project.DatabaseType)} = " +
                $"{aliases[join.TargetEntityId]}.{QuoteIdentifier(targetColumn, project.DatabaseType)}");
            appliedJoins.Add(join);
            remainingJoins.Remove(join);
            joinedEntityIds.Add(newEntityId);
        }

        var sql = $"SELECT {selectSql}\nFROM {fromSql}";
        if (joinClauses.Count > 0)
            sql += "\n" + string.Join("\n", joinClauses);
        return new ViewBuilderSqlPreviewDto
        {
            ProjectId = project.Id,
            DatabaseType = project.DatabaseType,
            Sql = sql.Trim(),
            SelectedFields = selectedFields,
            Joins = appliedJoins
        };
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ViewBuilderViewResultDto> CreateViewAsync(CreateViewDefinitionDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var project = await GetProjectAsync(input.ProjectId);
        ValidateViewName(input.ViewName);
        ValidateManualSql(input.Sql);

        var viewName = input.ViewName.Trim();
        // 只有真正落到数据库执行的那一步失败才算部署失败。前面的名称/替换授权守卫抛出的
        // 是"这次请求被拒"，不是"视图坏了"，不能把健康的 Applied 定义打成 Failed。
        var deploying = false;
        using var targetDb = CreateTargetDb(project);
        try
        {
            var existingView = targetDb.DbMaintenance
                .GetViewInfoList(false)
                .Any(item => string.Equals(item.Name, viewName, StringComparison.OrdinalIgnoreCase));
            if (!existingView && targetDb.DbMaintenance.IsAnyTable(viewName, false))
                throw new InvalidOperationException($"数据库对象 {viewName} 已存在且不是视图。");
            if (existingView && !input.ReplaceExistingView)
                throw new InvalidOperationException($"视图 {viewName} 已存在，当前定义未授权替换。");

            // 设计期 SQL 用实体名 + 属性名（FROM Project p / p.ProjectName），生成项目的物理表/列
            // 是 engineering_management__project / project_name。SQLite 的 CREATE VIEW 延迟解析，
            // 不转换就会建出一个查询即报 no such table / no such column 的坏视图。
            var viewEntities = await LoadEntitiesAsync(project.Id);
            var viewFields = await LoadFieldsAsync(viewEntities.Select(item => item.Id).ToArray());
            var executableSql = ViewSqlTableResolver.Resolve(
                input.Sql.Trim().TrimEnd(';').TrimEnd(),
                viewEntities,
                viewFields);
            var statements = BuildCreateViewStatements(project.DatabaseType, viewName, executableSql, existingView);
            var transactionalReplace = existingView && statements.Count > 1;
            deploying = true;
            if (transactionalReplace)
                targetDb.Ado.BeginTran();
            List<ViewBuilderViewColumnDto> columns;
            try
            {
                foreach (var statement in statements)
                    targetDb.Ado.ExecuteCommand(statement);

                // SQLite 对 CREATE VIEW 只做语法解析，FROM 里的表名和列引用都是延迟到查询时才
                // 校验，所以一条"执行成功"的 DDL 完全可能建出一个查询即报错的坏视图。列信息读取
                // 必须放在提交之前，否则替换失败时旧视图已经被 DROP，坏定义还会被提交下去。
                ProbeView(targetDb, viewName, project.DatabaseType);
                columns = ReadViewColumns(targetDb, viewName, project.DatabaseType);

                if (transactionalReplace)
                    targetDb.Ado.CommitTran();
            }
            catch
            {
                if (transactionalReplace)
                {
                    targetDb.Ado.RollbackTran();
                }
                else if (!existingView)
                {
                    // 没有事务可回滚：新建失败时清掉可能残留的坏视图，不留垃圾对象。
                    TryDropView(targetDb, viewName, project.DatabaseType);
                }
                throw;
            }

            var expectedColumns = input.ExpectedColumns
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var actualNames = columns.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedNames = expectedColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missingColumns = expectedColumns.Where(item => !actualNames.Contains(item)).ToList();
            var extraColumns = columns.Select(item => item.Name).Where(item => !expectedNames.Contains(item)).ToList();
            var requiresColumnConfirmation = expectedColumns.Count > 0 && !expectedNames.SetEquals(actualNames);
            var warnings = new List<string>();
            if (requiresColumnConfirmation)
            {
                warnings.Add(
                    $"The database view was created, but its reported columns differ from the declared entity mapping. " +
                    $"Missing: {string.Join(", ", missingColumns)}; extra: {string.Join(", ", extraColumns)}. " +
                    "CodeMaster will trust the declared mapping when registering the entity.");
            }

            // 视图列名是实体字段 ColumnName 的契约。设计期 SQL 里的 AS 别名与字段的
            // 自动 snake_case 推导经常对不上（ContractIncome vs contract_income），
            // 建完视图就按实际列名回填一次，避免实体一查询就 no such column。
            await SynchronizeViewEntityFieldsAsync(project, viewName, columns);

            // 视图真正建成功之后，把对应的受管定义推进到 Applied。定义行只在
            // ProjectViewDefinitionService 的部署路径里推进，而视图构建器是另一条路；
            // 历史失败留下的 Failed 会永久卡住 GeneratedViewMigrationExporter 的筛选
            // （它只接受 DefinitionOnly/Applied/Drifted），生成的 Migrator 就永远是空的。
            await ReconcileViewDefinitionAsync(project.Id, viewName, null);

            return new ViewBuilderViewResultDto
            {
                ProjectId = project.Id,
                DatabaseType = project.DatabaseType,
                ViewName = viewName,
                Sql = input.Sql,
                ReplacedExistingView = existingView,
                RequiresColumnConfirmation = requiresColumnConfirmation,
                ExpectedColumns = expectedColumns,
                MissingColumns = missingColumns,
                ExtraColumns = extraColumns,
                Warnings = warnings,
                Columns = columns
            };
        }
        catch (Exception ex)
        {
            if (deploying)
                await ReconcileViewDefinitionAsync(project.Id, viewName, ex.Message);
            throw;
        }
        finally
        {
            targetDb.Ado.Close();
        }
    }

    /// <summary>
    /// 把视图构建器的执行结果回写到受管视图定义。
    /// <paramref name="error"/> 为 null 表示本次建视图成功，定义推进到 Applied 并刷新
    /// <see cref="ProjectViewDefinition.AppliedDefinitionHash"/>；否则标记 Failed 并记录原因。
    /// 没有对应定义行（即临时/非受管视图）时静默跳过。
    /// </summary>
    private async Task ReconcileViewDefinitionAsync(long projectId, string viewName, string? error)
    {
        if (string.IsNullOrWhiteSpace(viewName))
            return;

        try
        {
            var definition = await _db.Queryable<ProjectViewDefinition>()
                .Where(item => item.ProjectId == projectId &&
                               item.ViewName == viewName &&
                               !item.IsDeleted)
                .OrderBy(item => item.Id, OrderByType.Desc)
                .FirstAsync();
            if (definition is null || definition.Ownership != ProjectViewOwnership.Managed)
                return;

            if (error is null)
            {
                definition.DeploymentState = ProjectViewDeploymentState.Applied;
                definition.AppliedDefinitionHash = definition.DefinitionHash;
                definition.LastDeployedAt = DateTime.UtcNow;
                definition.LastError = null;
            }
            else
            {
                definition.DeploymentState = ProjectViewDeploymentState.Failed;
                definition.LastError = error.Length <= 2000 ? error : error[..2000];
            }

            await _db.Updateable(definition)
                .UpdateColumns(item => new
                {
                    item.DeploymentState,
                    item.AppliedDefinitionHash,
                    item.LastDeployedAt,
                    item.LastError
                })
                .ExecuteCommandAsync();
        }
        catch (Exception reconcileError)
        {
            // 状态回写失败不能影响建视图本身的结果。
            Console.Error.WriteLine(
                $"[ViewBuilder] reconcile definition '{viewName}' failed: {reconcileError.Message}");
        }
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ReadOnlyViewEntityResultDto> RegisterReadOnlyViewEntityAsync(RegisterReadOnlyViewEntityDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var project = await GetProjectAsync(input.ProjectId);
        ValidateViewName(input.ViewName);
        ValidateEntityName(input.EntityName);
        if (input.ModuleId <= 0)
            throw new ArgumentException("ModuleId must be positive.", nameof(input));

        ProjectViewDefinition? viewDefinition = null;
        var definitionIsUnapplied = false;
        if (input.ViewDefinitionId.HasValue)
        {
            viewDefinition = await _db.Queryable<ProjectViewDefinition>()
                .Where(item => item.Id == input.ViewDefinitionId.Value && item.ProjectId == project.Id && !item.IsDeleted)
                .FirstAsync()
                ?? throw new InvalidOperationException("The view definition was not found in the current project.");
            if (!string.Equals(viewDefinition.ViewName, input.ViewName.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The registration view name does not match the persisted view definition.");
            definitionIsUnapplied = viewDefinition.DeploymentState == ProjectViewDeploymentState.DefinitionOnly &&
                                     input.AllowUnappliedDefinition;
            if (!definitionIsUnapplied &&
                (viewDefinition.DeploymentState is not (ProjectViewDeploymentState.Applied or ProjectViewDeploymentState.Drifted) ||
                 !string.Equals(viewDefinition.AppliedDefinitionHash, viewDefinition.DefinitionHash, StringComparison.OrdinalIgnoreCase)))
            {
                if (!definitionIsUnapplied)
                    throw new InvalidOperationException("Deploy the current view definition before registering a read-only consumer.");
            }
            if (viewDefinition.ModuleEntityId.HasValue != viewDefinition.ProjectDataSourceId.HasValue)
                throw new InvalidOperationException("The view definition has an incomplete read-only consumer binding.");
        }

        var module = await _db.Queryable<ProjectModule>()
            .Where(item => item.Id == input.ModuleId && !item.IsDeleted && item.ProjectId == project.Id)
            .FirstAsync() ?? throw new InvalidOperationException($"Module {input.ModuleId} was not found in project {project.Id}.");

        using var targetDb = CreateTargetDb(project);
        try
        {
            var viewName = input.ViewName.Trim();
            var viewExists = targetDb.DbMaintenance.GetViewInfoList(false)
                .Any(item => string.Equals(item.Name, viewName, StringComparison.OrdinalIgnoreCase));
            if (!viewExists && !definitionIsUnapplied)
                throw new InvalidOperationException($"View {viewName} does not exist in the target database.");

            var columns = viewExists ? ReadViewColumns(targetDb, viewName, project.DatabaseType) : [];
            var declaredFields = input.Fields ?? new List<ViewEntityFieldDefinitionDto>();
            if (columns.Count == 0 && declaredFields.Count == 0)
                throw new InvalidOperationException($"View {viewName} has no readable columns.");
            var mappedFields = declaredFields.Count > 0
                ? BuildDeclaredViewFields(declaredFields, input.KeyColumn)
                : BuildInferredViewFields(columns, input.KeyColumn, viewName);
            var key = mappedFields.Single(item => item.IsPrimaryKey);

            var existing = await _db.Queryable<ModuleEntity>()
                .Where(item => !item.IsDeleted && item.ProjectId == project.Id &&
                               (item.Name == input.EntityName || item.TableName == viewName))
                .FirstAsync();
            if (existing != null)
            {
                // A read-only entity carrying both the planned name and the planned view table is this
                // view's own consumer, whichever attempt created it. When the definition has no
                // consumer bound yet, neither the plan nor the definition can point at it — which is
                // exactly the state left behind when an earlier attempt registered the consumer and a
                // later reset removed the definition. The generic "is it already ours?" test below
                // answers no for that state and refuses, so the same task fails on every retry. Adopt
                // the orphan instead of rejecting it. Anything less specific than a full name+table
                // match still throws.
                var isExactConsumer = existing.IsReadOnly &&
                                      string.Equals(existing.Name, input.EntityName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(existing.TableName, viewName, StringComparison.OrdinalIgnoreCase);
                var bindsToThisConsumer = input.ExistingEntityId == existing.Id ||
                                          viewDefinition?.ModuleEntityId == existing.Id;
                // The consumer an earlier attempt left behind without linking it to the definition.
                // Its table name may still be the module-prefixed one from the entity stage, so an
                // exact name+table match is too strict here: a read-only entity carrying the planned
                // name, with the definition still unbound, is this view's consumer and the update
                // path below repairs its table name to the view name.
                var adoptableOrphan = viewDefinition?.ModuleEntityId == null &&
                                      existing.IsReadOnly &&
                                      string.Equals(existing.Name, input.EntityName.Trim(), StringComparison.OrdinalIgnoreCase);

                // A consumer bound to this very definition — or named explicitly by the caller — is this
                // view's own consumer even when its physical view name no longer matches, because the
                // update path below renames it. Requiring an exact name+table match here made a retry
                // fail forever once an attempt had registered the consumer under an older view name
                // ("实体 X 或视图 Y 已注册。"). A same-named entity that belongs to another view still throws.
                if (viewDefinition == null || (!isExactConsumer && !bindsToThisConsumer && !adoptableOrphan))
                    throw new InvalidOperationException($"实体 {existing.Name} 或视图 {viewName} 已注册。");

                var existingSource = await _db.Queryable<ProjectDataSource>()
                    .Where(item => item.ProjectId == project.Id && item.EntityId == existing.Id &&
                                   item.SourceType == PageDataSourceType.View && !item.IsDeleted)
                    .FirstAsync();
                if (viewDefinition.ModuleEntityId == existing.Id &&
                    existingSource != null &&
                    viewDefinition.ProjectDataSourceId == existingSource.Id)
                {
                    // Re-registering an already linked consumer used to return straight away, which
                    // made the second run of a view task a no-op: a definition that had just been
                    // corrected — a numeric projection retyped from string, a renamed output — never
                    // reached the entity fields it is the source of truth for. Converge on every run
                    // instead; when nothing changed this rewrites the same values.
                    await SynchronizeRestoredViewFieldsAsync(existing, mappedFields, existing.TenantId);

                    var linkedKey = mappedFields.Single(item => item.IsPrimaryKey);
                    return new ReadOnlyViewEntityResultDto
                    {
                        EntityId = existing.Id,
                        DataSourceId = existingSource.Id,
                        ProjectId = project.Id,
                        ModuleId = existing.ModuleId,
                        EntityName = existing.Name,
                        ViewName = viewName,
                        KeyColumn = linkedKey.ColumnName,
                        HasTenant = existing.HasTenant,
                        HasDataPermission = existing.HasDataPermission,
                        GenerateFrontend = existing.GenerateFrontend,
                        Columns = columns
                    };
                }

                var adoptedHasTenant = mappedFields.Any(item => IsSemanticColumn(item.Name, "TenantId"));
                var adoptedHasDataPermission = mappedFields.Any(item => IsSemanticColumn(item.Name, "DeptId"));
                existing.ModuleId = module.Id;
                existing.Description = string.IsNullOrWhiteSpace(input.DisplayName) ? input.EntityName.Trim() : input.DisplayName.Trim();
                existing.TableName = viewName;
                existing.GenerationMode = EntityGenerationMode.FullStack;
                existing.HasPrimaryKey = true;
                existing.IsReadOnly = true;
                existing.HasTenant = adoptedHasTenant;
                existing.HasDataPermission = adoptedHasDataPermission;
                existing.HasAudit = false;
                existing.HasSoftDelete = false;
                existing.GenerateFrontend = input.GenerateFrontend;
                existing.IsChildTable = false;
                existing.IsGenerated = false;
                existing.UpdateBy = "CodeMaster";
                existing.UpdateUserId = 0;
                existing.UpdateTime = DateTime.UtcNow;

                long existingSourceId;
                _db.Ado.BeginTran();
                try
                {
                    await _db.Updateable(existing)
                        .IgnoreColumns(item => new { item.CreateBy, item.CreateUserId, item.CreateTime })
                        .ExecuteCommandAsync();
                    await SynchronizeRestoredViewFieldsAsync(existing, mappedFields, project.TenantId);
                    existingSourceId = await RestoreOrCreateViewDataSourceAsync(existing, input, viewName);
                    if ((viewDefinition.ModuleEntityId.HasValue && viewDefinition.ModuleEntityId.Value != existing.Id) ||
                        (viewDefinition.ProjectDataSourceId.HasValue && viewDefinition.ProjectDataSourceId.Value != existingSourceId))
                        throw new InvalidOperationException("视图定义已经关联了另一个只读消费者。");

                    if (!viewDefinition.ModuleEntityId.HasValue)
                    {
                        var linked = await _db.Updateable<ProjectViewDefinition>()
                            .SetColumns(item => new ProjectViewDefinition
                            {
                                ModuleEntityId = existing.Id,
                                ProjectDataSourceId = existingSourceId,
                                UpdateBy = "CodeMaster",
                                UpdateUserId = 0,
                                UpdateTime = DateTime.UtcNow
                            })
                            .Where(item => item.Id == viewDefinition.Id && item.DefinitionHash == viewDefinition.DefinitionHash &&
                                           item.ModuleEntityId == null && item.ProjectDataSourceId == null)
                            .ExecuteCommandAsync();
                        if (linked == 0)
                            throw new InvalidOperationException("The view definition changed or was registered concurrently.");
                    }
                    _db.Ado.CommitTran();
                }
                catch
                {
                    _db.Ado.RollbackTran();
                    throw;
                }

                var existingKey = mappedFields.Single(item => item.IsPrimaryKey);
                return new ReadOnlyViewEntityResultDto
                {
                    EntityId = existing.Id,
                    DataSourceId = existingSourceId,
                    ProjectId = project.Id,
                    ModuleId = existing.ModuleId,
                    EntityName = existing.Name,
                    ViewName = viewName,
                    KeyColumn = existingKey.ColumnName,
                    HasTenant = existing.HasTenant,
                    HasDataPermission = existing.HasDataPermission,
                    GenerateFrontend = existing.GenerateFrontend,
                    Columns = columns
                };
            }

            var deleted = viewDefinition == null
                ? null
                : await _db.Queryable<ModuleEntity>()
                    .ClearFilter()
                    .Where(item => item.IsDeleted && item.ProjectId == project.Id && item.Name == input.EntityName)
                    .FirstAsync();
            if (deleted != null)
            {
                var durableDefinition = viewDefinition!;
                if (!deleted.IsReadOnly)
                    throw new InvalidOperationException($"已删除的同名实体 {deleted.Name} 不是只读视图实体，不能自动恢复。");

                var restoredHasTenant = mappedFields.Any(item => IsSemanticColumn(item.Name, "TenantId"));
                var restoredHasDataPermission = mappedFields.Any(item => IsSemanticColumn(item.Name, "DeptId"));
                deleted.ModuleId = module.Id;
                deleted.Description = string.IsNullOrWhiteSpace(input.DisplayName) ? input.EntityName.Trim() : input.DisplayName.Trim();
                deleted.TableName = viewName;
                deleted.GenerationMode = EntityGenerationMode.FullStack;
                deleted.HasPrimaryKey = true;
                deleted.IsReadOnly = true;
                deleted.HasTenant = restoredHasTenant;
                deleted.HasDataPermission = restoredHasDataPermission;
                deleted.HasAudit = false;
                deleted.HasSoftDelete = false;
                deleted.GenerateFrontend = input.GenerateFrontend;
                deleted.IsChildTable = false;
                deleted.IsGenerated = false;
                deleted.IsDeleted = false;
                deleted.DeleteBy = null;
                deleted.DeleteUserId = null;
                deleted.DeleteTime = null;
                deleted.UpdateBy = "CodeMaster";
                deleted.UpdateUserId = 0;
                deleted.UpdateTime = DateTime.UtcNow;

                long restoredDataSourceId;
                _db.Ado.BeginTran();
                try
                {
                    await _db.Updateable(deleted)
                        .IgnoreColumns(item => new { item.CreateBy, item.CreateUserId, item.CreateTime })
                        .ExecuteCommandAsync();
                    await SynchronizeRestoredViewFieldsAsync(deleted, mappedFields, project.TenantId);
                    restoredDataSourceId = await RestoreOrCreateViewDataSourceAsync(deleted, input, viewName);

                    if ((durableDefinition.ModuleEntityId.HasValue && durableDefinition.ModuleEntityId.Value != deleted.Id) ||
                        (durableDefinition.ProjectDataSourceId.HasValue && durableDefinition.ProjectDataSourceId.Value != restoredDataSourceId))
                        throw new InvalidOperationException("视图定义已经关联了另一个只读消费者。");

                    if (!durableDefinition.ModuleEntityId.HasValue)
                    {
                        var linked = await _db.Updateable<ProjectViewDefinition>()
                            .SetColumns(item => new ProjectViewDefinition
                            {
                                ModuleEntityId = deleted.Id,
                                ProjectDataSourceId = restoredDataSourceId,
                                UpdateBy = "CodeMaster",
                                UpdateUserId = 0,
                                UpdateTime = DateTime.UtcNow
                            })
                            .Where(item => item.Id == durableDefinition.Id && item.DefinitionHash == durableDefinition.DefinitionHash &&
                                           item.ModuleEntityId == null && item.ProjectDataSourceId == null)
                            .ExecuteCommandAsync();
                        if (linked == 0)
                            throw new InvalidOperationException("The view definition changed or was registered concurrently.");
                    }

                    _db.Ado.CommitTran();
                }
                catch
                {
                    _db.Ado.RollbackTran();
                    throw;
                }

                return new ReadOnlyViewEntityResultDto
                {
                    EntityId = deleted.Id,
                    DataSourceId = restoredDataSourceId,
                    ProjectId = project.Id,
                    ModuleId = deleted.ModuleId,
                    EntityName = deleted.Name,
                    ViewName = viewName,
                    KeyColumn = key.ColumnName,
                    HasTenant = deleted.HasTenant,
                    HasDataPermission = deleted.HasDataPermission,
                    GenerateFrontend = deleted.GenerateFrontend,
                    Columns = columns
                };
            }

            var hasTenant = mappedFields.Any(item => IsSemanticColumn(item.Name, "TenantId"));
            var hasDataPermission = mappedFields.Any(item => IsSemanticColumn(item.Name, "DeptId"));
            var entity = new ModuleEntity
            {
                ProjectId = project.Id,
                ModuleId = module.Id,
                Name = input.EntityName.Trim(),
                Description = string.IsNullOrWhiteSpace(input.DisplayName) ? input.EntityName.Trim() : input.DisplayName.Trim(),
                TableName = viewName,
                HasPrimaryKey = true,
                IsReadOnly = true,
                HasTenant = hasTenant,
                HasDataPermission = hasDataPermission,
                HasAudit = false,
                HasSoftDelete = false,
                GenerateFrontend = input.GenerateFrontend,
                IsChildTable = false,
                IsGenerated = false,
                OrderNum = 0,
                TenantId = project.TenantId,
                CreateBy = "CodeMaster",
                CreateUserId = 0,
                UpdateUserId = 0,
                CreateTime = DateTime.UtcNow
            };

            long registeredDataSourceId;
            _db.Ado.BeginTran();
            try
            {
                entity.Id = await _db.Insertable(entity).ExecuteReturnSnowflakeIdAsync();
                foreach (var mapped in mappedFields)
                {
                    var field = new EntityField
                    {
                        ModuleEntityId = entity.Id,
                        Name = mapped.Name,
                        Description = mapped.DisplayName,
                        ColumnName = mapped.ColumnName,
                        DataType = mapped.IsPrimaryKey ? "long" : mapped.DataType,
                        IsNullable = mapped.IsPrimaryKey ? false : mapped.IsNullable,
                        MaxLength = mapped.MaxLength,
                        Precision = mapped.Precision,
                        Scale = mapped.Scale,
                        IsRequired = mapped.IsPrimaryKey || !mapped.IsNullable,
                        IsSystemField = mapped.IsPrimaryKey || IsSemanticColumn(mapped.Name, "TenantId") || IsSemanticColumn(mapped.Name, "DeptId"),
                        IsPrimaryKey = mapped.IsPrimaryKey,
                        ShowInList = mapped.ShowInList,
                        ShowInDetail = mapped.ShowInDetail,
                        ShowInAddForm = false,
                        ShowInEditForm = false,
                        ShowInSearch = mapped.ShowInSearch,
                        IsSortable = mapped.IsSortable,
                        FormControlType = mapped.FormControlType,
                        OrderNum = mapped.OrderNum,
                        TenantId = project.TenantId,
                        CreateBy = "CodeMaster",
                        CreateUserId = 0,
                        UpdateUserId = 0,
                        CreateTime = DateTime.UtcNow
                    };
                    await _db.Insertable(field).ExecuteReturnSnowflakeIdAsync();
                }
                var sourceKey = await BuildUniqueEntitySourceKeyAsync(project.Id, entity.Name);
                var dataSourceId = await _projectDataSourceService.CreateAsync(new CreateProjectDataSourceDto
                {
                    ProjectId = project.Id,
                    SourceKey = sourceKey,
                    DisplayName = entity.Description,
                    SourceType = input.DataSourceType,
                    EntityId = entity.Id,
                    ServiceName = $"{entity.Name}Service",
                    MethodName = "GetPagedListAsync",
                    DtoName = $"{entity.Name}Dto",
                DataShape = PageDataShape.Paged,
                SyncMode = PageSyncMode.Auto,
                ParamsJson = "{}",
                OrderNum = entity.OrderNum,
                IsEnabled = true,
                ViewName = input.DataSourceType == PageDataSourceType.View ? viewName : null
            });
                if (viewDefinition != null)
                {
                    var linked = await _db.Updateable<ProjectViewDefinition>()
                        .SetColumns(item => new ProjectViewDefinition
                        {
                            ModuleEntityId = entity.Id,
                            ProjectDataSourceId = dataSourceId,
                            UpdateBy = "CodeMaster",
                            UpdateUserId = 0,
                            UpdateTime = DateTime.UtcNow
                        })
                        .Where(item => item.Id == viewDefinition.Id && item.DefinitionHash == viewDefinition.DefinitionHash &&
                                       item.ModuleEntityId == null && item.ProjectDataSourceId == null)
                        .ExecuteCommandAsync();
                    if (linked == 0)
                        throw new InvalidOperationException("The view definition changed or was registered concurrently.");
                }
                _db.Ado.CommitTran();
                registeredDataSourceId = dataSourceId;
            }
            catch
            {
                _db.Ado.RollbackTran();
                throw;
            }

            return new ReadOnlyViewEntityResultDto
            {
                EntityId = entity.Id,
                DataSourceId = registeredDataSourceId,
                ProjectId = project.Id,
                ModuleId = module.Id,
                EntityName = entity.Name,
                ViewName = viewName,
                KeyColumn = key.ColumnName,
                HasTenant = hasTenant,
                HasDataPermission = hasDataPermission,
                GenerateFrontend = entity.GenerateFrontend,
                Columns = columns
            };
        }
        finally
        {
            targetDb.Ado.Close();
        }
    }

    [Permission("codegen:projectpage:view")]
    public async Task<List<ViewBuilderExistingViewDto>> GetExistingViewsAsync(long projectId)
    {
        var project = await GetProjectAsync(projectId);
        using var targetDb = CreateTargetDb(project);
        try
        {
            return targetDb.DbMaintenance.GetViewInfoList(false)
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .OrderBy(item => item.Name)
                .Select(item => new ViewBuilderExistingViewDto { ViewName = item.Name })
                .ToList();
        }
        finally
        {
            targetDb.Ado.Close();
        }
    }

    [Permission("codegen:projectpage:create")]
    public Task<ReadOnlyViewEntityResultDto> ImportExistingViewAsync(RegisterReadOnlyViewEntityDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.DataSourceType = PageDataSourceType.View;
        return RegisterReadOnlyViewEntityAsync(input);
    }

    private async Task SynchronizeViewEntityFieldsAsync(
        Project project,
        string viewName,
        IReadOnlyList<ViewBuilderViewColumnDto> columns)
    {
        if (columns.Count == 0)
            return;

        var entities = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == project.Id && !item.IsDeleted && item.IsReadOnly)
            .ToListAsync();
        var targets = entities
            .Where(item => string.Equals(item.TableName, viewName, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(item.Name, viewName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (targets.Count == 0)
            return;

        var columnNames = columns.Select(item => item.Name).ToList();
        foreach (var entity in targets)
        {
            var fields = await _db.Queryable<EntityField>()
                .Where(item => item.ModuleEntityId == entity.Id && !item.IsDeleted && !item.IsIgnore)
                .ToListAsync();
            foreach (var field in fields)
            {
                var match =
                    columnNames.FirstOrDefault(name => string.Equals(name, field.ColumnName, StringComparison.OrdinalIgnoreCase)) ??
                    columnNames.FirstOrDefault(name => string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase)) ??
                    columnNames.FirstOrDefault(name => string.Equals(name, SqlSugarSetup.ToSnakeCase(field.Name), StringComparison.OrdinalIgnoreCase));
                if (match is null || string.Equals(field.ColumnName, match, StringComparison.Ordinal))
                    continue;

                field.ColumnName = match;
                field.UpdateBy = "CodeMaster";
                field.UpdateUserId = 0;
                field.UpdateTime = DateTime.UtcNow;
                await _db.Updateable(field)
                    .IgnoreColumns(item => new { item.ModuleEntityId, item.TenantId, item.CreateBy, item.CreateUserId, item.CreateTime })
                    .ExecuteCommandAsync();
            }
        }
    }

    private async Task<Project> GetProjectAsync(long projectId)
    {
        if (projectId <= 0)
            throw new ArgumentException("ProjectId must be positive.", nameof(projectId));
        return await _db.Queryable<Project>()
                   .Where(item => item.Id == projectId && !item.IsDeleted)
                   .FirstAsync()
               ?? throw new InvalidOperationException($"Project {projectId} was not found.");
    }

    private async Task<List<ModuleEntity>> LoadEntitiesAsync(long projectId)
        => await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted &&
                           item.GenerationMode == EntityGenerationMode.FullStack)
            .OrderBy(item => item.OrderNum)
            .OrderBy(item => item.Id)
            .ToListAsync();

    private async Task<List<EntityField>> LoadFieldsAsync(long[] entityIds)
    {
        if (entityIds.Length == 0) return new List<EntityField>();
        return await _db.Queryable<EntityField>()
            .Where(item => entityIds.Contains(item.ModuleEntityId) && !item.IsDeleted && !item.IsIgnore)
            .Where(item => item.FieldCategory != "Computed" && item.FieldCategory != "Aggregate")
            .OrderBy(item => item.ModuleEntityId)
            .OrderBy(item => item.OrderNum)
            .OrderBy(item => item.Id)
            .ToListAsync();
    }

    private static ViewBuilderFieldDto MapField(EntityField field)
        => new()
        {
            FieldId = field.Id,
            EntityId = field.ModuleEntityId,
            Name = field.Name,
            DisplayName = string.IsNullOrWhiteSpace(field.Description) ? field.Name : field.Description,
            ColumnName = ResolveColumnName(field),
            DataType = field.DataType,
            IsNullable = field.IsNullable,
            IsPrimaryKey = field.IsPrimaryKey,
            IsSystemField = field.IsSystemField,
            FieldCategory = field.FieldCategory,
            OrderNum = field.OrderNum
        };

    private static string ResolveColumnName(EntityField field)
        => string.IsNullOrWhiteSpace(field.ColumnName)
            ? SqlSugarSetup.ToSnakeCase(field.Name)
            : field.ColumnName.Trim();

    private async Task<string> BuildUniqueEntitySourceKeyAsync(long projectId, string entityName)
    {
        var baseKey = CodeGenNaming.ToPathSegment(entityName);
        if (string.IsNullOrWhiteSpace(baseKey))
            baseKey = "view";
        var sourceKey = baseKey;
        var suffix = 2;
        while (await _db.Queryable<ProjectDataSource>()
                   .ClearFilter()
                   .Where(item => item.ProjectId == projectId && item.SourceKey == sourceKey)
                   .AnyAsync())
        {
            sourceKey = $"{baseKey}-{suffix++}";
        }
        return sourceKey;
    }

    private async Task SynchronizeRestoredViewFieldsAsync(
        ModuleEntity entity,
        IReadOnlyList<MappedViewField> mappedFields,
        long tenantId)
    {
        var persisted = await _db.Queryable<EntityField>()
            .ClearFilter()
            .Where(item => item.ModuleEntityId == entity.Id)
            .OrderBy(item => item.OrderNum)
            .OrderBy(item => item.Id)
            .ToListAsync();
        var unmatched = new List<EntityField>(persisted);
        foreach (var mapped in mappedFields)
        {
            var field = unmatched.FirstOrDefault(item =>
                            !string.IsNullOrWhiteSpace(item.ColumnName) &&
                            string.Equals(item.ColumnName, mapped.ColumnName, StringComparison.OrdinalIgnoreCase))
                        ?? unmatched.FirstOrDefault(item =>
                            string.Equals(item.Name, mapped.Name, StringComparison.OrdinalIgnoreCase));
            if (field == null)
            {
                field = new EntityField
                {
                    ModuleEntityId = entity.Id,
                    TenantId = tenantId,
                    CreateBy = "CodeMaster",
                    CreateUserId = 0,
                    UpdateUserId = 0,
                    CreateTime = DateTime.UtcNow
                };
                ApplyMappedViewField(field, mapped);
                await _db.Insertable(field).ExecuteReturnSnowflakeIdAsync();
                continue;
            }

            unmatched.Remove(field);
            ApplyMappedViewField(field, mapped);
            field.UpdateBy = "CodeMaster";
            field.UpdateUserId = 0;
            field.UpdateTime = DateTime.UtcNow;
            await _db.Updateable(field)
                .IgnoreColumns(item => new { item.ModuleEntityId, item.TenantId, item.CreateBy, item.CreateUserId, item.CreateTime })
                .ExecuteCommandAsync();
        }

        var removedIds = unmatched.Where(item => !item.IsDeleted).Select(item => item.Id).ToList();
        if (removedIds.Count > 0)
        {
            await _db.Updateable<EntityField>()
                .SetColumns(item => new EntityField
                {
                    IsDeleted = true,
                    DeleteBy = "CodeMaster",
                    DeleteUserId = 0,
                    DeleteTime = DateTime.UtcNow,
                    UpdateBy = "CodeMaster",
                    UpdateUserId = 0,
                    UpdateTime = DateTime.UtcNow
                })
                .Where(item => removedIds.Contains(item.Id))
                .ExecuteCommandAsync();
        }
    }

    private async Task<long> RestoreOrCreateViewDataSourceAsync(
        ModuleEntity entity,
        RegisterReadOnlyViewEntityDto input,
        string viewName)
    {
        var candidates = await _db.Queryable<ProjectDataSource>()
            .ClearFilter()
            .Where(item => item.ProjectId == entity.ProjectId &&
                           (item.EntityId == entity.Id || item.ViewName == viewName))
            .OrderBy(item => item.Id)
            .ToListAsync();
        var source = candidates.FirstOrDefault(item => item.EntityId == entity.Id)
                     ?? candidates.FirstOrDefault(item =>
                         string.Equals(item.ViewName, viewName, StringComparison.OrdinalIgnoreCase));
        if (source != null && source.EntityId.HasValue && source.EntityId.Value != entity.Id)
            throw new InvalidOperationException($"视图 {viewName} 已关联到另一个数据源实体。");

        var sourceKey = source?.SourceKey;
        if (string.IsNullOrWhiteSpace(sourceKey))
            sourceKey = await BuildUniqueEntitySourceKeyAsync(entity.ProjectId, entity.Name);
        var sourceInput = new UpdateProjectDataSourceDto
        {
            ProjectId = entity.ProjectId,
            SourceKey = sourceKey,
            DisplayName = entity.Description,
            SourceType = input.DataSourceType,
            EntityId = entity.Id,
            ServiceName = $"{entity.Name}Service",
            MethodName = "GetPagedListAsync",
            DtoName = $"{entity.Name}Dto",
            DataShape = PageDataShape.Paged,
            SyncMode = PageSyncMode.Auto,
            ParamsJson = "{}",
            OrderNum = entity.OrderNum,
            IsEnabled = true,
            ViewName = input.DataSourceType == PageDataSourceType.View ? viewName : null
        };
        if (source == null)
            return await _projectDataSourceService.CreateAsync(sourceInput);

        if (source.IsDeleted)
        {
            await _db.Updateable<ProjectDataSource>()
                .SetColumns(item => new ProjectDataSource
                {
                    IsDeleted = false,
                    DeleteBy = null,
                    DeleteUserId = null,
                    DeleteTime = null,
                    UpdateBy = "CodeMaster",
                    UpdateUserId = 0,
                    UpdateTime = DateTime.UtcNow
                })
                .Where(item => item.Id == source.Id && item.IsDeleted)
                .ExecuteCommandAsync();
        }
        await _projectDataSourceService.UpdateAsync(source.Id, sourceInput);
        return source.Id;
    }

    private static void ApplyMappedViewField(EntityField field, MappedViewField mapped)
    {
        field.Name = mapped.Name;
        field.Description = mapped.DisplayName;
        field.ColumnName = mapped.ColumnName;
        field.DataType = mapped.IsPrimaryKey ? "long" : mapped.DataType;
        field.IsNullable = mapped.IsPrimaryKey ? false : mapped.IsNullable;
        field.MaxLength = mapped.MaxLength;
        field.Precision = mapped.Precision;
        field.Scale = mapped.Scale;
        field.IsRequired = mapped.IsPrimaryKey || !mapped.IsNullable;
        field.IsSystemField = mapped.IsPrimaryKey || IsSemanticColumn(mapped.Name, "TenantId") || IsSemanticColumn(mapped.Name, "DeptId");
        field.IsPrimaryKey = mapped.IsPrimaryKey;
        field.ShowInList = mapped.ShowInList;
        field.ShowInDetail = mapped.ShowInDetail;
        field.ShowInAddForm = false;
        field.ShowInEditForm = false;
        field.ShowInSearch = mapped.ShowInSearch;
        field.IsSortable = mapped.IsSortable;
        field.FormControlType = mapped.FormControlType;
        field.OrderNum = mapped.OrderNum;
        field.IsDeleted = false;
        field.DeleteBy = null;
        field.DeleteUserId = null;
        field.DeleteTime = null;
    }

    private static List<MappedViewField> BuildDeclaredViewFields(
        IReadOnlyList<ViewEntityFieldDefinitionDto> definitions,
        string? requestedKeyColumn)
    {
        var normalized = definitions.Select((definition, index) =>
        {
            var name = definition.Name?.Trim() ?? string.Empty;
            if (!Regex.IsMatch(name, "^[A-Z][A-Za-z0-9_]*$"))
                throw new ArgumentException($"View entity field name '{name}' must be an ASCII PascalCase identifier.");

            var columnName = string.IsNullOrWhiteSpace(definition.ColumnName)
                ? name
                : definition.ColumnName.Trim();
            if (columnName.Length == 0 || columnName.Length > 100)
                throw new ArgumentException($"View entity field '{name}' requires a column name of at most 100 characters.");

            var dataType = CSharpDataTypeNormalizer.Normalize(definition.DataType);
            if (!Regex.IsMatch(dataType, "^[A-Za-z_][A-Za-z0-9_.]*(?:\\[\\])?\\??$"))
                throw new ArgumentException($"View entity field '{name}' has an unsafe C# data type.");

            return new MappedViewField(
                name,
                string.IsNullOrWhiteSpace(definition.DisplayName) ? name : definition.DisplayName.Trim(),
                columnName,
                dataType,
                definition.IsNullable || dataType.EndsWith('?'),
                definition.MaxLength,
                definition.Precision,
                definition.Scale,
                false,
                definition.ShowInList,
                definition.ShowInDetail,
                definition.ShowInSearch,
                definition.IsSortable,
                string.IsNullOrWhiteSpace(definition.FormControlType)
                    ? ResolveViewControlType(dataType)
                    : definition.FormControlType.Trim(),
                definition.OrderNum == 0 ? index : definition.OrderNum);
        }).ToList();

        var duplicateProperty = normalized.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateProperty != null)
            throw new ArgumentException($"Duplicate view entity field name: {duplicateProperty.Key}.");
        var duplicateColumn = normalized.GroupBy(item => item.ColumnName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateColumn != null)
            throw new ArgumentException($"Duplicate view output column mapping: {duplicateColumn.Key}. Use distinct SQL AS aliases.");

        var keyColumn = string.IsNullOrWhiteSpace(requestedKeyColumn)
            ? normalized[0].ColumnName
            : requestedKeyColumn.Trim();
        var keyIndex = normalized.FindIndex(item =>
            string.Equals(item.ColumnName, keyColumn, StringComparison.OrdinalIgnoreCase));
        if (keyIndex < 0)
            throw new ArgumentException($"Key column {keyColumn} is not declared in the view entity fields.");
        if (!IsIntegralType(normalized[keyIndex].DataType))
            throw new ArgumentException("The declared key field must use an integral C# data type compatible with the generated long Id.");

        normalized[keyIndex] = normalized[keyIndex] with { Name = "Id", DataType = "long", IsNullable = false, IsPrimaryKey = true };
        var duplicateMappedProperty = normalized.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateMappedProperty != null)
            throw new ArgumentException($"View fields map to duplicate generated property name: {duplicateMappedProperty.Key}.");

        return normalized
            .OrderBy(item => item.IsPrimaryKey ? 0 : 1)
            .ThenBy(item => item.OrderNum)
            .Select((item, index) => item with { OrderNum = index })
            .ToList();
    }

    private static List<MappedViewField> BuildInferredViewFields(
        IReadOnlyList<ViewBuilderViewColumnDto> columns,
        string? requestedKeyColumn,
        string viewName)
    {
        var keyColumn = string.IsNullOrWhiteSpace(requestedKeyColumn)
            ? columns[0].Name
            : requestedKeyColumn.Trim();
        var key = columns.FirstOrDefault(item =>
            string.Equals(item.Name, keyColumn, StringComparison.OrdinalIgnoreCase));
        if (key is null)
            throw new InvalidOperationException($"Key column {keyColumn} does not exist on view {viewName}.");
        if (!IsIntegralType(key.DataType))
            throw new InvalidOperationException("The read-only entity key column must be integral to support the generated long Id service contract.");

        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return columns
            .OrderBy(column => string.Equals(column.Name, key.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(column => column.Ordinal)
            .Select((column, index) =>
            {
                var isKey = string.Equals(column.Name, key.Name, StringComparison.OrdinalIgnoreCase);
                var propertyName = isKey ? "Id" : ToPascalIdentifier(column.Name);
                if (!propertyNames.Add(propertyName))
                    throw new InvalidOperationException($"View columns map to duplicate entity property name: {propertyName}.");
                return new MappedViewField(
                    propertyName,
                    column.Name,
                    column.Name,
                    isKey ? "long" : column.DataType,
                    !isKey && column.IsNullable,
                    null,
                    null,
                    null,
                    isKey,
                    true,
                    true,
                    false,
                    false,
                    ResolveViewControlType(column.DataType),
                    index);
            }).ToList();
    }

    private static List<ViewBuilderViewColumnDto> ReadViewColumns(
        SqlSugarClient targetDb,
        string viewName,
        DatabaseType databaseType)
    {
        var columns = targetDb.DbMaintenance
            .GetColumnInfosByTableName(viewName, false)
            .OrderBy(item => item.CreateTableFieldSort)
            .ThenBy(item => item.DbColumnName)
            .Select((item, index) => new ViewBuilderViewColumnDto
            {
                Name = item.DbColumnName,
                DataType = MapViewColumnDataType(item),
                IsNullable = item.IsNullable,
                IsPrimaryKey = item.IsPrimarykey,
                Ordinal = index
            })
            .ToList();
        if (columns.Count > 0 || databaseType != DatabaseType.PostgreSQL)
            return columns;

        var schema = targetDb.Ado.GetDataTable(
            """
            SELECT column_name, data_type, is_nullable, ordinal_position
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = @viewName
            ORDER BY ordinal_position
            """,
            new SugarParameter("@viewName", viewName));
        return schema.Rows.Cast<global::System.Data.DataRow>()
            .Select(row => new ViewBuilderViewColumnDto
            {
                Name = Convert.ToString(row["column_name"]) ?? string.Empty,
                DataType = MapViewColumnDataType(Convert.ToString(row["data_type"])),
                IsNullable = string.Equals(Convert.ToString(row["is_nullable"]), "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimaryKey = false,
                Ordinal = Math.Max(0, Convert.ToInt32(row["ordinal_position"]) - 1)
            })
            .ToList();
    }

    private static string MapViewColumnDataType(DbColumnInfo column)
    {
        var databaseType = column.DataType?.Trim().ToLowerInvariant() ?? string.Empty;
        var mapped = MapViewColumnDataType(databaseType);
        if (mapped != "string" || databaseType.Contains("char") || databaseType.Contains("text"))
            return mapped;

        var type = Nullable.GetUnderlyingType(column.PropertyType ?? typeof(string)) ?? column.PropertyType ?? typeof(string);
        if (type == typeof(byte)) return "byte";
        if (type == typeof(short)) return "short";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(DateTimeOffset)) return "DateTimeOffset";
        if (type == typeof(Guid)) return "Guid";
        if (type == typeof(byte[])) return "byte[]";
        return "string";
    }

    private static string MapViewColumnDataType(string? dataType)
    {
        var databaseType = dataType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (databaseType.Contains("bigint") || databaseType.Contains("int8")) return "long";
        if (databaseType.Contains("smallint") || databaseType.Contains("int2")) return "short";
        if (databaseType.Contains("tinyint") || databaseType.Contains("byte")) return "byte";
        if (databaseType.Contains("int") || databaseType.Contains("integer") || databaseType.Contains("serial")) return "int";
        if (databaseType.Contains("bool")) return "bool";
        if (databaseType.Contains("decimal") || databaseType.Contains("numeric") || databaseType.Contains("money")) return "decimal";
        if (databaseType.Contains("double")) return "double";
        if (databaseType.Contains("real") || databaseType.Contains("float")) return "float";
        if (databaseType.Contains("date") && databaseType.Contains("time")) return "DateTime";
        if (databaseType.Contains("timestamp")) return "DateTime";
        if (databaseType.Contains("uuid") || databaseType.Contains("guid")) return "Guid";
        if (databaseType.Contains("blob") || databaseType.Contains("binary")) return "byte[]";
        return "string";
    }

    private static bool IsIntegralType(string dataType)
        => dataType.Trim().TrimEnd('?') is "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong";

    private static string ResolveViewControlType(string dataType)
        => dataType.Trim().TrimEnd('?') switch
        {
            "DateTime" or "DateTimeOffset" => "datetime",
            "bool" => "switch",
            "byte" or "short" or "int" or "long" or "uint" or "ulong" or "float" or "double" or "decimal" => "number",
            _ => "input"
        };

    private static bool IsSemanticColumn(string propertyName, string semanticName)
        => string.Equals(propertyName, semanticName, StringComparison.OrdinalIgnoreCase);

    private sealed record MappedViewField(
        string Name,
        string DisplayName,
        string ColumnName,
        string DataType,
        bool IsNullable,
        int? MaxLength,
        int? Precision,
        int? Scale,
        bool IsPrimaryKey,
        bool ShowInList,
        bool ShowInDetail,
        bool ShowInSearch,
        bool IsSortable,
        string FormControlType,
        int OrderNum);

    private static string ToPascalIdentifier(string columnName)
    {
        var parts = Regex.Split(columnName.Trim(), "[^A-Za-z0-9]+")
            .Where(part => part.Length > 0)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..])
            .ToList();
        if (parts.Count == 0)
            throw new ArgumentException($"Unable to map view column to an entity property: {columnName}");
        var result = string.Concat(parts);
        return char.IsDigit(result[0]) ? "Column" + result : result;
    }

    private static void ValidateEntityName(string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName) ||
            !Regex.IsMatch(entityName.Trim(), "^[A-Z][A-Za-z0-9_]*$"))
            throw new ArgumentException("EntityName must start with an uppercase letter and contain only letters, numbers, and underscores.", nameof(entityName));
    }

    private static ViewBuilderJoinCandidateDto MapJoinCandidate(EntityRelationEdge edge)
        => new()
        {
            RelationId = edge.IsLegacy ? edge.Id : edge.Id,
            SourceEntityId = edge.SourceEntityId,
            TargetEntityId = edge.TargetEntityId,
            RelationName = edge.RelationName,
            SourceField = edge.SourceField,
            TargetField = edge.TargetField,
            Cardinality = edge.Cardinality.ToString(),
            Ownership = edge.Ownership.ToString(),
            DefaultJoinType = "Left",
            IsRequired = edge.IsRequired,
            IsInferred = true
        };

    private static List<ViewBuilderEntitySelectionDto> NormalizeSelections(
        IEnumerable<ViewBuilderEntitySelectionDto>? input,
        IReadOnlyDictionary<long, ModuleEntity> entityById)
    {
        var selections = (input ?? Array.Empty<ViewBuilderEntitySelectionDto>())
            .GroupBy(item => item.EntityId)
            .Select(group => new ViewBuilderEntitySelectionDto
            {
                EntityId = group.Key,
                FieldIds = group.SelectMany(item => item.FieldIds ?? new List<long>()).Distinct().ToList()
            })
            .ToList();
        if (selections.Count == 0)
            throw new ArgumentException("At least one entity must be selected.", nameof(input));
        var unknown = selections.Select(item => item.EntityId).Where(id => !entityById.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Selected entities do not belong to the project: {string.Join(", ", unknown)}.");
        return selections;
    }

    private static List<ViewBuilderSelectedFieldDto> ResolveSelectedFields(
        IReadOnlyList<ViewBuilderEntitySelectionDto> selections,
        IReadOnlyDictionary<long, ModuleEntity> entityById,
        IReadOnlyDictionary<long, List<EntityField>> fieldsByEntity)
    {
        var result = new List<ViewBuilderSelectedFieldDto>();
        foreach (var selection in selections)
        {
            if (!fieldsByEntity.TryGetValue(selection.EntityId, out var fields) || fields.Count == 0)
                throw new InvalidOperationException($"Entity {entityById[selection.EntityId].Name} has no persisted fields.");
            var requested = selection.FieldIds.Count == 0
                ? fields.Select(item => item.Id).ToHashSet()
                : selection.FieldIds.ToHashSet();
            var selected = fields.Where(field => requested.Contains(field.Id)).ToList();
            var unknown = requested.Except(selected.Select(item => item.Id)).ToList();
            if (unknown.Count > 0)
                throw new InvalidOperationException($"Selected fields do not belong to entity {entityById[selection.EntityId].Name}: {string.Join(", ", unknown)}.");
            foreach (var field in selected)
            {
                result.Add(new ViewBuilderSelectedFieldDto
                {
                    EntityId = selection.EntityId,
                    EntityName = entityById[selection.EntityId].Name,
                    FieldId = field.Id,
                    FieldName = field.Name,
                    ColumnName = ResolveColumnName(field),
                    Alias = $"{entityById[selection.EntityId].Name}_{field.Name}"
                });
            }
        }
        return result;
    }

    private static List<ViewBuilderJoinDto> ResolveJoins(
        PreviewViewSqlDto input,
        IReadOnlyList<ViewBuilderEntitySelectionDto> selections,
        IReadOnlyDictionary<long, ModuleEntity> entityById,
        IReadOnlyDictionary<long, List<EntityField>> fieldsByEntity,
        EntityRelationGraph graph)
    {
        var selectedIds = selections.Select(item => item.EntityId).ToHashSet();
        var joins = new List<ViewBuilderJoinDto>();
        if (input.IncludeInferredJoins)
        {
            joins.AddRange(graph.Edges
                .Where(edge => selectedIds.Contains(edge.SourceEntityId) && selectedIds.Contains(edge.TargetEntityId))
                .Select(edge => new ViewBuilderJoinDto
                {
                    SourceEntityId = edge.SourceEntityId,
                    TargetEntityId = edge.TargetEntityId,
                    SourceField = edge.SourceField,
                    TargetField = edge.TargetField,
                    JoinType = "Left",
                    RelationName = edge.RelationName,
                    IsInferred = true
                }));
        }
        joins.AddRange(input.Joins ?? new List<ViewBuilderJoinDto>());
        joins = joins
            .GroupBy(BuildJoinKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        foreach (var join in joins)
        {
            if (!selectedIds.Contains(join.SourceEntityId) || !selectedIds.Contains(join.TargetEntityId))
                throw new InvalidOperationException("Join entities must be selected before configuring a join.");
            if (join.SourceEntityId == join.TargetEntityId)
                throw new InvalidOperationException("An entity cannot join itself.");
            if (join.JoinType is not ("Left" or "Inner"))
                throw new ArgumentException("JoinType must be Left or Inner.");
            ValidateJoinField(join.SourceEntityId, join.SourceField, entityById, fieldsByEntity);
            ValidateJoinField(join.TargetEntityId, join.TargetField, entityById, fieldsByEntity);
        }
        return joins;
    }

    private static void ValidateJoinField(
        long entityId,
        string fieldName,
        IReadOnlyDictionary<long, ModuleEntity> entityById,
        IReadOnlyDictionary<long, List<EntityField>> fieldsByEntity)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || !SafeIdentifier.IsMatch(fieldName))
            throw new ArgumentException("Join fields must be valid entity field names.");
        if (!fieldsByEntity.TryGetValue(entityId, out var fields) ||
            !fields.Any(field => string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Join field {fieldName} does not exist on entity {entityById[entityId].Name}.");
    }

    private static string BuildJoinKey(ViewBuilderJoinDto join)
        => string.Join('|', join.SourceEntityId, join.TargetEntityId,
            join.SourceField.Trim().ToUpperInvariant(), join.TargetField.Trim().ToUpperInvariant());

    private static string ResolveJoinColumnName(
        long entityId,
        string fieldName,
        IReadOnlyDictionary<long, List<EntityField>> fieldsByEntity)
    {
        var field = fieldsByEntity[entityId].First(item =>
            string.Equals(item.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        return ResolveColumnName(field);
    }

    internal static void ValidateManualSql(string sql)
    {
        var trimmed = sql.Trim();
        if (!Regex.IsMatch(trimmed, "^(SELECT|WITH)\\b", RegexOptions.IgnoreCase))
            throw new ArgumentException("手写 SQL 必须以 SELECT 或 WITH 查询开始。", nameof(sql));

        var executableText = Regex.Replace(
            trimmed,
            "'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|--[^\\r\\n]*|/\\*.*?\\*/",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTrailingTerminator = executableText.TrimEnd().TrimEnd(';').TrimEnd();
        if (withoutTrailingTerminator.Contains(';'))
            throw new ArgumentException("手写 SQL 只能包含一条查询语句。", nameof(sql));
        if (Regex.IsMatch(
                withoutTrailingTerminator,
                "\\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|TRUNCATE|MERGE|EXEC(?:UTE)?)\\b",
                RegexOptions.IgnoreCase))
            throw new ArgumentException("手写 SQL 只允许查询语句，不能包含写入或 DDL 操作。", nameof(sql));
    }

    internal static void ValidateViewName(string viewName)
    {
        if (string.IsNullOrWhiteSpace(viewName) || !SafeIdentifier.IsMatch(viewName.Trim()))
            throw new ArgumentException("ViewName 只能包含字母、数字和下划线，且必须以字母或下划线开头。", nameof(viewName));
    }

    private static SqlSugarClient CreateTargetDb(Project project)
    {
        var dbType = project.DatabaseType switch
        {
            DatabaseType.MySQL => DbType.MySql,
            DatabaseType.SqlServer => DbType.SqlServer,
            DatabaseType.PostgreSQL => DbType.PostgreSQL,
            DatabaseType.SQLite => DbType.Sqlite,
            DatabaseType.Oracle => DbType.Oracle,
            _ => throw new NotSupportedException($"Unsupported database type: {project.DatabaseType}")
        };
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = NormalizeConnectionString(project),
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(dbType)
        });
    }

    private static string NormalizeConnectionString(Project project)
    {
        if (project.DatabaseType != DatabaseType.SQLite) return project.ConnectionString;
        var connectionString = project.ConnectionString ?? string.Empty;
        var match = Regex.Match(connectionString, @"Data Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return EnsureSqlitePoolingDisabled(connectionString);
        var dataSource = match.Groups[1].Value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(dataSource) || Path.IsPathRooted(dataSource))
            return EnsureSqlitePoolingDisabled(connectionString);
        var projectPath = project.ProjectPath ?? string.Empty;
        if (!projectPath.EndsWith(project.ProjectName, StringComparison.OrdinalIgnoreCase))
            projectPath = Path.Combine(projectPath, project.ProjectName);
        var absolutePath = global::CodeMaster.Application.Services.CodeGen.ProjectService.EnsureSqliteDirectory(
            Path.GetFullPath(Path.Combine(projectPath, $"{project.ProjectName}.Migrator", dataSource)));
        connectionString = Regex.Replace(connectionString, @"Data Source\s*=\s*([^;]+)", "Data Source=" + absolutePath, RegexOptions.IgnoreCase);
        return connectionString.Contains("Pooling=", StringComparison.OrdinalIgnoreCase)
            ? connectionString
            : connectionString.TrimEnd(';') + ";Pooling=False;";
    }

    private static string EnsureSqlitePoolingDisabled(string connectionString)
        => connectionString.Contains("Pooling=", StringComparison.OrdinalIgnoreCase)
            ? connectionString
            : connectionString.TrimEnd(';') + ";Pooling=False;";

    private static string ResolveTableName(ModuleEntity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.TableName))
        {
            var explicitTableName = entity.TableName.Trim();
            if (!SafeIdentifier.IsMatch(explicitTableName))
                throw new ArgumentException($"Unsafe database table identifier: {explicitTableName}");
            return explicitTableName;
        }
        var snake = SqlSugarSetup.ToSnakeCase(entity.Name);
        return snake.EndsWith('s') ? snake : snake + "s";
    }

    private static string QuoteIdentifier(string value, DatabaseType databaseType)
    {
        if (!SafeIdentifier.IsMatch(value))
            throw new ArgumentException($"Unsafe database identifier: {value}");
        var quote = databaseType switch
        {
            DatabaseType.MySQL => '`',
            DatabaseType.SqlServer => '[',
            _ => '"'
        };
        var close = quote == '[' ? ']' : quote;
        return $"{quote}{value}{close}";
    }

    /// <summary>
    /// 真正执行一次视图查询，迫使数据库完成名称解析。
    /// SQLite（以及部分提供程序）的 CREATE VIEW 只校验语法，表名/列名要到查询时才解析，
    /// 不探测就无法在建视图阶段发现坏定义。<c>WHERE 1 = 0</c> 保证零行返回，各数据库方言通用。
    /// </summary>
    private static void ProbeView(SqlSugarClient targetDb, string viewName, DatabaseType databaseType)
    {
        var quoted = QuoteIdentifier(viewName, databaseType);
        targetDb.Ado.GetDataTable($"SELECT * FROM {quoted} WHERE 1 = 0");
    }

    /// <summary>新建视图失败后清理残留对象，避免在目标库里留下一个查询即报错的坏视图。</summary>
    private static void TryDropView(SqlSugarClient targetDb, string viewName, DatabaseType databaseType)
    {
        try
        {
            targetDb.Ado.ExecuteCommand($"DROP VIEW IF EXISTS {QuoteIdentifier(viewName, databaseType)}");
        }
        catch (Exception cleanupError)
        {
            Console.Error.WriteLine($"[ViewBuilder] cleanup of failed view '{viewName}' skipped: {cleanupError.Message}");
        }
    }

    private static IReadOnlyList<string> BuildCreateViewStatements(
        DatabaseType databaseType,
        string viewName,
        string executableSql,
        bool existingView)
    {
        var quotedViewName = QuoteIdentifier(viewName, databaseType);
        if (databaseType is DatabaseType.SqlServer or DatabaseType.PostgreSQL or DatabaseType.SQLite)
        {
            var statements = new List<string>();
            if (existingView)
                statements.Add($"DROP VIEW IF EXISTS {quotedViewName}");
            statements.Add($"CREATE VIEW {quotedViewName} AS {executableSql}");
            return statements;
        }
        return new[] { $"CREATE OR REPLACE VIEW {quotedViewName} AS {executableSql}" };
    }
}
