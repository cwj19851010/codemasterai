using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Dtos;
using CodeMaster.UiComposition.Entities;
using CodeMaster.UiComposition.Services.OpenApi;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class OpenApiImportService : IOpenApiImportService
{
    private const int MaxDocumentBytes = 10 * 1024 * 1024;
    private readonly ISqlSugarClient _db;
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenApiImportService(ISqlSugarClient db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    [Permission("codegen:projectpage:list")]
    public async Task<List<OpenApiDocumentDto>> GetDocumentsAsync(long projectId)
    {
        var project = await GetProjectAsync(projectId);
        var documents = await _db.Queryable<OpenApiDocumentRegistration>()
            .Where(item => item.ProjectId == project.Id && item.TenantId == project.TenantId && !item.IsDeleted)
            .OrderByDescending(item => item.UpdateTime)
            .OrderByDescending(item => item.CreateTime)
            .ToListAsync();
        return documents.Select(ToDocumentDto).ToList();
    }

    [Permission("codegen:projectpage:list")]
    public async Task<OpenApiImportPreviewDto> PreviewAsync(PreviewOpenApiImportDto input)
    {
        var context = await PrepareImportAsync(input);
        return await BuildPreviewAsync(context);
    }

    [Permission("codegen:projectpage:create")]
    public async Task<OpenApiApplyResultDto> ApplyAsync(ApplyOpenApiImportDto input)
    {
        var context = await PrepareImportAsync(input);
        var preview = await BuildPreviewAsync(context);
        if (!string.Equals(input.ExpectedDocumentHash, preview.DocumentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAPI 文档已变化，请重新预览后再应用。");
        if (!string.Equals(input.ExpectedChangeSetHash, preview.ChangeSetHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAPI 变更集已变化，请重新预览后再应用。");

        var conflicts = preview.Changes.Where(item => item.State == OpenApiSyncState.Conflict).ToList();
        if (conflicts.Count > 0)
            throw new InvalidOperationException($"存在 {conflicts.Count} 个冲突，请先处理：{string.Join("；", conflicts.Take(5).Select(item => item.Description))}");

        var removalDecisions = CreateRemovalDecisions(input, preview);

        var now = DateTime.UtcNow;
        var result = new OpenApiApplyResultDto
        {
            DocumentHash = preview.DocumentHash,
            ChangeSetHash = preview.ChangeSetHash,
            AddedCount = preview.Changes.Count(item => item.State == OpenApiSyncState.Added),
            ModifiedCount = preview.Changes.Count(item => item.State is OpenApiSyncState.Modified or OpenApiSyncState.RenameCandidate),
            PendingDeleteCount = preview.Changes.Count(item => item.State == OpenApiSyncState.PendingDelete)
                                 - removalDecisions.RemoveKeys.Count
                                 - removalDecisions.DetachKeys.Count,
            ConflictCount = conflicts.Count
        };

        _db.Ado.BeginTran();
        try
        {
            var document = await UpsertDocumentAsync(context, now);
            result.DocumentId = document.Id;

            var existingBindings = await _db.Queryable<OpenApiSchemaBinding>()
                .Where(item => item.DocumentId == document.Id && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                .ToListAsync();
            var bindingByKey = existingBindings.ToDictionary(item => item.SchemaKey, StringComparer.Ordinal);
            var entityById = (await _db.Queryable<ModuleEntity>()
                    .Where(item => item.ProjectId == context.Project.Id && item.ModuleId == context.Module.Id
                                   && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                    .ToListAsync())
                .ToDictionary(item => item.Id);

            foreach (var schemaPreview in preview.Schemas)
            {
                var schema = context.Parsed.Schemas.First(item => item.SchemaKey == schemaPreview.SchemaKey);
                var entity = schemaPreview.ExistingEntityId.HasValue
                    ? entityById.GetValueOrDefault(schemaPreview.ExistingEntityId.Value)
                    : null;
                if (entity == null)
                {
                    entity = CreateMetadataOnlyEntity(context, schema, now);
                    entity.Id = await _db.Insertable(entity).ExecuteReturnSnowflakeIdAsync();
                    entityById[entity.Id] = entity;
                }
                else
                {
                    ApplyMetadataOnlyEntityMetadata(entity, schema, now);
                    await _db.Updateable(entity).ExecuteCommandAsync();
                }

                if (!bindingByKey.TryGetValue(schema.SchemaKey, out var binding))
                {
                    binding = new OpenApiSchemaBinding
                    {
                        DocumentId = document.Id,
                        ProjectId = context.Project.Id,
                        ModuleId = context.Module.Id,
                        ModuleEntityId = entity.Id,
                        SchemaKey = schema.SchemaKey,
                        TenantId = context.Project.TenantId,
                        CreateBy = "CodeMaster OpenAPI",
                        CreateUserId = 0,
                        UpdateUserId = 0,
                        CreateTime = now
                    };
                    ApplySchemaBindingMetadata(binding, schema, now);
                    binding.Id = await _db.Insertable(binding).ExecuteReturnSnowflakeIdAsync();
                    bindingByKey[schema.SchemaKey] = binding;
                    existingBindings.Add(binding);
                }
                else
                {
                    binding.ModuleEntityId = entity.Id;
                    ApplySchemaBindingMetadata(binding, schema, now);
                    await _db.Updateable(binding).ExecuteCommandAsync();
                }

                result.EntityIds.Add(entity.Id);
            }

            var activeSchemaKeys = context.Parsed.Schemas.Select(item => item.SchemaKey).ToHashSet(StringComparer.Ordinal);
            var existingMembers = await _db.Queryable<OpenApiMemberBinding>()
                .Where(item => item.DocumentId == document.Id && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                .ToListAsync();
            var fieldIds = existingMembers.Where(item => item.EntityFieldId.HasValue).Select(item => item.EntityFieldId!.Value).ToList();
            var fields = fieldIds.Count == 0
                ? new List<EntityField>()
                : await _db.Queryable<EntityField>().Where(item => fieldIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
            var fieldById = fields.ToDictionary(item => item.Id);

            foreach (var schema in context.Parsed.Schemas)
            {
                var binding = bindingByKey[schema.SchemaKey];
                var schemaMembers = existingMembers.Where(item => item.SchemaBindingId == binding.Id).ToList();
                var entityFields = await _db.Queryable<EntityField>()
                    .Where(item => item.ModuleEntityId == binding.ModuleEntityId && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                    .ToListAsync();
                foreach (var field in entityFields) fieldById[field.Id] = field;

                var matchedBindingIds = new HashSet<long>();
                foreach (var member in schema.Members)
                {
                    var memberBinding = FindMemberBinding(schemaMembers, member);
                    if (memberBinding != null) matchedBindingIds.Add(memberBinding.Id);

                    EntityField? field = null;
                    if (member.MemberKind == OpenApiMemberKind.Scalar)
                    {
                        if (memberBinding?.EntityFieldId is long fieldId)
                            fieldById.TryGetValue(fieldId, out field);
                        field ??= entityFields.FirstOrDefault(item => item.Name.Equals(member.MemberName, StringComparison.Ordinal));
                        if (field == null)
                        {
                            field = CreateField(binding.ModuleEntityId, context.Project.TenantId, member, now, entityFields.Count);
                            field.Id = await _db.Insertable(field).ExecuteReturnSnowflakeIdAsync();
                            entityFields.Add(field);
                            fieldById[field.Id] = field;
                        }
                        else
                        {
                            ApplyFieldMetadata(field, member, now);
                            await _db.Updateable(field).ExecuteCommandAsync();
                        }
                    }

                    var targetBindingId = member.TargetSchemaKey != null && bindingByKey.TryGetValue(member.TargetSchemaKey, out var targetBinding)
                        ? targetBinding.Id
                        : default(long?);
                    if (memberBinding == null)
                    {
                        memberBinding = new OpenApiMemberBinding
                        {
                            DocumentId = document.Id,
                            SchemaBindingId = binding.Id,
                            MemberKey = member.MemberKey,
                            TenantId = context.Project.TenantId,
                            CreateBy = "CodeMaster OpenAPI",
                            CreateUserId = 0,
                            UpdateUserId = 0,
                            CreateTime = now
                        };
                        ApplyMemberBindingMetadata(memberBinding, member, field?.Id, targetBindingId, now);
                        memberBinding.Id = await _db.Insertable(memberBinding).ExecuteReturnSnowflakeIdAsync();
                        existingMembers.Add(memberBinding);
                    }
                    else
                    {
                        ApplyMemberBindingMetadata(memberBinding, member, field?.Id, targetBindingId, now);
                        await _db.Updateable(memberBinding).ExecuteCommandAsync();
                    }
                }

                foreach (var removed in schemaMembers.Where(item => !matchedBindingIds.Contains(item.Id)
                                                                    && item.SyncState != OpenApiSyncState.Detached))
                {
                    var changeKey = $"member:{removed.MemberKey}";
                    result.RemovedCount += await HandleRemovedMemberAsync(
                        removed,
                        removalDecisions.GetAction(changeKey),
                        now,
                        result);
                }
            }

            var dataSources = await UpsertOperationsAsync(context, document, bindingByKey, removalDecisions, now, result);
            var removedSchemas = existingBindings
                .Where(item => !activeSchemaKeys.Contains(item.SchemaKey) && item.SyncState != OpenApiSyncState.Detached)
                .ToList();
            result.RemovedCount += await HandleRemovedSchemasAsync(removedSchemas, removalDecisions, now, result);
            result.DataSourceIds = dataSources.Where(item => !item.IsDeleted).Select(item => item.Id).Distinct().ToList();
            result.EntityIds = result.EntityIds.Distinct().ToList();
            if (preview.Changes.Count > 0)
            {
                result.AffectedPages = await MarkAffectedPagesAsync(document.Id, now);
                result.AffectedPageIds = result.AffectedPages.Select(item => item.PageId).ToList();
            }

            _db.Ado.CommitTran();
            return result;
        }
        catch
        {
            _db.Ado.RollbackTran();
            throw;
        }
    }

    [Permission("codegen:projectpage:view")]
    public async Task<OpenApiSchemaTreeDto?> GetSchemaTreeAsync(long entityId)
    {
        var root = await _db.Queryable<OpenApiSchemaBinding>()
            .Where(item => item.ModuleEntityId == entityId && !item.IsDeleted)
            .FirstAsync();
        if (root == null) return null;

        var bindings = await _db.Queryable<OpenApiSchemaBinding>()
            .Where(item => item.DocumentId == root.DocumentId && item.TenantId == root.TenantId && !item.IsDeleted)
            .ToListAsync();
        var members = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => item.DocumentId == root.DocumentId && item.TenantId == root.TenantId && !item.IsDeleted)
            .OrderBy(item => item.Id)
            .ToListAsync();
        var bindingById = bindings.ToDictionary(item => item.Id);

        return new OpenApiSchemaTreeDto
        {
            EntityId = root.ModuleEntityId,
            SchemaKey = root.SchemaKey,
            DtoName = root.DtoName,
            Members = BuildTreeMembers(root.Id, members, bindingById, new HashSet<long>(), 0)
        };
    }

    private async Task<ImportContext> PrepareImportAsync(PreviewOpenApiImportDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var project = await GetProjectAsync(input.ProjectId);
        var module = await _db.Queryable<ProjectModule>()
            .Where(item => item.Id == input.ModuleId && item.ProjectId == project.Id
                           && item.TenantId == project.TenantId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"项目模块 {input.ModuleId} 不存在。");

        OpenApiDocumentRegistration? existing = null;
        if (input.DocumentId.HasValue)
        {
            existing = await _db.Queryable<OpenApiDocumentRegistration>()
                .Where(item => item.Id == input.DocumentId.Value && item.ProjectId == project.Id
                               && item.TenantId == project.TenantId && !item.IsDeleted)
                .FirstAsync() ?? throw new InvalidOperationException($"OpenAPI 文档 {input.DocumentId.Value} 不存在。");
        }

        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = existing?.Name ?? string.Empty;
        var sourceUrl = string.IsNullOrWhiteSpace(input.SourceUrl) ? existing?.SourceUrl : input.SourceUrl.Trim();
        var content = input.DocumentContent;
        if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(input.SourceUrl))
            content = await DownloadDocumentAsync(input.SourceUrl.Trim());
        if (string.IsNullOrWhiteSpace(content)) content = existing?.RawDocument;
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("请提供 OpenAPI 文档内容或 HTTP/HTTPS 地址。");

        var parsed = new OpenApiContractParser().Parse(content);
        if (string.IsNullOrWhiteSpace(name)) name = parsed.Title;
        if (name.Length > 200) throw new ArgumentException("OpenAPI 文档名称不能超过 200 个字符。");

        if (existing == null)
        {
            existing = await _db.Queryable<OpenApiDocumentRegistration>()
                .Where(item => item.ProjectId == project.Id && item.TenantId == project.TenantId
                               && item.Name == name && !item.IsDeleted)
                .FirstAsync();
        }
        else if (existing.ModuleId != module.Id)
        {
            throw new InvalidOperationException("OpenAPI 文档创建后不能迁移到其他模块；请新建数据源文档。");
        }

        return new ImportContext(project, module, existing, name, sourceUrl, content, parsed);
    }

    private async Task<OpenApiImportPreviewDto> BuildPreviewAsync(ImportContext context)
    {
        var preview = new OpenApiImportPreviewDto
        {
            DocumentId = context.ExistingDocument?.Id,
            ProjectId = context.Project.Id,
            ModuleId = context.Module.Id,
            Name = context.Name,
            SourceUrl = context.SourceUrl,
            Title = context.Parsed.Title,
            Description = context.Parsed.Description,
            OpenApiVersion = context.Parsed.OpenApiVersion,
            DocumentHash = context.Parsed.DocumentHash,
            Warnings = context.Parsed.Warnings.ToList()
        };

        var bindings = context.ExistingDocument == null
            ? new List<OpenApiSchemaBinding>()
            : await _db.Queryable<OpenApiSchemaBinding>()
                .Where(item => item.DocumentId == context.ExistingDocument.Id && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                .ToListAsync();
        var bindingByKey = bindings.ToDictionary(item => item.SchemaKey, StringComparer.Ordinal);
        var members = bindings.Count == 0
            ? new List<OpenApiMemberBinding>()
            : await _db.Queryable<OpenApiMemberBinding>()
                .Where(item => item.DocumentId == context.ExistingDocument!.Id && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                .ToListAsync();
        var entities = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == context.Project.Id && item.ModuleId == context.Module.Id
                           && item.TenantId == context.Project.TenantId && !item.IsDeleted)
            .ToListAsync();
        var entityIds = entities.Select(item => item.Id).ToList();
        var fields = entityIds.Count == 0
            ? new List<EntityField>()
            : await _db.Queryable<EntityField>()
                .Where(item => entityIds.Contains(item.ModuleEntityId) && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                .ToListAsync();
        var fieldById = fields.ToDictionary(item => item.Id);
        var entityById = entities.ToDictionary(item => item.Id);
        var activeSchemaKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var schema in context.Parsed.Schemas)
        {
            activeSchemaKeys.Add(schema.SchemaKey);
            bindingByKey.TryGetValue(schema.SchemaKey, out var binding);
            var entity = binding != null ? entityById.GetValueOrDefault(binding.ModuleEntityId) : null;
            entity ??= entities.FirstOrDefault(item => item.Name.Equals(schema.DtoName, StringComparison.Ordinal));
            var schemaState = GetSchemaState(schema, binding, entity);
            var schemaPreview = new OpenApiSchemaPreviewDto
            {
                SchemaKey = schema.SchemaKey,
                SchemaName = schema.SchemaName,
                DtoName = schema.DtoName,
                Description = schema.Description,
                DtoRole = schema.DtoRole,
                SchemaHash = schema.SchemaHash,
                ExistingEntityId = entity?.Id,
                SyncState = schemaState
            };

            if (schemaState != OpenApiSyncState.Active)
                preview.Changes.Add(CreateSchemaChange(schema, binding, entity, schemaState));

            var schemaMembers = binding == null
                ? new List<OpenApiMemberBinding>()
                : members.Where(item => item.SchemaBindingId == binding.Id).ToList();
            var matched = new HashSet<long>();
            var entityFields = entity == null ? new List<EntityField>() : fields.Where(item => item.ModuleEntityId == entity.Id).ToList();
            foreach (var member in schema.Members)
            {
                var memberBinding = FindMemberBinding(schemaMembers, member);
                if (memberBinding != null) matched.Add(memberBinding.Id);
                EntityField? field = null;
                if (memberBinding?.EntityFieldId is long fieldId) fieldById.TryGetValue(fieldId, out field);
                field ??= entityFields.FirstOrDefault(item => item.Name.Equals(member.MemberName, StringComparison.Ordinal));
                var memberState = GetMemberState(member, memberBinding, field);
                var memberPreview = ToMemberPreview(member, memberBinding, field, memberState, context.Parsed.Schemas);
                schemaPreview.Members.Add(memberPreview);
                if (memberState != OpenApiSyncState.Active)
                    preview.Changes.Add(CreateMemberChange(schema, member, memberBinding, field, memberState));
            }

            foreach (var removed in schemaMembers.Where(item => !matched.Contains(item.Id)
                                                                && item.SyncState != OpenApiSyncState.Detached))
            {
                fieldById.TryGetValue(removed.EntityFieldId ?? 0, out var field);
                preview.Changes.Add(new OpenApiChangeDto
                {
                    ChangeKey = $"member:{removed.MemberKey}",
                    TargetType = "Member",
                    TargetName = removed.MemberName,
                    ParentName = schema.DtoName,
                    State = OpenApiSyncState.PendingDelete,
                    IsBreaking = true,
                    Description = $"字段 {schema.DtoName}.{removed.MemberName} 已不在新文档中，等待确认删除。",
                    EntityId = binding?.ModuleEntityId,
                    FieldId = field?.Id,
                    PreviousSnapshotJson = removed.SnapshotJson
                });
            }

            preview.Schemas.Add(schemaPreview);
        }

        foreach (var removed in bindings.Where(item => !activeSchemaKeys.Contains(item.SchemaKey)
                                                        && item.SyncState != OpenApiSyncState.Detached))
        {
            preview.Changes.Add(new OpenApiChangeDto
            {
                ChangeKey = $"schema:{removed.SchemaKey}",
                TargetType = "Schema",
                TargetName = removed.DtoName,
                State = OpenApiSyncState.PendingDelete,
                IsBreaking = true,
                Description = $"DTO {removed.DtoName} 已不在新文档中，等待确认删除。",
                EntityId = removed.ModuleEntityId,
                PreviousSnapshotJson = removed.SnapshotJson
            });
        }

        await AppendOperationPreviewAsync(context, preview);
        preview.Changes = preview.Changes
            .OrderBy(item => item.ChangeKey, StringComparer.Ordinal)
            .ThenBy(item => item.State)
            .ToList();
        preview.ChangeSetHash = OpenApiContractParser.HashJson(new
        {
            preview.DocumentHash,
            Changes = preview.Changes.Select(item => new
            {
                item.ChangeKey,
                item.State,
                item.PreviousSnapshotJson,
                item.CurrentSnapshotJson
            })
        });
        return preview;
    }

    private async Task AppendOperationPreviewAsync(ImportContext context, OpenApiImportPreviewDto preview)
    {
        var sources = context.ExistingDocument == null
            ? new List<ProjectDataSource>()
            : await _db.Queryable<ProjectDataSource>()
                .Where(item => item.OpenApiDocumentId == context.ExistingDocument.Id
                               && item.TenantId == context.Project.TenantId && !item.IsDeleted)
                .ToListAsync();
        var byOperation = sources.Where(item => !string.IsNullOrWhiteSpace(item.OperationKey))
            .ToDictionary(item => item.OperationKey!, StringComparer.Ordinal);
        var schemaByKey = context.Parsed.Schemas.ToDictionary(item => item.SchemaKey, StringComparer.Ordinal);
        var activeOperationKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in context.Parsed.Operations)
        {
            activeOperationKeys.Add(operation.OperationKey);
            byOperation.TryGetValue(operation.OperationKey, out var existing);
            var schemaHash = ComputeOperationSchemaHash(operation, schemaByKey);
            var state = existing == null || existing.OpenApiSyncState == OpenApiSyncState.Detached
                ? OpenApiSyncState.Added
                : string.Equals(existing.SchemaHash, schemaHash, StringComparison.OrdinalIgnoreCase)
                    ? OpenApiSyncState.Active
                    : OpenApiSyncState.Modified;
            var requestDto = operation.RequestSchemaKey != null && schemaByKey.TryGetValue(operation.RequestSchemaKey, out var requestSchema)
                ? requestSchema.DtoName
                : null;
            var responseDto = operation.ResponseSchemaKey != null && schemaByKey.TryGetValue(operation.ResponseSchemaKey, out var responseSchema)
                ? responseSchema.DtoName
                : null;
            var responseEnvelopeDto = operation.ResponseEnvelopeSchemaKey != null
                                      && schemaByKey.TryGetValue(operation.ResponseEnvelopeSchemaKey, out var responseEnvelopeSchema)
                ? responseEnvelopeSchema.DtoName
                : null;
            var contractJson = JsonSerializer.Serialize(operation, OpenApiContractParser.JsonOptions);
            preview.Operations.Add(new OpenApiOperationPreviewDto
            {
                ExistingDataSourceId = existing?.Id,
                OperationKey = operation.OperationKey,
                OperationId = operation.OperationId,
                HttpMethod = operation.HttpMethod,
                RoutePath = operation.RoutePath,
                Summary = operation.Summary,
                Description = operation.Description,
                RequestSchemaKey = operation.RequestSchemaKey,
                ResponseSchemaKey = operation.ResponseSchemaKey,
                ResponseEnvelopeSchemaKey = operation.ResponseEnvelopeSchemaKey,
                RequestDtoName = requestDto,
                ResponseDtoName = responseDto,
                ResponseEnvelopeDtoName = responseEnvelopeDto,
                DataShape = operation.DataShape,
                ParametersJson = JsonSerializer.Serialize(operation.Parameters, OpenApiContractParser.JsonOptions),
                ContractJson = contractJson,
                SyncState = state
            });
            if (state != OpenApiSyncState.Active)
            {
                preview.Changes.Add(new OpenApiChangeDto
                {
                    ChangeKey = $"operation:{operation.OperationKey}",
                    TargetType = "Operation",
                    TargetName = operation.OperationId,
                    State = state,
                    IsBreaking = state == OpenApiSyncState.Modified,
                    Description = existing == null
                        ? $"新增 API {operation.HttpMethod} {operation.RoutePath}。"
                        : $"API {operation.HttpMethod} {operation.RoutePath} 的契约已变化。",
                    EntityId = existing?.EntityId,
                    PreviousSnapshotJson = existing?.OperationContractJson,
                    CurrentSnapshotJson = contractJson
                });
            }
        }

        foreach (var removed in sources.Where(item => item.OperationKey != null
                                                      && !activeOperationKeys.Contains(item.OperationKey)
                                                      && item.OpenApiSyncState != OpenApiSyncState.Detached))
        {
            preview.Changes.Add(new OpenApiChangeDto
            {
                ChangeKey = $"operation:{removed.OperationKey}",
                TargetType = "Operation",
                TargetName = removed.MethodName,
                State = OpenApiSyncState.PendingDelete,
                IsBreaking = true,
                Description = $"API {removed.HttpMethod} {removed.RoutePath} 已不在新文档中，等待确认删除。",
                EntityId = removed.EntityId,
                PreviousSnapshotJson = removed.OperationContractJson
            });
        }
    }

    private async Task<OpenApiDocumentRegistration> UpsertDocumentAsync(ImportContext context, DateTime now)
    {
        var document = context.ExistingDocument;
        if (document == null)
        {
            document = new OpenApiDocumentRegistration
            {
                ProjectId = context.Project.Id,
                ModuleId = context.Module.Id,
                TenantId = context.Project.TenantId,
                CreateBy = "CodeMaster OpenAPI",
                CreateUserId = 0,
                UpdateUserId = 0,
                CreateTime = now
            };
            ApplyDocumentMetadata(document, context, now);
            document.Id = await _db.Insertable(document).ExecuteReturnSnowflakeIdAsync();
        }
        else
        {
            ApplyDocumentMetadata(document, context, now);
            await _db.Updateable(document).ExecuteCommandAsync();
        }
        return document;
    }

    private async Task<List<ProjectDataSource>> UpsertOperationsAsync(
        ImportContext context,
        OpenApiDocumentRegistration document,
        IReadOnlyDictionary<string, OpenApiSchemaBinding> bindingByKey,
        RemovalDecisions removalDecisions,
        DateTime now,
        OpenApiApplyResultDto result)
    {
        var existing = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.OpenApiDocumentId == document.Id && item.TenantId == context.Project.TenantId && !item.IsDeleted)
            .ToListAsync();
        var byOperation = existing.Where(item => !string.IsNullOrWhiteSpace(item.OperationKey))
            .ToDictionary(item => item.OperationKey!, StringComparer.Ordinal);
        var schemaByKey = context.Parsed.Schemas.ToDictionary(item => item.SchemaKey, StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var operation in context.Parsed.Operations)
        {
            active.Add(operation.OperationKey);
            byOperation.TryGetValue(operation.OperationKey, out var source);
            var requestBinding = operation.RequestSchemaKey != null ? bindingByKey.GetValueOrDefault(operation.RequestSchemaKey) : null;
            var responseBinding = operation.ResponseSchemaKey != null ? bindingByKey.GetValueOrDefault(operation.ResponseSchemaKey) : null;
            var dtoBinding = responseBinding ?? requestBinding;
            if (source == null)
            {
                source = new ProjectDataSource
                {
                    ProjectId = context.Project.Id,
                    SourceKey = CreateSourceKey(context.Name, operation.OperationKey),
                    SourceType = PageDataSourceType.Api,
                    SyncMode = PageSyncMode.Auto,
                    OpenApiDocumentId = document.Id,
                    OperationKey = operation.OperationKey,
                    TenantId = context.Project.TenantId,
                    CreateBy = "CodeMaster OpenAPI",
                    CreateUserId = 0,
                    UpdateUserId = 0,
                    CreateTime = now
                };
                ApplyOperationMetadata(source, document, operation, requestBinding, responseBinding, dtoBinding, schemaByKey, now);
                source.Id = await _db.Insertable(source).ExecuteReturnSnowflakeIdAsync();
                existing.Add(source);
            }
            else
            {
                ApplyOperationMetadata(source, document, operation, requestBinding, responseBinding, dtoBinding, schemaByKey, now);
                await _db.Updateable(source).ExecuteCommandAsync();
            }
        }

        foreach (var removed in existing.Where(item => item.OperationKey != null
                                                       && !active.Contains(item.OperationKey)
                                                       && item.OpenApiSyncState != OpenApiSyncState.Detached))
        {
            var action = removalDecisions.GetAction($"operation:{removed.OperationKey}");
            if (action == RemovalAction.Pending)
            {
                removed.OpenApiSyncState = OpenApiSyncState.PendingDelete;
                removed.UpdateTime = now;
                await _db.Updateable(removed).ExecuteCommandAsync();
                continue;
            }

            if (action == RemovalAction.Detach)
            {
                removed.OpenApiSyncState = OpenApiSyncState.Detached;
                removed.IsEnabled = true;
                removed.UpdateTime = now;
                await _db.Updateable(removed).ExecuteCommandAsync();
                continue;
            }

            var hasBinding = await _db.Queryable<PageDataSourceBinding>()
                .AnyAsync(item => item.DataSourceId == removed.Id && !item.IsDeleted);
            if (hasBinding)
            {
                removed.OpenApiSyncState = OpenApiSyncState.Conflict;
                removed.IsEnabled = false;
                result.ConflictCount++;
            }
            else
            {
                removed.OpenApiSyncState = OpenApiSyncState.Detached;
                removed.IsDeleted = true;
                removed.DeleteTime = now;
                result.RemovedCount++;
            }
            removed.UpdateTime = now;
            await _db.Updateable(removed).ExecuteCommandAsync();
        }
        return existing;
    }

    private async Task<int> HandleRemovedMemberAsync(
        OpenApiMemberBinding member,
        RemovalAction action,
        DateTime now,
        OpenApiApplyResultDto result)
    {
        if (action == RemovalAction.Pending)
        {
            member.SyncState = OpenApiSyncState.PendingDelete;
            member.UpdateTime = now;
            await _db.Updateable(member).ExecuteCommandAsync();
            return 0;
        }

        if (action == RemovalAction.Detach)
        {
            member.SyncState = OpenApiSyncState.Detached;
            member.UpdateTime = now;
            await _db.Updateable(member).ExecuteCommandAsync();
            return 0;
        }

        if (member.EntityFieldId is long fieldId)
        {
            var referenced = await _db.Queryable<PageComponentBinding>()
                .AnyAsync(item => item.FieldId == fieldId && !item.IsDeleted);
            if (referenced)
            {
                member.SyncState = OpenApiSyncState.Conflict;
                member.UpdateTime = now;
                await _db.Updateable(member).ExecuteCommandAsync();
                result.ConflictCount++;
                return 0;
            }

            await _db.Updateable<EntityField>()
                .SetColumns(item => new EntityField { IsDeleted = true, DeleteTime = now })
                .Where(item => item.Id == fieldId && !item.IsDeleted)
                .ExecuteCommandAsync();
        }

        member.SyncState = OpenApiSyncState.Detached;
        member.IsDeleted = true;
        member.DeleteTime = now;
        member.UpdateTime = now;
        await _db.Updateable(member).ExecuteCommandAsync();
        return 1;
    }

    private async Task<int> HandleRemovedSchemasAsync(
        IReadOnlyCollection<OpenApiSchemaBinding> bindings,
        RemovalDecisions removalDecisions,
        DateTime now,
        OpenApiApplyResultDto result)
    {
        if (bindings.Count == 0) return 0;
        var pendingBindings = bindings
            .Where(item => removalDecisions.GetAction($"schema:{item.SchemaKey}") == RemovalAction.Pending)
            .ToList();
        foreach (var binding in pendingBindings)
        {
            binding.SyncState = OpenApiSyncState.PendingDelete;
            binding.UpdateTime = now;
            await _db.Updateable(binding).ExecuteCommandAsync();
        }

        var detachedBindings = bindings
            .Where(item => removalDecisions.GetAction($"schema:{item.SchemaKey}") == RemovalAction.Detach)
            .ToList();
        foreach (var binding in detachedBindings)
        {
            binding.SyncState = OpenApiSyncState.Detached;
            binding.UpdateTime = now;
            await _db.Updateable(binding).ExecuteCommandAsync();
            await _db.Updateable<OpenApiMemberBinding>()
                .SetColumns(item => new OpenApiMemberBinding
                {
                    SyncState = OpenApiSyncState.Detached,
                    UpdateTime = now
                })
                .Where(item => item.SchemaBindingId == binding.Id && !item.IsDeleted)
                .ExecuteCommandAsync();
        }

        bindings = bindings
            .Where(item => removalDecisions.GetAction($"schema:{item.SchemaKey}") == RemovalAction.Remove)
            .ToList();
        if (bindings.Count == 0) return 0;

        var removedBindingIds = bindings.Select(item => item.Id).ToHashSet();
        var removedEntityIds = bindings.Select(item => item.ModuleEntityId).ToHashSet();
        var protectedBindingIds = new HashSet<long>();
        var referencedEntityIds = await _db.Queryable<ProjectDataSource>()
            .Where(item => !item.IsDeleted
                           && (removedEntityIds.Contains(item.EntityId ?? 0)
                               || removedEntityIds.Contains(item.RequestEntityId ?? 0)
                               || removedEntityIds.Contains(item.ResponseEntityId ?? 0)))
            .Select(item => new { item.EntityId, item.RequestEntityId, item.ResponseEntityId })
            .ToListAsync();
        var protectedEntityIds = referencedEntityIds
            .SelectMany(item => new[] { item.EntityId, item.RequestEntityId, item.ResponseEntityId })
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToHashSet();

        var removedFields = await _db.Queryable<EntityField>()
            .Where(item => removedEntityIds.Contains(item.ModuleEntityId) && !item.IsDeleted)
            .Select(item => new { item.Id, item.ModuleEntityId })
            .ToListAsync();
        var removedFieldIds = removedFields.Select(item => item.Id).ToList();
        if (removedFieldIds.Count > 0)
        {
            var referencedFieldIds = await _db.Queryable<PageComponentBinding>()
                .Where(item => item.FieldId.HasValue && removedFieldIds.Contains(item.FieldId.Value) && !item.IsDeleted)
                .Select(item => item.FieldId!.Value)
                .Distinct()
                .ToListAsync();
            var referencedFieldSet = referencedFieldIds.ToHashSet();
            foreach (var field in removedFields.Where(item => referencedFieldSet.Contains(item.Id)))
                protectedEntityIds.Add(field.ModuleEntityId);
        }

        foreach (var binding in bindings.Where(item => protectedEntityIds.Contains(item.ModuleEntityId)))
            protectedBindingIds.Add(binding.Id);

        var externalReferences = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => item.TargetSchemaBindingId.HasValue
                           && removedBindingIds.Contains(item.TargetSchemaBindingId.Value)
                           && !removedBindingIds.Contains(item.SchemaBindingId)
                           && !item.IsDeleted)
            .Select(item => item.TargetSchemaBindingId!.Value)
            .Distinct()
            .ToListAsync();
        protectedBindingIds.UnionWith(externalReferences);

        var internalEdges = await _db.Queryable<OpenApiMemberBinding>()
            .Where(item => removedBindingIds.Contains(item.SchemaBindingId)
                           && item.TargetSchemaBindingId.HasValue
                           && removedBindingIds.Contains(item.TargetSchemaBindingId.Value)
                           && !item.IsDeleted)
            .Select(item => new { item.SchemaBindingId, TargetId = item.TargetSchemaBindingId!.Value })
            .ToListAsync();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in internalEdges.Where(item => protectedBindingIds.Contains(item.SchemaBindingId)))
            {
                if (protectedBindingIds.Add(edge.TargetId)) changed = true;
            }
        }

        var removedCount = 0;
        foreach (var binding in bindings)
        {
            if (protectedBindingIds.Contains(binding.Id))
            {
                binding.SyncState = OpenApiSyncState.Conflict;
                binding.UpdateTime = now;
                await _db.Updateable(binding).ExecuteCommandAsync();
                result.ConflictCount++;
                continue;
            }

            await _db.Updateable<OpenApiMemberBinding>()
                .SetColumns(item => new OpenApiMemberBinding
                {
                    IsDeleted = true,
                    DeleteTime = now,
                    SyncState = OpenApiSyncState.Detached,
                    UpdateTime = now
                })
                .Where(item => item.SchemaBindingId == binding.Id && !item.IsDeleted)
                .ExecuteCommandAsync();
            await _db.Updateable<EntityField>()
                .SetColumns(item => new EntityField { IsDeleted = true, DeleteTime = now })
                .Where(item => item.ModuleEntityId == binding.ModuleEntityId && !item.IsDeleted)
                .ExecuteCommandAsync();
            await _db.Updateable<ModuleEntity>()
                .SetColumns(item => new ModuleEntity { IsDeleted = true, DeleteTime = now })
                .Where(item => item.Id == binding.ModuleEntityId && !item.IsDeleted)
                .ExecuteCommandAsync();
            binding.SyncState = OpenApiSyncState.Detached;
            binding.IsDeleted = true;
            binding.DeleteTime = now;
            binding.UpdateTime = now;
            await _db.Updateable(binding).ExecuteCommandAsync();
            removedCount++;
        }
        return removedCount;
    }

    private async Task<List<OpenApiAffectedPageDto>> MarkAffectedPagesAsync(long documentId, DateTime now)
    {
        var sourceIds = await _db.Queryable<ProjectDataSource>()
            .Where(item => item.OpenApiDocumentId == documentId)
            .Select(item => item.Id)
            .ToListAsync();
        if (sourceIds.Count == 0) return new List<OpenApiAffectedPageDto>();

        await _db.Updateable<PageDataSourceBinding>()
            .SetColumns(item => new PageDataSourceBinding
            {
                BindingStatus = PageBindingStatus.NeedsReview,
                UpdateTime = now
            })
            .Where(item => sourceIds.Contains(item.DataSourceId) && !item.IsDeleted)
            .ExecuteCommandAsync();
        await _db.Updateable<PageComponentBinding>()
            .SetColumns(item => new PageComponentBinding
            {
                BindingStatus = PageBindingStatus.NeedsReview,
                UpdateTime = now
            })
            .Where(item => sourceIds.Contains(item.DataSourceId) && !item.IsDeleted)
            .ExecuteCommandAsync();
        var sourcePageIds = await _db.Queryable<PageDataSourceBinding>()
            .Where(item => sourceIds.Contains(item.DataSourceId) && item.PageId.HasValue && !item.IsDeleted)
            .Select(item => item.PageId!.Value)
            .Distinct()
            .ToListAsync();
        var componentPageIds = await _db.Queryable<PageComponentBinding>()
            .Where(item => sourceIds.Contains(item.DataSourceId) && !item.IsDeleted)
            .Select(item => item.PageId)
            .Distinct()
            .ToListAsync();
        var pageIds = sourcePageIds.Concat(componentPageIds).Distinct().ToList();
        if (pageIds.Count > 0)
        {
            await _db.Updateable<ProjectPage>()
                .SetColumns(item => new ProjectPage { SyncStatus = PageSyncStatus.NeedsSync, UpdateTime = now })
                .Where(item => pageIds.Contains(item.Id) && !item.IsDeleted)
                .ExecuteCommandAsync();
        }
        var pages = pageIds.Count == 0
            ? new List<ProjectPage>()
            : await _db.Queryable<ProjectPage>()
                .Where(item => pageIds.Contains(item.Id) && !item.IsDeleted)
                .OrderBy(item => item.OrderNum)
                .OrderBy(item => item.PageName)
                .ToListAsync();
        return pages.Select(item => new OpenApiAffectedPageDto
        {
            PageId = item.Id,
            PageKey = item.PageKey,
            PageName = item.PageName,
            SyncMode = item.SyncMode,
            SyncStatus = PageSyncStatus.NeedsSync
        }).ToList();
    }

    private static OpenApiSyncState GetSchemaState(
        NormalizedOpenApiSchema schema,
        OpenApiSchemaBinding? binding,
        ModuleEntity? entity)
    {
        if (schema.DtoName.Length > 100) return OpenApiSyncState.Conflict;
        if (binding == null)
        {
            if (entity != null && !IsDtoOnlyEntity(entity)) return OpenApiSyncState.Conflict;
            return OpenApiSyncState.Added;
        }
        if (entity == null || !IsDtoOnlyEntity(entity)) return OpenApiSyncState.Conflict;
        if (!entity.Name.Equals(binding.DtoName, StringComparison.Ordinal)) return OpenApiSyncState.Conflict;
        if (binding.SyncState == OpenApiSyncState.Detached) return OpenApiSyncState.Added;
        return string.Equals(binding.SchemaHash, schema.SchemaHash, StringComparison.OrdinalIgnoreCase)
            ? OpenApiSyncState.Active
            : OpenApiSyncState.Modified;
    }

    private static OpenApiSyncState GetMemberState(
        NormalizedOpenApiMember member,
        OpenApiMemberBinding? binding,
        EntityField? field)
    {
        if (member.MemberName.Length > 100) return OpenApiSyncState.Conflict;
        if (member.MemberKind != OpenApiMemberKind.Scalar && field != null) return OpenApiSyncState.Conflict;
        if (binding == null) return OpenApiSyncState.Added;
        if (member.MemberKind == OpenApiMemberKind.Scalar && field == null) return OpenApiSyncState.Conflict;
        if (binding.SyncState == OpenApiSyncState.Detached) return OpenApiSyncState.Added;
        if (field != null && HasManualFieldDrift(field, binding.SnapshotJson))
            return OpenApiSyncState.Conflict;
        if (string.Equals(binding.MemberHash, member.MemberHash, StringComparison.OrdinalIgnoreCase))
            return OpenApiSyncState.Active;
        if (!string.Equals(binding.JsonName, member.JsonName, StringComparison.Ordinal)
            || !string.Equals(binding.MemberName, member.MemberName, StringComparison.Ordinal))
            return OpenApiSyncState.RenameCandidate;
        return OpenApiSyncState.Modified;
    }

    private static bool HasManualFieldDrift(EntityField field, string snapshotJson)
    {
        try
        {
            var previous = JsonSerializer.Deserialize<NormalizedOpenApiMember>(snapshotJson, OpenApiContractParser.JsonOptions);
            if (previous == null) return false;
            return !field.Name.Equals(previous.MemberName, StringComparison.Ordinal)
                   || !field.DataType.Equals(previous.DataType ?? "object", StringComparison.Ordinal)
                   || field.IsNullable != previous.IsNullable
                   || field.IsRequired != previous.IsRequired
                   || field.MaxLength != previous.MaxLength
                   || !string.Equals(field.MinValue, ToInvariant(previous.Minimum), StringComparison.Ordinal)
                   || !string.Equals(field.MaxValue, ToInvariant(previous.Maximum), StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsDtoOnlyEntity(ModuleEntity entity)
        => !entity.HasPrimaryKey && entity.TableName == null && entity.IsReadOnly
           && !entity.HasTenant && !entity.HasDataPermission && !entity.HasAudit
           && !entity.HasSoftDelete && !entity.GenerateFrontend;

    private static OpenApiMemberBinding? FindMemberBinding(
        IEnumerable<OpenApiMemberBinding> bindings,
        NormalizedOpenApiMember member)
    {
        var list = bindings.ToList();
        return list.FirstOrDefault(item => item.MemberKey.Equals(member.MemberKey, StringComparison.Ordinal))
               ?? (!string.IsNullOrWhiteSpace(member.ExternalId)
                   ? list.FirstOrDefault(item => item.ExternalId != null && item.ExternalId.Equals(member.ExternalId, StringComparison.Ordinal))
                   : null)
               ?? list.FirstOrDefault(item => item.JsonName.Equals(member.JsonName, StringComparison.Ordinal));
    }

    private static OpenApiSchemaPreviewDto CreateSchemaPreview(NormalizedOpenApiSchema schema)
        => new()
        {
            SchemaKey = schema.SchemaKey,
            SchemaName = schema.SchemaName,
            DtoName = schema.DtoName,
            Description = schema.Description,
            DtoRole = schema.DtoRole,
            SchemaHash = schema.SchemaHash
        };

    private static OpenApiMemberPreviewDto ToMemberPreview(
        NormalizedOpenApiMember member,
        OpenApiMemberBinding? binding,
        EntityField? field,
        OpenApiSyncState state,
        IReadOnlyCollection<NormalizedOpenApiSchema> schemas)
    {
        var target = member.TargetSchemaKey == null
            ? null
            : schemas.FirstOrDefault(item => item.SchemaKey == member.TargetSchemaKey);
        return new OpenApiMemberPreviewDto
        {
            MemberKey = member.MemberKey,
            ExternalId = member.ExternalId,
            JsonName = member.JsonName,
            MemberName = member.MemberName,
            Description = member.Description,
            MemberKind = member.MemberKind,
            DataType = member.DataType,
            Format = member.Format,
            IsRequired = member.IsRequired,
            IsNullable = member.IsNullable,
            IsCollection = member.IsCollection,
            TargetSchemaKey = member.TargetSchemaKey,
            TargetDtoName = target?.DtoName,
            MinLength = member.MinLength,
            MaxLength = member.MaxLength,
            Minimum = member.Minimum,
            Maximum = member.Maximum,
            EnumValues = member.EnumValues.ToList(),
            IsUnsupported = member.IsUnsupported,
            MemberHash = member.MemberHash,
            ExistingFieldId = field?.Id ?? binding?.EntityFieldId,
            SyncState = state
        };
    }

    private static OpenApiChangeDto CreateSchemaChange(
        NormalizedOpenApiSchema schema,
        OpenApiSchemaBinding? binding,
        ModuleEntity? entity,
        OpenApiSyncState state)
        => new()
        {
            ChangeKey = $"schema:{schema.SchemaKey}",
            TargetType = "Schema",
            TargetName = schema.DtoName,
            State = state,
            IsBreaking = state is OpenApiSyncState.Modified or OpenApiSyncState.Conflict,
            Description = state switch
            {
                OpenApiSyncState.Added => $"新增 API 模型元数据 {schema.DtoName}，仅用于字段绑定和前端数据源生成。",
                OpenApiSyncState.Modified => $"DTO {schema.DtoName} 的结构已变化。",
                _ => $"DTO {schema.DtoName} 与现有非 DTO 实体冲突，或名称超过元数据限制。"
            },
            EntityId = entity?.Id ?? binding?.ModuleEntityId,
            PreviousSnapshotJson = binding?.SnapshotJson,
            CurrentSnapshotJson = JsonSerializer.Serialize(schema, OpenApiContractParser.JsonOptions)
        };

    private static OpenApiChangeDto CreateMemberChange(
        NormalizedOpenApiSchema schema,
        NormalizedOpenApiMember member,
        OpenApiMemberBinding? binding,
        EntityField? field,
        OpenApiSyncState state)
        => new()
        {
            ChangeKey = $"member:{member.MemberKey}",
            TargetType = "Member",
            TargetName = member.MemberName,
            ParentName = schema.DtoName,
            State = state,
            IsBreaking = state is OpenApiSyncState.Modified or OpenApiSyncState.RenameCandidate or OpenApiSyncState.Conflict,
            Description = state switch
            {
                OpenApiSyncState.Added => $"新增字段 {schema.DtoName}.{member.MemberName}。",
                OpenApiSyncState.Modified => $"字段 {schema.DtoName}.{member.MemberName} 的类型或约束已变化。",
                OpenApiSyncState.RenameCandidate => $"字段 {schema.DtoName}.{binding?.MemberName} 可能重命名为 {member.MemberName}。",
                _ => $"字段 {schema.DtoName}.{member.MemberName} 同时存在本地修改和 OpenAPI 变化，需人工处理。"
            },
            EntityId = field?.ModuleEntityId,
            FieldId = field?.Id ?? binding?.EntityFieldId,
            PreviousSnapshotJson = binding?.SnapshotJson,
            CurrentSnapshotJson = JsonSerializer.Serialize(member, OpenApiContractParser.JsonOptions)
        };

    private static ModuleEntity CreateMetadataOnlyEntity(
        ImportContext context,
        NormalizedOpenApiSchema schema,
        DateTime now)
    {
        var entity = new ModuleEntity
        {
            ProjectId = context.Project.Id,
            ModuleId = context.Module.Id,
            Name = schema.DtoName,
            TenantId = context.Project.TenantId,
            CreateBy = "CodeMaster OpenAPI",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now
        };
        ApplyMetadataOnlyEntityMetadata(entity, schema, now);
        return entity;
    }

    private static void ApplyMetadataOnlyEntityMetadata(
        ModuleEntity entity,
        NormalizedOpenApiSchema schema,
        DateTime now)
    {
        entity.Name = schema.DtoName;
        entity.Description = TruncateRequired(schema.Description ?? schema.SchemaName, 200);
        entity.GenerationMode = EntityGenerationMode.MetadataOnly;
        entity.HasPrimaryKey = false;
        entity.TableName = null;
        entity.IsTree = false;
        entity.IsReadOnly = true;
        entity.HasTenant = false;
        entity.HasDataPermission = false;
        entity.HasAudit = false;
        entity.HasSoftDelete = false;
        entity.GenerateFrontend = false;
        entity.FrontendRoute = null;
        entity.MenuIcon = null;
        entity.IsChildTable = false;
        entity.IsGenerated = false;
        entity.LastGeneratedTime = null;
        entity.UpdateBy = "CodeMaster OpenAPI";
        entity.UpdateUserId = 0;
        entity.UpdateTime = now;
    }

    private static EntityField CreateField(
        long entityId,
        long tenantId,
        NormalizedOpenApiMember member,
        DateTime now,
        int order)
    {
        var field = new EntityField
        {
            ModuleEntityId = entityId,
            TenantId = tenantId,
            CreateBy = "CodeMaster OpenAPI",
            CreateUserId = 0,
            UpdateUserId = 0,
            CreateTime = now,
            OrderNum = order
        };
        ApplyFieldMetadata(field, member, now, isNew: true);
        return field;
    }

    private static void ApplyFieldMetadata(
        EntityField field,
        NormalizedOpenApiMember member,
        DateTime now,
        bool isNew = false)
    {
        field.Name = member.MemberName;
        field.Description = TruncateRequired(member.Description ?? member.JsonName, 200);
        field.IsSystemField = false;
        field.DataType = TruncateRequired(member.DataType ?? "object", 50);
        field.IsNullable = member.IsNullable;
        field.MaxLength = member.MaxLength;
        field.ColumnName = member.JsonName;
        field.IsIgnore = true;
        field.IsPrimaryKey = false;
        field.IsRequired = member.IsRequired;
        field.MinValue = ToInvariant(member.Minimum);
        field.MaxValue = ToInvariant(member.Maximum);
        field.IsEmail = string.Equals(member.Format, "email", StringComparison.OrdinalIgnoreCase);
        field.IsPhone = false;
        field.FieldCategory = "Normal";
        if (isNew)
        {
            field.ShowInList = true;
            field.ShowInDetail = true;
            field.ShowInAddForm = true;
            field.ShowInEditForm = true;
            field.ShowInSearch = false;
            field.FormControlType = InferControlType(member);
            field.SelectDataSource = member.EnumValues.Count > 0 ? "enum" : null;
            field.SelectOptions = member.EnumValues.Count > 0
                ? JsonSerializer.Serialize(member.EnumValues)
                : null;
        }
        field.UpdateBy = "CodeMaster OpenAPI";
        field.UpdateUserId = 0;
        field.UpdateTime = now;
    }

    private static string InferControlType(NormalizedOpenApiMember member)
    {
        if (member.EnumValues.Count > 0) return "select";
        return member.DataType switch
        {
            "bool" => "switch",
            "DateTime" => "datetime",
            "DateOnly" => "date",
            "int" or "long" or "float" or "double" or "decimal" => "number",
            "byte[]" => "upload",
            _ when member.MaxLength > 300 => "textarea",
            _ => "input"
        };
    }

    private static void ApplyDocumentMetadata(OpenApiDocumentRegistration document, ImportContext context, DateTime now)
    {
        document.Name = context.Name;
        document.SourceUrl = context.SourceUrl;
        document.Title = Truncate(context.Parsed.Title, 300);
        document.Description = Truncate(context.Parsed.Description, 2000);
        document.OpenApiVersion = TruncateRequired(context.Parsed.OpenApiVersion, 50);
        document.DocumentHash = context.Parsed.DocumentHash;
        document.RawDocument = context.Content;
        document.NormalizedSnapshotJson = JsonSerializer.Serialize(context.Parsed, OpenApiContractParser.JsonOptions);
        document.Status = OpenApiDocumentStatus.Active;
        document.LastSyncedTime = now;
        document.LastError = null;
        document.UpdateBy = "CodeMaster OpenAPI";
        document.UpdateUserId = 0;
        document.UpdateTime = now;
    }

    private static void ApplySchemaBindingMetadata(
        OpenApiSchemaBinding binding,
        NormalizedOpenApiSchema schema,
        DateTime now)
    {
        binding.SchemaName = TruncateRequired(schema.SchemaName, 300);
        binding.DtoName = TruncateRequired(schema.DtoName, 300);
        binding.DtoRole = schema.DtoRole;
        binding.SchemaHash = schema.SchemaHash;
        binding.SnapshotJson = JsonSerializer.Serialize(schema, OpenApiContractParser.JsonOptions);
        binding.SyncState = OpenApiSyncState.Active;
        binding.LastSyncedTime = now;
        binding.UpdateBy = "CodeMaster OpenAPI";
        binding.UpdateUserId = 0;
        binding.UpdateTime = now;
    }

    private static void ApplyMemberBindingMetadata(
        OpenApiMemberBinding binding,
        NormalizedOpenApiMember member,
        long? fieldId,
        long? targetBindingId,
        DateTime now)
    {
        binding.MemberKey = member.MemberKey;
        binding.ExternalId = member.ExternalId;
        binding.JsonName = TruncateRequired(member.JsonName, 300);
        binding.MemberName = TruncateRequired(member.MemberName, 300);
        binding.MemberKind = member.MemberKind;
        binding.DataType = Truncate(member.DataType, 300);
        binding.Format = Truncate(member.Format, 100);
        binding.IsRequired = member.IsRequired;
        binding.IsNullable = member.IsNullable;
        binding.IsCollection = member.IsCollection;
        binding.EntityFieldId = fieldId;
        binding.TargetSchemaBindingId = targetBindingId;
        binding.MemberHash = member.MemberHash;
        binding.SnapshotJson = JsonSerializer.Serialize(member, OpenApiContractParser.JsonOptions);
        binding.SyncState = member.IsUnsupported ? OpenApiSyncState.Conflict : OpenApiSyncState.Active;
        binding.LastSyncedTime = now;
        binding.UpdateBy = "CodeMaster OpenAPI";
        binding.UpdateUserId = 0;
        binding.UpdateTime = now;
    }

    private static void ApplyOperationMetadata(
        ProjectDataSource source,
        OpenApiDocumentRegistration document,
        NormalizedOpenApiOperation operation,
        OpenApiSchemaBinding? requestBinding,
        OpenApiSchemaBinding? responseBinding,
        OpenApiSchemaBinding? dtoBinding,
        IReadOnlyDictionary<string, NormalizedOpenApiSchema> schemaByKey,
        DateTime now)
    {
        source.DisplayName = TruncateRequired(operation.Summary ?? operation.OperationId, 200);
        source.SourceType = PageDataSourceType.Api;
        source.EntityId = dtoBinding?.ModuleEntityId;
        source.ServiceName = TruncateRequired(document.Title ?? document.Name, 200);
        source.MethodName = TruncateRequired(operation.OperationId, 200);
        source.DtoName = TruncateRequired(dtoBinding?.DtoName ?? "object", 300);
        source.DataShape = operation.DataShape;
        source.SyncMode = PageSyncMode.Auto;
        source.ParamsJson = JsonSerializer.Serialize(operation.Parameters, OpenApiContractParser.JsonOptions);
        source.ViewName = null;
        source.OpenApiDocumentId = document.Id;
        source.OperationKey = operation.OperationKey;
        source.HttpMethod = Truncate(operation.HttpMethod, 20);
        source.RoutePath = Truncate(operation.RoutePath, 1000);
        source.Summary = Truncate(operation.Summary, 500);
        source.Description = Truncate(operation.Description, 2000);
        source.RequestEntityId = requestBinding?.ModuleEntityId;
        source.ResponseEntityId = responseBinding?.ModuleEntityId;
        source.OperationContractJson = JsonSerializer.Serialize(operation, OpenApiContractParser.JsonOptions);
        source.OpenApiSyncState = OpenApiSyncState.Active;
        source.SchemaHash = ComputeOperationSchemaHash(operation, schemaByKey);
        source.LastSyncedTime = now;
        source.IsEnabled = true;
        source.UpdateBy = "CodeMaster OpenAPI";
        source.UpdateUserId = 0;
        source.UpdateTime = now;
    }

    private static string ComputeOperationSchemaHash(
        NormalizedOpenApiOperation operation,
        IReadOnlyDictionary<string, NormalizedOpenApiSchema> schemas)
        => OpenApiContractParser.HashJson(new
        {
            Operation = operation,
            RequestSchemaHash = operation.RequestSchemaKey != null && schemas.TryGetValue(operation.RequestSchemaKey, out var request)
                ? request.SchemaHash
                : null,
            ResponseSchemaHash = operation.ResponseSchemaKey != null && schemas.TryGetValue(operation.ResponseSchemaKey, out var response)
                ? response.SchemaHash
                : null,
            EnvelopeSchemaHash = operation.ResponseEnvelopeSchemaKey != null && schemas.TryGetValue(operation.ResponseEnvelopeSchemaKey, out var envelope)
                ? envelope.SchemaHash
                : null
        });

    private static string CreateSourceKey(string documentName, string operationKey)
        => $"api-{OpenApiContractParser.HashText($"{documentName}:{operationKey}")[..20]}";

    private async Task<string> DownloadDocumentAsync(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("OpenAPI 地址必须是有效的 HTTP/HTTPS URL。");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await _httpClientFactory.CreateClient().GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDocumentBytes)
            throw new ArgumentException("OpenAPI 文档超过 10 MB 导入限制。");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, timeout.Token);
            if (read == 0) break;
            if (buffer.Length + read > MaxDocumentBytes)
                throw new ArgumentException("OpenAPI 文档超过 10 MB 导入限制。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<Project> GetProjectAsync(long projectId)
        => await _db.Queryable<Project>()
            .Where(item => item.Id == projectId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"项目 {projectId} 不存在。");

    private static OpenApiDocumentDto ToDocumentDto(OpenApiDocumentRegistration item)
        => new()
        {
            Id = item.Id,
            ProjectId = item.ProjectId,
            ModuleId = item.ModuleId,
            Name = item.Name,
            SourceUrl = item.SourceUrl,
            Title = item.Title,
            Description = item.Description,
            OpenApiVersion = item.OpenApiVersion,
            DocumentHash = item.DocumentHash,
            Status = item.Status,
            LastSyncedTime = item.LastSyncedTime,
            LastError = item.LastError
        };

    private static List<OpenApiSchemaTreeMemberDto> BuildTreeMembers(
        long schemaBindingId,
        IReadOnlyCollection<OpenApiMemberBinding> members,
        IReadOnlyDictionary<long, OpenApiSchemaBinding> bindingById,
        HashSet<long> path,
        int depth)
    {
        if (depth >= 12 || !path.Add(schemaBindingId)) return new List<OpenApiSchemaTreeMemberDto>();
        var result = members.Where(item => item.SchemaBindingId == schemaBindingId)
            .OrderBy(item => item.JsonName, StringComparer.Ordinal)
            .Select(item =>
            {
                var target = item.TargetSchemaBindingId.HasValue
                    ? bindingById.GetValueOrDefault(item.TargetSchemaBindingId.Value)
                    : null;
                return new OpenApiSchemaTreeMemberDto
                {
                    FieldId = item.EntityFieldId,
                    JsonName = item.JsonName,
                    MemberName = item.MemberName,
                    DataType = item.DataType,
                    MemberKind = item.MemberKind,
                    IsRequired = item.IsRequired,
                    IsNullable = item.IsNullable,
                    IsCollection = item.IsCollection,
                    TargetEntityId = target?.ModuleEntityId,
                    TargetDtoName = target?.DtoName,
                    SyncState = item.SyncState,
                    Children = target == null
                        ? new List<OpenApiSchemaTreeMemberDto>()
                        : BuildTreeMembers(target.Id, members, bindingById, new HashSet<long>(path), depth + 1)
                };
            })
            .ToList();
        path.Remove(schemaBindingId);
        return result;
    }

    private static string? ToInvariant(decimal? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Truncate(string? value, int maxLength)
        => value == null ? null : value.Length <= maxLength ? value : value[..maxLength];

    private static string TruncateRequired(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static RemovalDecisions CreateRemovalDecisions(
        ApplyOpenApiImportDto input,
        OpenApiImportPreviewDto preview)
    {
        var pendingKeys = preview.Changes
            .Where(item => item.State == OpenApiSyncState.PendingDelete)
            .Select(item => item.ChangeKey)
            .ToHashSet(StringComparer.Ordinal);
        var requestedRemoveKeys = (input.RemoveChangeKeys ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var detachKeys = (input.DetachChangeKeys ?? new List<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToHashSet(StringComparer.Ordinal);

        var unknownKeys = requestedRemoveKeys.Concat(detachKeys)
            .Where(item => !pendingKeys.Contains(item))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknownKeys.Count > 0)
            throw new InvalidOperationException($"删除决策不属于当前待删除变更集：{string.Join("，", unknownKeys.Take(5))}");

        if (requestedRemoveKeys.Overlaps(detachKeys))
            throw new InvalidOperationException("同一个 OpenAPI 变更不能同时选择删除和本地保留。");
        if (input.ApplyRemovals && detachKeys.Count > 0)
            throw new InvalidOperationException("全量删除与本地保留决策不能同时使用。");

        var removeKeys = input.ApplyRemovals
            ? pendingKeys
            : requestedRemoveKeys;
        return new RemovalDecisions(removeKeys, detachKeys);
    }

    private enum RemovalAction
    {
        Pending,
        Remove,
        Detach
    }

    private sealed record RemovalDecisions(
        HashSet<string> RemoveKeys,
        HashSet<string> DetachKeys)
    {
        public RemovalAction GetAction(string changeKey)
        {
            if (RemoveKeys.Contains(changeKey)) return RemovalAction.Remove;
            return DetachKeys.Contains(changeKey) ? RemovalAction.Detach : RemovalAction.Pending;
        }
    }

    private sealed record ImportContext(
        Project Project,
        ProjectModule Module,
        OpenApiDocumentRegistration? ExistingDocument,
        string Name,
        string? SourceUrl,
        string Content,
        NormalizedOpenApiDocument Parsed);
}
