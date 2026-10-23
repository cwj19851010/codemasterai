using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class ProjectViewDefinitionService : IProjectViewDefinitionService
{
    private static readonly Regex SafeKey = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    private static readonly Regex SafeIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private readonly ISqlSugarClient _db;
    private readonly ProjectViewBuilderService _viewBuilderService;

    public ProjectViewDefinitionService(ISqlSugarClient db, ProjectViewBuilderService? viewBuilderService = null)
    {
        _db = db;
        _viewBuilderService = viewBuilderService ?? new ProjectViewBuilderService(db);
    }

    [Permission("codegen:projectpage:list")]
    public async Task<List<ProjectViewDefinitionDto>> GetListAsync(long projectId)
    {
        var project = await GetProjectAsync(projectId);
        var definitions = await _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.ProjectId == project.Id && item.TenantId == project.TenantId && !item.IsDeleted)
            .OrderBy(item => item.ViewName)
            .ToListAsync();
        return await ToDtosAsync(definitions);
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ProjectViewDefinitionDto?> GetByIdAsync(long id)
    {
        var definition = await _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync();
        if (definition == null)
            return null;
        await GetProjectAsync(definition.ProjectId);
        return (await ToDtosAsync([definition])).Single();
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ProjectViewDefinitionDto> SaveAsync(SaveProjectViewDefinitionDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var project = await GetProjectAsync(input.ProjectId);
        ValidateInput(input);
        await ValidateReferencesAsync(project, input);

        var existing = input.Id.HasValue
            ? await _db.Queryable<ProjectViewDefinition>()
                .Where(item => item.Id == input.Id.Value && item.ProjectId == project.Id && !item.IsDeleted)
                .FirstAsync()
            : null;
        if (input.Id.HasValue && existing == null)
            throw new InvalidOperationException($"视图定义 {input.Id} 不存在或不属于当前项目。");
        if (existing?.DeploymentState == ProjectViewDeploymentState.Pending)
            throw new InvalidOperationException("视图定义正在部署，完成后才能修改。");
        if (existing != null && input.ExpectedDefinitionVersion != existing.DefinitionVersion)
            throw new InvalidOperationException($"视图定义已变更，请刷新后再保存。当前版本为 {existing.DefinitionVersion}。");

        var duplicate = _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.ProjectId == project.Id && item.TenantId == project.TenantId && !item.IsDeleted &&
                           (item.DefinitionKey == input.DefinitionKey.Trim() || item.ViewName == input.ViewName.Trim()));
        if (existing != null)
            duplicate = duplicate.Where(item => item.Id != existing.Id);
        if (duplicate.Any())
            throw new InvalidOperationException("同一项目中的定义键或视图名称已经存在。");

        var normalized = BuildCanonicalDefinition(input, project.DatabaseType);
        var declaredSchemaJson = JsonSerializer.Serialize(input.Fields.OrderBy(item => item.Ordinal ?? int.MaxValue).ThenBy(item => item.OutputName));
        var entity = existing ?? new ProjectViewDefinition
        {
            ProjectId = project.Id,
            TenantId = project.TenantId,
            CreateBy = "CodeMaster",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };
        entity.DefinitionKey = input.DefinitionKey.Trim();
        entity.ViewName = input.ViewName.Trim();
        entity.SchemaName = NormalizeOptional(input.SchemaName);
        entity.DatabaseType = project.DatabaseType;
        entity.Sql = NormalizeSql(input.Sql);
        entity.SqlHash = entity.Sql == null ? null : Hash(entity.Sql);
        entity.DefinitionVersion = existing == null ? 1 : existing.DefinitionVersion + 1;
        entity.DefinitionHash = Hash(normalized);
        entity.KeyColumn = NormalizeOptional(input.KeyColumn);
        entity.Ownership = input.Ownership;
        entity.DeploymentState = input.Ownership == ProjectViewOwnership.ImportedExternal
            ? ProjectViewDeploymentState.External
            : ProjectViewDeploymentState.DefinitionOnly;
        entity.DeclaredSchemaJson = declaredSchemaJson;
        entity.DeclaredSchemaHash = Hash(declaredSchemaJson);
        entity.ActualSchemaJson = existing?.ActualSchemaJson;
        entity.ActualSchemaHash = existing?.ActualSchemaHash;
        entity.AppliedDefinitionHash = existing?.AppliedDefinitionHash;
        entity.ModuleEntityId = existing?.ModuleEntityId;
        entity.ProjectDataSourceId = existing?.ProjectDataSourceId;
        entity.AllowReplace = input.AllowReplace;
        entity.LastError = null;
        entity.UpdateBy = "CodeMaster";
        entity.UpdateUserId = 0;
        entity.UpdateTime = DateTime.UtcNow;

        _db.Ado.BeginTran();
        try
        {
            if (existing == null)
                entity.Id = await _db.Insertable(entity).ExecuteReturnSnowflakeIdAsync();
            else
                await _db.Updateable(entity)
                    .IgnoreColumns(item => new { item.IsDeleted, item.DeleteTime, item.DeleteBy, item.DeleteUserId, item.CreateTime, item.CreateBy, item.CreateUserId })
                    .ExecuteCommandAsync();

            await _db.Deleteable<ProjectViewDependency>().Where(item => item.DefinitionId == entity.Id).ExecuteCommandAsync();
            await _db.Deleteable<ProjectViewFieldLineage>().Where(item => item.DefinitionId == entity.Id).ExecuteCommandAsync();
            var dependencyIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in input.Dependencies)
            {
                var child = ToDependency(entity, dependency);
                child.Id = await _db.Insertable(child).ExecuteReturnSnowflakeIdAsync();
                dependencyIds[child.DependencyKey] = child.Id;
            }
            foreach (var field in input.Fields.OrderBy(item => item.Ordinal ?? int.MaxValue).ThenBy(item => item.OutputName))
            {
                var child = ToLineage(entity, field, dependencyIds);
                child.Id = await _db.Insertable(child).ExecuteReturnSnowflakeIdAsync();
            }
            _db.Ado.CommitTran();
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }

        // Keep definition-only work-plan views available to the generated
        // migrator. Physical deployment is intentionally deferred until the
        // final materialization pipeline has created the referenced tables.
        await ExportGeneratedViewMigrationAsync(project);

        return (await GetByIdAsync(entity.Id))!;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ProjectViewDeploymentResultDto> DeployAsync(DeployProjectViewDefinitionDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var definition = await _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.Id == input.DefinitionId && !item.IsDeleted)
            .FirstAsync()
            ?? throw new InvalidOperationException($"视图定义 {input.DefinitionId} 不存在。");
        var project = await GetProjectAsync(definition.ProjectId);
        if (definition.DefinitionVersion != input.ExpectedDefinitionVersion ||
            !string.Equals(definition.DefinitionHash, input.ExpectedDefinitionHash?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("视图定义在部署前已发生变化，请重新确认 SQL 和影响范围。");
        if (definition.Ownership != ProjectViewOwnership.Managed || string.IsNullOrWhiteSpace(definition.Sql))
            throw new InvalidOperationException("导入的外部视图不能由 CodeMaster 默认替换；请先显式转为受管定义。");
        if ((definition.DeploymentState is ProjectViewDeploymentState.Applied or ProjectViewDeploymentState.Drifted) &&
            string.Equals(definition.AppliedDefinitionHash, definition.DefinitionHash, StringComparison.OrdinalIgnoreCase))
        {
            await ExportGeneratedViewMigrationAsync(project);
            return new ProjectViewDeploymentResultDto
            {
                AlreadyApplied = true,
                Definition = (await GetByIdAsync(definition.Id))!
            };
        }

        var claimed = await _db.Updateable<ProjectViewDefinition>()
            .SetColumns(item => new ProjectViewDefinition
            {
                DeploymentState = ProjectViewDeploymentState.Pending,
                LastError = null,
                UpdateBy = "CodeMaster",
                UpdateUserId = 0,
                UpdateTime = DateTime.UtcNow
            })
            .Where(item => item.Id == definition.Id &&
                           item.DefinitionVersion == input.ExpectedDefinitionVersion &&
                           item.DefinitionHash == definition.DefinitionHash &&
                           item.DeploymentState != ProjectViewDeploymentState.Pending)
            .ExecuteCommandAsync();
        if (claimed == 0)
            throw new InvalidOperationException("视图定义正在部署或已发生变化，请刷新后重试。");

        var fields = await _db.Queryable<ProjectViewFieldLineage>()
            .Where(item => item.DefinitionId == definition.Id && !item.IsDeleted)
            .OrderBy(item => item.Ordinal)
            .ToListAsync();
        try
        {
            var databaseResult = await _viewBuilderService.CreateViewAsync(new CreateViewDefinitionDto
            {
                ProjectId = definition.ProjectId,
                ViewName = definition.ViewName,
                Sql = definition.Sql,
                ExpectedColumns = fields.Select(item => item.OutputColumn).ToList(),
                ReplaceExistingView = definition.AllowReplace
            });
            var actualSchemaJson = JsonSerializer.Serialize(databaseResult.Columns);
            var updated = await _db.Updateable<ProjectViewDefinition>()
                .SetColumns(item => new ProjectViewDefinition
                {
                    DeploymentState = databaseResult.RequiresColumnConfirmation
                        ? ProjectViewDeploymentState.Drifted
                        : ProjectViewDeploymentState.Applied,
                    ActualSchemaJson = actualSchemaJson,
                    ActualSchemaHash = Hash(actualSchemaJson),
                    AppliedDefinitionHash = definition.DefinitionHash,
                    LastDeployedAt = DateTime.UtcNow,
                    LastInspectedAt = DateTime.UtcNow,
                    LastError = null,
                    UpdateBy = "CodeMaster",
                    UpdateUserId = 0,
                    UpdateTime = DateTime.UtcNow
                })
                .Where(item => item.Id == definition.Id && item.DefinitionVersion == input.ExpectedDefinitionVersion &&
                               item.DefinitionHash == definition.DefinitionHash && item.DeploymentState == ProjectViewDeploymentState.Pending)
                .ExecuteCommandAsync();
            if (updated == 0)
                throw new InvalidOperationException("视图已部署，但定义状态发生并发变化，需要重新检查实际数据库架构。");
            await ExportGeneratedViewMigrationAsync(project);
            return new ProjectViewDeploymentResultDto
            {
                DatabaseResult = databaseResult,
                Definition = (await GetByIdAsync(definition.Id))!
            };
        }
        catch (Exception ex)
        {
            var errorMessage = ex.Message.Length <= 2000 ? ex.Message : ex.Message.Substring(0, 2000);
            await _db.Updateable<ProjectViewDefinition>()
                .SetColumns(item => new ProjectViewDefinition
                {
                    DeploymentState = ProjectViewDeploymentState.Failed,
                    LastError = errorMessage,
                    LastInspectedAt = DateTime.UtcNow,
                    UpdateBy = "CodeMaster",
                    UpdateUserId = 0,
                    UpdateTime = DateTime.UtcNow
                })
                .Where(item => item.Id == definition.Id)
                .ExecuteCommandAsync();
            throw;
        }
    }

    private Task ExportGeneratedViewMigrationAsync(Project project)
        => GeneratedViewMigrationExporter.ExportForProjectAsync(_db, project.Id);

    [Permission("codegen:projectpage:delete")]
    public async Task<int> DeleteAsync(long id)
    {
        var definition = await _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync();
        if (definition == null)
            return 0;

        var project = await GetProjectAsync(definition.ProjectId);
        if (definition.DeploymentState == ProjectViewDeploymentState.Pending)
            throw new InvalidOperationException("视图定义正在部署，完成后才能删除。");

        var referenced = await _db.Queryable<ProjectViewDependency>()
            .Where(item => item.SourceViewDefinitionId == definition.Id && !item.IsDeleted)
            .AnyAsync();
        if (referenced)
            throw new InvalidOperationException("该视图仍被其他受管视图依赖，请先删除或调整依赖视图。");

        var now = DateTime.UtcNow;
        _db.Ado.BeginTran();
        try
        {
            var affected = await _db.Updateable<ProjectViewDefinition>()
                .SetColumns(item => new ProjectViewDefinition
                {
                    IsDeleted = true,
                    DeleteTime = now,
                    DeleteBy = "CodeMaster"
                })
                .Where(item => item.Id == definition.Id && !item.IsDeleted)
                .ExecuteCommandAsync();
            await _db.Updateable<ProjectViewDependency>()
                .SetColumns(item => new ProjectViewDependency { IsDeleted = true, DeleteTime = now, DeleteBy = "CodeMaster" })
                .Where(item => item.DefinitionId == definition.Id && !item.IsDeleted)
                .ExecuteCommandAsync();
            await _db.Updateable<ProjectViewFieldLineage>()
                .SetColumns(item => new ProjectViewFieldLineage { IsDeleted = true, DeleteTime = now, DeleteBy = "CodeMaster" })
                .Where(item => item.DefinitionId == definition.Id && !item.IsDeleted)
                .ExecuteCommandAsync();
            _db.Ado.CommitTran();

            if (affected > 0)
                await ExportGeneratedViewMigrationAsync(project);
            return affected;
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ProjectViewSchemaDiffDto> GetSchemaDiffAsync(long definitionId)
    {
        var definition = await _db.Queryable<ProjectViewDefinition>()
            .Where(item => item.Id == definitionId && !item.IsDeleted)
            .FirstAsync()
            ?? throw new InvalidOperationException($"视图定义 {definitionId} 不存在。");
        await GetProjectAsync(definition.ProjectId);
        var declared = await _db.Queryable<ProjectViewFieldLineage>()
            .Where(item => item.DefinitionId == definition.Id && !item.IsDeleted)
            .Select(item => item.OutputColumn)
            .ToListAsync();
        var actual = string.IsNullOrWhiteSpace(definition.ActualSchemaJson)
            ? []
            : JsonSerializer.Deserialize<List<ViewBuilderViewColumnDto>>(definition.ActualSchemaJson) ?? [];
        var declaredSet = declared.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSet = actual.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = declared.Where(item => !actualSet.Contains(item)).ToList();
        var extra = actual.Select(item => item.Name).Where(item => !declaredSet.Contains(item)).ToList();
        return new ProjectViewSchemaDiffDto
        {
            DefinitionId = definition.Id,
            DeclaredSchemaHash = definition.DeclaredSchemaHash,
            ActualSchemaHash = definition.ActualSchemaHash,
            HasActualSchema = definition.ActualSchemaJson != null,
            IsInSync = definition.ActualSchemaJson != null && missing.Count == 0 && extra.Count == 0,
            MissingColumns = missing,
            ExtraColumns = extra
        };
    }

    private async Task<Project> GetProjectAsync(long projectId)
        => await _db.Queryable<Project>()
            .Where(item => item.Id == projectId && !item.IsDeleted)
            .FirstAsync()
            ?? throw new InvalidOperationException($"项目 {projectId} 不存在。");

    private async Task ValidateReferencesAsync(Project project, SaveProjectViewDefinitionDto input)
    {
        var entityIds = input.Dependencies
            .Where(item => item.ModuleEntityId.HasValue)
            .Select(item => item.ModuleEntityId!.Value)
            .Concat(input.Fields.Where(item => item.SourceEntityId.HasValue).Select(item => item.SourceEntityId!.Value))
            .Distinct()
            .ToList();
        var entities = entityIds.Count == 0
            ? []
            : await _db.Queryable<ModuleEntity>()
                .Where(item => entityIds.Contains(item.Id) && item.ProjectId == project.Id && !item.IsDeleted)
                .ToListAsync();
        if (entities.Count != entityIds.Count)
            throw new InvalidOperationException("视图定义引用了不属于当前项目的实体。");

        var fieldIds = input.Fields.Where(item => item.SourceFieldId.HasValue).Select(item => item.SourceFieldId!.Value).Distinct().ToList();
        if (fieldIds.Count > 0)
        {
            var fields = await _db.Queryable<EntityField>()
                .Where(item => fieldIds.Contains(item.Id) && !item.IsDeleted)
                .ToListAsync();
            if (fields.Count != fieldIds.Count || fields.Any(item => !entityIds.Contains(item.ModuleEntityId)))
                throw new InvalidOperationException("视图字段血缘引用了无效的实体字段。");
        }

        var sourceViewIds = input.Dependencies.Where(item => item.SourceViewDefinitionId.HasValue)
            .Select(item => item.SourceViewDefinitionId!.Value).Distinct().ToList();
        if (sourceViewIds.Count > 0)
        {
            var sourceViews = await _db.Queryable<ProjectViewDefinition>()
                .Where(item => sourceViewIds.Contains(item.Id) && item.ProjectId == project.Id && !item.IsDeleted)
                .CountAsync();
            if (sourceViews != sourceViewIds.Count)
                throw new InvalidOperationException("视图定义引用了不属于当前项目的视图。");
        }
    }

    private static void ValidateInput(SaveProjectViewDefinitionDto input)
    {
        if (!SafeKey.IsMatch(input.DefinitionKey.Trim()))
            throw new ArgumentException("DefinitionKey 只能包含字母、数字、下划线或短横线，并且必须以字母开头。", nameof(input));
        ProjectViewBuilderService.ValidateViewName(input.ViewName);
        if (input.Ownership == ProjectViewOwnership.Managed && string.IsNullOrWhiteSpace(input.Sql))
            throw new ArgumentException("受 CodeMaster 管理的视图必须提供只读 SQL。", nameof(input));
        if (!string.IsNullOrWhiteSpace(input.Sql))
            ProjectViewBuilderService.ValidateManualSql(input.Sql);
        if (!Enum.IsDefined(input.Ownership))
            throw new ArgumentException("Ownership 无效。", nameof(input));

        var dependencyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in input.Dependencies)
        {
            if (!SafeKey.IsMatch(dependency.DependencyKey.Trim()) || !dependencyKeys.Add(dependency.DependencyKey.Trim()))
                throw new ArgumentException("参与源的 DependencyKey 必须唯一且格式合法。", nameof(input));
            if (!Enum.IsDefined(dependency.Kind) || string.IsNullOrWhiteSpace(dependency.PhysicalName))
                throw new ArgumentException("参与源必须包含合法类型和物理名称。", nameof(input));
            ValidateIdentifierPart(dependency.PhysicalName, nameof(input));
            if (!string.IsNullOrWhiteSpace(dependency.PhysicalSchema)) ValidateIdentifierPart(dependency.PhysicalSchema, nameof(input));
        }

        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in input.Fields)
        {
            if (!SafeIdentifier.IsMatch(field.OutputName.Trim()) || !outputNames.Add(field.OutputName.Trim()))
                throw new ArgumentException("输出字段名称必须唯一且格式合法。", nameof(input));
            if (!SafeIdentifier.IsMatch(field.OutputColumn.Trim()) || !outputColumns.Add(field.OutputColumn.Trim()))
                throw new ArgumentException("输出列名称必须唯一且格式合法。", nameof(input));
            if (string.IsNullOrWhiteSpace(field.DataType) || !Enum.IsDefined(field.LineageKind))
                throw new ArgumentException("输出字段必须包含类型和合法血缘类型。", nameof(input));
        }
        if (!string.IsNullOrWhiteSpace(input.KeyColumn) && !outputColumns.Contains(input.KeyColumn.Trim()))
            throw new ArgumentException("KeyColumn 必须存在于声明的输出列中。", nameof(input));
        if (input.Fields.Any(item => !string.IsNullOrWhiteSpace(item.SourceDependencyKey) &&
                                     !dependencyKeys.Contains(item.SourceDependencyKey.Trim())))
            throw new ArgumentException("字段血缘引用了不存在的参与源。", nameof(input));
    }

    private static void ValidateIdentifierPart(string? value, string parameterName)
    {
        if (!string.IsNullOrWhiteSpace(value) && !SafeIdentifier.IsMatch(value.Trim()))
            throw new ArgumentException("数据库标识符格式不合法。", parameterName);
    }

    private static string BuildCanonicalDefinition(SaveProjectViewDefinitionDto input, DatabaseType databaseType)
        => JsonSerializer.Serialize(new
        {
            input.ProjectId,
            definitionKey = input.DefinitionKey.Trim(),
            viewName = input.ViewName.Trim(),
            schemaName = NormalizeOptional(input.SchemaName),
            databaseType,
            sql = NormalizeSql(input.Sql),
            keyColumn = NormalizeOptional(input.KeyColumn),
            input.Ownership,
            input.AllowReplace,
            dependencies = input.Dependencies.OrderBy(item => item.DependencyKey).Select(item => new
            {
                key = item.DependencyKey.Trim(), item.Kind, schema = NormalizeOptional(item.PhysicalSchema), name = item.PhysicalName.Trim(),
                alias = NormalizeOptional(item.SourceAlias), role = NormalizeOptional(item.SourceRole), item.ModuleEntityId, item.SourceViewDefinitionId
            }),
            fields = input.Fields.OrderBy(item => item.Ordinal ?? int.MaxValue).ThenBy(item => item.OutputName).Select(item => new
            {
                name = item.OutputName.Trim(), column = item.OutputColumn.Trim(), type = item.DataType.Trim(), item.IsNullable, item.Ordinal,
                item.LineageKind, dependency = NormalizeOptional(item.SourceDependencyKey), item.SourceEntityId, item.SourceFieldId,
                expression = NormalizeOptional(item.Expression), notes = NormalizeOptional(item.LineageNotes)
            })
        });

    private static ProjectViewDependency ToDependency(ProjectViewDefinition definition, ProjectViewDependencyInputDto input)
        => new()
        {
            DefinitionId = definition.Id,
            ProjectId = definition.ProjectId,
            TenantId = definition.TenantId,
            DependencyKey = input.DependencyKey.Trim(),
            Kind = input.Kind,
            PhysicalSchema = NormalizeOptional(input.PhysicalSchema),
            PhysicalName = input.PhysicalName.Trim(),
            SourceAlias = NormalizeOptional(input.SourceAlias),
            SourceRole = NormalizeOptional(input.SourceRole),
            ModuleEntityId = input.ModuleEntityId,
            SourceViewDefinitionId = input.SourceViewDefinitionId,
            CreateBy = "CodeMaster",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };

    private static ProjectViewFieldLineage ToLineage(
        ProjectViewDefinition definition,
        ProjectViewFieldLineageInputDto input,
        IReadOnlyDictionary<string, long> dependencyIds)
        => new()
        {
            DefinitionId = definition.Id,
            ProjectId = definition.ProjectId,
            TenantId = definition.TenantId,
            OutputName = input.OutputName.Trim(),
            OutputColumn = input.OutputColumn.Trim(),
            DataType = input.DataType.Trim(),
            IsNullable = input.IsNullable,
            Ordinal = input.Ordinal ?? 0,
            LineageKind = input.LineageKind,
            SourceDependencyId = string.IsNullOrWhiteSpace(input.SourceDependencyKey) ? null : dependencyIds[input.SourceDependencyKey.Trim()],
            SourceEntityId = input.SourceEntityId,
            SourceFieldId = input.SourceFieldId,
            Expression = NormalizeOptional(input.Expression),
            LineageNotes = NormalizeOptional(input.LineageNotes),
            CreateBy = "CodeMaster",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = DateTime.UtcNow
        };

    private async Task<List<ProjectViewDefinitionDto>> ToDtosAsync(List<ProjectViewDefinition> definitions)
    {
        if (definitions.Count == 0) return [];
        var ids = definitions.Select(item => item.Id).ToList();
        var dependencies = await _db.Queryable<ProjectViewDependency>().Where(item => ids.Contains(item.DefinitionId) && !item.IsDeleted).ToListAsync();
        var fields = await _db.Queryable<ProjectViewFieldLineage>().Where(item => ids.Contains(item.DefinitionId) && !item.IsDeleted).ToListAsync();
        return definitions.Select(definition => new ProjectViewDefinitionDto
        {
            Id = definition.Id,
            ProjectId = definition.ProjectId,
            DefinitionKey = definition.DefinitionKey,
            ViewName = definition.ViewName,
            SchemaName = definition.SchemaName,
            DatabaseType = definition.DatabaseType,
            Sql = definition.Sql,
            SqlHash = definition.SqlHash,
            DefinitionVersion = definition.DefinitionVersion,
            DefinitionHash = definition.DefinitionHash,
            KeyColumn = definition.KeyColumn,
            Ownership = definition.Ownership,
            DeploymentState = definition.DeploymentState,
            DeclaredSchemaHash = definition.DeclaredSchemaHash,
            ActualSchemaHash = definition.ActualSchemaHash,
            AppliedDefinitionHash = definition.AppliedDefinitionHash,
            ModuleEntityId = definition.ModuleEntityId,
            ProjectDataSourceId = definition.ProjectDataSourceId,
            AllowReplace = definition.AllowReplace,
            LastDeployedAt = definition.LastDeployedAt,
            LastInspectedAt = definition.LastInspectedAt,
            LastError = definition.LastError,
            Dependencies = dependencies.Where(item => item.DefinitionId == definition.Id).OrderBy(item => item.Id).Select(item => new ProjectViewDependencyDto
            {
                Id = item.Id, DependencyKey = item.DependencyKey, Kind = item.Kind, PhysicalSchema = item.PhysicalSchema,
                PhysicalName = item.PhysicalName, SourceAlias = item.SourceAlias, SourceRole = item.SourceRole,
                ModuleEntityId = item.ModuleEntityId, SourceViewDefinitionId = item.SourceViewDefinitionId
            }).ToList(),
            Fields = fields.Where(item => item.DefinitionId == definition.Id).OrderBy(item => item.Ordinal).ThenBy(item => item.Id).Select(item => new ProjectViewFieldLineageDto
            {
                Id = item.Id, OutputName = item.OutputName, OutputColumn = item.OutputColumn, DataType = item.DataType,
                IsNullable = item.IsNullable, Ordinal = item.Ordinal, LineageKind = item.LineageKind,
                SourceDependencyId = item.SourceDependencyId, SourceEntityId = item.SourceEntityId, SourceFieldId = item.SourceFieldId,
                Expression = item.Expression, LineageNotes = item.LineageNotes
            }).ToList()
        }).ToList();
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeSql(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd(';').Trim();

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
