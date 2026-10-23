using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.Ai;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Dtos;
using CodeMaster.UiComposition.Entities;
using Microsoft.Extensions.AI;

namespace CodeMaster.UiComposition.Agent;

public sealed class UiCompositionAgentToolExtension : ICodeMasterAgentToolExtension
{
    public const string GetUiCompositionContextTool = "get_ui_composition_context";
    public const string GetViewBuilderContextTool = "get_view_builder_context";
    public const string PreviewOpenApiImportTool = "preview_openapi_import";
    public const string ProposeViewPlanTool = "propose_view_data_source_plan";
    public const string ProposeOpenApiImportTool = "propose_openapi_import";
    public const string GetContextTool = "get_complex_page_context";
    public const string ProposePlanTool = "propose_complex_page_plan";

    private static readonly Regex SafeIdentifier = new(
        "^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PascalIdentifier = new(
        "^[A-Z][A-Za-z0-9]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(AgentJsonSerializer.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static UiCompositionAgentToolExtension()
        => JsonOptions.Converters.Add(new JsonStringEnumConverter());

    private readonly IComplexPageAgentPlanningService _planningService;
    private readonly IProjectPageService _pageService;
    private readonly IProjectDataSourceService _dataSourceService;
    private readonly IProjectViewBuilderService _viewBuilderService;
    private readonly IOpenApiImportService _openApiImportService;
    private readonly IProjectModuleService _moduleService;
    private readonly IModuleEntityService _entityService;
    private readonly IAiAuthorizationService _authorizationService;
    private readonly IProjectViewDefinitionService _definitionService;

    public UiCompositionAgentToolExtension(
        IComplexPageAgentPlanningService planningService,
        IProjectPageService pageService,
        IProjectDataSourceService dataSourceService,
        IProjectViewBuilderService viewBuilderService,
        IOpenApiImportService openApiImportService,
        IProjectModuleService moduleService,
        IModuleEntityService entityService,
        IAiAuthorizationService authorizationService,
        IProjectViewDefinitionService definitionService)
    {
        _planningService = planningService;
        _pageService = pageService;
        _dataSourceService = dataSourceService;
        _viewBuilderService = viewBuilderService;
        _openApiImportService = openApiImportService;
        _moduleService = moduleService;
        _entityService = entityService;
        _authorizationService = authorizationService;
        _definitionService = definitionService;
    }

    public async Task<IReadOnlyCollection<AITool>> CreateToolsAsync(CodeMasterAgentExtensionContext context)
    {
        var tools = new List<AITool>();
        var canRead = await _authorizationService.HasAnyAsync(
            "codegen:projectpage:view",
            "codegen:projectpage:list");
        var canCreatePage = await _authorizationService.HasAnyAsync("codegen:projectpage:create");
        var canUpdatePage = await _authorizationService.HasAnyAsync("codegen:projectpage:update");
        var canCreateEntity = await _authorizationService.HasAnyAsync("codegen:moduleEntity:create");

        if (canRead)
        {
            tools.Add(AIFunctionFactory.Create(
                async (UiCompositionAgentContextQuery? query = null) =>
                {
                    query ??= new UiCompositionAgentContextQuery();
                    var result = new UiCompositionAgentContextDto { ProjectId = context.ProjectId };
                    if (query.IncludeDataSources)
                    {
                        result.DataSources = await _dataSourceService.GetListAsync(new DataSourceQueryDto
                        {
                            ProjectId = context.ProjectId
                        });
                    }
                    if (query.IncludeOpenApiDocuments)
                        result.OpenApiDocuments = await _openApiImportService.GetDocumentsAsync(context.ProjectId);
                    if (query.IncludeComplexPages)
                    {
                        result.ComplexPages = await _pageService.GetListAsync(new ProjectPageQueryDto
                        {
                            ProjectId = context.ProjectId
                        });
                    }
                    return JsonSerializer.Serialize(result, JsonOptions);
                },
                name: GetUiCompositionContextTool,
                description: "Read the private UI-composition inventory for the current project: registered entity/view/API data sources, imported OpenAPI documents, and complex pages. The query object is optional: {} includes all inventory; {\"query\":{\"includeDataSources\":false}} narrows the response."));

            tools.Add(AIFunctionFactory.Create(
                async () => JsonSerializer.Serialize(new
                {
                    projectId = context.ProjectId,
                    modules = await _moduleService.GetByProjectIdAsync(context.ProjectId),
                    catalog = await _viewBuilderService.GetEntityCatalogAsync(context.ProjectId),
                    definitions = await _definitionService.GetListAsync(context.ProjectId)
                }, JsonOptions),
                name: GetViewBuilderContextTool,
                description: "Read the CodeMaster project metadata used to plan a view: modules, entities, fields, inferred joins, and persisted view definitions. Generated files and physical database views are not planning inputs; physical views are checked only during deployment or import."));

            tools.Add(AIFunctionFactory.Create(
                async (OpenApiAgentImportRequestDto request) =>
                {
                    await ValidateOpenApiRequestAsync(context.ProjectId, request);
                    var preview = await _openApiImportService.PreviewAsync(BuildOpenApiPreview(context.ProjectId, request));
                    return JsonSerializer.Serialize(preview, JsonOptions);
                },
                name: PreviewOpenApiImportTool,
                description: "Fetch and preview one OpenAPI 3.x URL without mutation. Returns stable document/change-set hashes plus operation, DTO, field, removal, and conflict changes required by propose_openapi_import."));

            tools.Add(AIFunctionFactory.Create(
                async (ComplexPageAgentContextQuery? query = null) => JsonSerializer.Serialize(
                    await _planningService.GetContextAsync(context.ProjectId, query?.PageId),
                    JsonOptions),
                name: GetContextTool,
                description: "Read complex pages, entity service capabilities, display templates, and an optional complete structured page snapshot. The query object is optional: {} reads project context; {\"query\":{\"pageId\":\"123\"}} also reads that page. Use this before planning a complex page."));
        }

        if (canUpdatePage && canCreateEntity)
        {
            tools.Add(AIFunctionFactory.Create(
                async (ViewAgentPlanDto plan) =>
                {
                    try
                    {
                        var approvalPlan = await ValidateAndResolveViewPlanAsync(context.ProjectId, plan);
                        var executionId = await context.CreatePendingApprovalAsync(ProposeViewPlanTool, approvalPlan);
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            pendingApprovalId = executionId,
                            projectId = context.ProjectId,
                            action = ProposeViewPlanTool,
                            approvalPlan.Summary,
                            approvalPlan.ViewName,
                            approvalPlan.EntityName,
                            fieldCount = approvalPlan.ExpectedColumns.Count,
                            message = "视图方案已准备，请确认后应用到 CodeMaster。"
                        }, JsonOptions);
                    }
                    catch (Exception exception)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            projectId = context.ProjectId,
                            action = ProposeViewPlanTool,
                            diagnostic = exception.GetBaseException().Message,
                            message = "视图方案未应用，请根据提示修正后重试。"
                        }, JsonOptions);
                    }
                },
                name: ProposeViewPlanTool,
                description: "Validate final read-only SQL (or generate initial SQL from CodeMaster entity selections) plus user-declared field mappings, then prepare the CodeMaster view definition and read-only consumer metadata. Complex SQL mappings are trusted rather than deeply inferred."));
        }

        if (canCreatePage)
        {
            tools.Add(AIFunctionFactory.Create(
                async (OpenApiAgentImportPlanDto plan) =>
                {
                    var preview = await ValidateOpenApiPlanAsync(context.ProjectId, plan);
                    var executionId = await context.CreatePendingApprovalAsync(ProposeOpenApiImportTool, plan);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        pendingApprovalId = executionId,
                        projectId = context.ProjectId,
                        action = ProposeOpenApiImportTool,
                        plan.Summary,
                        preview.Title,
                        operationCount = preview.Operations.Count,
                        schemaCount = preview.Schemas.Count,
                        changeCount = preview.Changes.Count,
                        pendingDeleteCount = preview.Changes.Count(item => item.State == OpenApiSyncState.PendingDelete),
                        preview.Warnings,
                        message = "OpenAPI 元数据方案已准备，请确认后应用到 CodeMaster。"
                    }, JsonOptions);
                },
                name: ProposeOpenApiImportTool,
                description: "Re-run and validate an OpenAPI URL preview, lock it by document/change-set hashes, and prepare metadata-only DTO entities and API data sources. Call preview_openapi_import first. Backend CRUD code is never generated for imported DTO metadata."));
        }

        if (canCreatePage || canUpdatePage)
        {
            tools.Add(AIFunctionFactory.Create(
                async (ComplexPageAgentPlanDto plan) =>
                {
                    var validation = await _planningService.ValidateAsync(context.ProjectId, plan);
                    if (!validation.IsValid)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            projectId = context.ProjectId,
                            validation.Errors,
                            validation.Warnings,
                            validation.Suggestions,
                            message = "The structured complex-page plan is invalid. Correct it and submit a new plan."
                        }, JsonOptions);
                    }

                    var executionId = await context.CreatePendingApprovalAsync(ProposePlanTool, plan);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        pendingApprovalId = executionId,
                        projectId = context.ProjectId,
                        action = ProposePlanTool,
                        plan.Summary,
                        plan.ExistingPageId,
                        dataSourceCount = plan.Definition.DataSources.Count,
                        bindingCount = plan.Definition.Bindings.Count,
                        decisionCount = plan.Decisions.Count,
                        validation.Warnings,
                        message = "页面方案已准备，请确认后应用到 CodeMaster。"
                    }, JsonOptions);
                },
                name: ProposePlanTool,
                description: "Validate a structured complex-page definition for CodeMaster. Every data-source shape and explicit control override needs a reason; backend API changes need Swagger verification. Raw Vue source is rejected, and confirmed work is executed only through the page service and generator."));
        }

        return tools;
    }

    public bool CanExecute(string toolName) =>
        string.Equals(toolName, ProposeViewPlanTool, StringComparison.Ordinal) ||
        string.Equals(toolName, ProposeOpenApiImportTool, StringComparison.Ordinal) ||
        string.Equals(toolName, ProposePlanTool, StringComparison.Ordinal);

    public async Task DemandPermissionAsync(AiToolExecution execution)
    {
        switch (execution.ToolName)
        {
            case ProposeViewPlanTool:
            {
                var plan = Deserialize<ViewAgentApprovalPlanDto>(execution);
                await _authorizationService.DemandAnyAsync("codegen:projectpage:update");
                await _authorizationService.DemandAnyAsync("codegen:moduleEntity:create");
                break;
            }
            case ProposeOpenApiImportTool:
            {
                var plan = Deserialize<OpenApiAgentImportPlanDto>(execution);
                await _authorizationService.DemandAnyAsync("codegen:projectpage:create");
                break;
            }
            case ProposePlanTool:
            {
                var plan = Deserialize<ComplexPageAgentPlanDto>(execution);
                await _authorizationService.DemandAnyAsync(plan.ExistingPageId.HasValue
                    ? "codegen:projectpage:update"
                    : "codegen:projectpage:create");
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported UI composition Agent tool: {execution.ToolName}");
        }
    }

    public Task<object> ExecuteAsync(AiToolExecution execution) => execution.ToolName switch
    {
        ProposeViewPlanTool => ExecuteViewPlanAsync(execution),
        ProposeOpenApiImportTool => ExecuteOpenApiPlanAsync(execution),
        ProposePlanTool => ExecuteComplexPagePlanAsync(execution),
        _ => throw new InvalidOperationException($"Unsupported UI composition Agent tool: {execution.ToolName}")
    };

    private async Task<object> ExecuteViewPlanAsync(AiToolExecution execution)
    {
        var plan = Deserialize<ViewAgentApprovalPlanDto>(execution);
        var validation = await ValidateAndResolveViewPlanAsync(execution.ProjectId, plan);
        if (!string.Equals(validation.ResolvedSql, plan.ResolvedSql, StringComparison.Ordinal) ||
            !validation.ExpectedColumns.SequenceEqual(plan.ExpectedColumns, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The view-builder metadata changed after approval. Read the latest context and submit a new plan.");
        }

        var persisted = await ViewDefinitionPersistence.SaveDeployAndRegisterAsync(
            _definitionService, _viewBuilderService, execution.ProjectId, plan);
        var registered = persisted.Registered;
        var result = new ViewAgentExecutionResult
        {
            ProjectId = execution.ProjectId,
            EntityId = registered.EntityId,
            DataSourceId = registered.DataSourceId,
            ViewName = registered.ViewName,
            Summary = plan.Summary.Trim(),
            Warnings = persisted.Deployment.DatabaseResult?.Warnings ?? []
        };
        return result;
    }

    private async Task<object> ExecuteOpenApiPlanAsync(AiToolExecution execution)
    {
        var plan = Deserialize<OpenApiAgentImportPlanDto>(execution);
        _ = await ValidateOpenApiPlanAsync(execution.ProjectId, plan);
        var applied = await _openApiImportService.ApplyAsync(new ApplyOpenApiImportDto
        {
            ProjectId = execution.ProjectId,
            ModuleId = plan.ModuleId,
            DocumentId = plan.DocumentId,
            Name = plan.Name,
            SourceUrl = plan.SourceUrl,
            ExpectedDocumentHash = plan.ExpectedDocumentHash,
            ExpectedChangeSetHash = plan.ExpectedChangeSetHash,
            ApplyRemovals = plan.ApplyRemovals,
            RemoveChangeKeys = plan.RemoveChangeKeys,
            DetachChangeKeys = plan.DetachChangeKeys
        });
        var result = new OpenApiAgentExecutionResult
        {
            ProjectId = execution.ProjectId,
            DocumentId = applied.DocumentId,
            Summary = plan.Summary.Trim(),
            AddedCount = applied.AddedCount,
            ModifiedCount = applied.ModifiedCount,
            PendingDeleteCount = applied.PendingDeleteCount,
            RemovedCount = applied.RemovedCount,
            EntityIds = applied.EntityIds,
            DataSourceIds = applied.DataSourceIds,
            AffectedPageIds = applied.AffectedPageIds
        };
        return result;
    }

    private async Task<object> ExecuteComplexPagePlanAsync(AiToolExecution execution)
    {
        var plan = Deserialize<ComplexPageAgentPlanDto>(execution);
        var applied = await _planningService.ApplyAsync(execution.ProjectId, plan);
        var result = new ComplexPageAgentExecutionResult
        {
            ProjectId = applied.ProjectId,
            PageId = applied.PageId,
            Created = applied.Created,
            Summary = applied.Summary
        };
        return result;
    }

    private async Task<ViewAgentApprovalPlanDto> ValidateAndResolveViewPlanAsync(long projectId, ViewAgentPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var summary = plan.Summary?.Trim() ?? string.Empty;
        var reason = plan.Reason?.Trim() ?? string.Empty;
        var viewName = plan.ViewName?.Trim() ?? string.Empty;
        var entityName = plan.EntityName?.Trim() ?? string.Empty;
        var keyColumn = plan.KeyColumn?.Trim() ?? string.Empty;
        if (summary.Length == 0)
            throw new ArgumentException("The view plan summary is required.");
        if (reason.Length == 0)
            throw new ArgumentException("The view plan requires a concrete reason.");
        if (!SafeIdentifier.IsMatch(viewName))
            throw new ArgumentException("ViewName must be a safe database identifier.");
        if (!PascalIdentifier.IsMatch(entityName))
            throw new ArgumentException("EntityName must be an ASCII PascalCase identifier.");
        if (keyColumn.Length == 0)
            throw new ArgumentException("An explicit integral KeyColumn is required.");
        if (plan.Query == null)
            throw new ArgumentException("A view query is required.");

        var declaredFields = NormalizeDeclaredFields(plan.EntityFields);
        if (declaredFields.Count > 0)
        {
            var keyField = declaredFields.FirstOrDefault(item =>
                string.Equals(item.ColumnName, keyColumn, StringComparison.OrdinalIgnoreCase));
            if (keyField == null)
                throw new ArgumentException("KeyColumn must match a declared view entity field ColumnName.");
            if (!IsIntegralType(keyField.DataType))
                throw new ArgumentException("The declared KeyColumn must use an integral C# data type compatible with the generated long Id.");
        }

        await ResolveAndEnsureModuleAsync(projectId, plan);
        var moduleEntities = await _entityService.GetByModuleIdAsync(plan.ModuleId);
        var existingConsumer = moduleEntities.FirstOrDefault(item =>
                string.Equals(item.Name, entityName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.TableName, viewName, StringComparison.OrdinalIgnoreCase));
        if (existingConsumer != null)
        {
            var existingDefinition = (await _definitionService.GetListAsync(projectId))
                .FirstOrDefault(item => string.Equals(item.ViewName, viewName, StringComparison.OrdinalIgnoreCase));
            var isExactRegisteredConsumer = existingDefinition?.ModuleEntityId == existingConsumer.Id &&
                                            existingConsumer.IsReadOnly &&
                                            string.Equals(existingConsumer.Name, entityName, StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(existingConsumer.TableName, viewName, StringComparison.OrdinalIgnoreCase);
            var isExactAdoption = plan.ExistingEntityId == existingConsumer.Id &&
                                  existingConsumer.IsReadOnly &&
                                  string.Equals(existingConsumer.Name, entityName, StringComparison.OrdinalIgnoreCase) &&
                                  string.Equals(existingConsumer.TableName, viewName, StringComparison.OrdinalIgnoreCase);
            if (!isExactRegisteredConsumer && !isExactAdoption &&
                existingConsumer.IsReadOnly &&
                string.Equals(existingConsumer.Name, entityName, StringComparison.OrdinalIgnoreCase))
            {
                // A read-only entity carrying exactly the planned consumer name is the same view
                // consumer left behind by an earlier attempt, so reuse it instead of failing.
                plan.ExistingEntityId = existingConsumer.Id;
            }
            else if (!isExactRegisteredConsumer && !isExactAdoption)
            {
                throw new InvalidOperationException(
                    $"The planned view consumer '{entityName}' collides with the {(existingConsumer.IsReadOnly ? "read-only" : "writable")} entity '{existingConsumer.Name}' in the target module. " +
                    $"Rename the read-only consumer (for example '{entityName}View') and resubmit the view plan.");
            }
        }
        else if (plan.ExistingEntityId.HasValue)
            throw new InvalidOperationException($"Existing view entity {plan.ExistingEntityId.Value} was not found with the planned name and table mapping.");

        var metadataDefinition = ViewPlanMetadataRules.FindDefinition(
            await _definitionService.GetListAsync(projectId),
            viewName);
        ViewPlanMetadataRules.EnsureCanCreateOrReplace(metadataDefinition, plan.ReplaceExistingView);

        var query = new PreviewViewSqlDto
        {
            ProjectId = projectId,
            Sql = plan.Query.Sql,
            Entities = plan.Query.Entities,
            Joins = plan.Query.Joins,
            IncludeInferredJoins = plan.Query.IncludeInferredJoins
        };
        var preview = await _viewBuilderService.PreviewSqlAsync(query);
        if (string.IsNullOrWhiteSpace(query.Sql))
        {
            var key = preview.SelectedFields.FirstOrDefault(item =>
                string.Equals(item.Alias, keyColumn, StringComparison.OrdinalIgnoreCase));
            if (key == null)
                throw new ArgumentException("KeyColumn must match one selected view-column alias.");
            var catalog = await _viewBuilderService.GetEntityCatalogAsync(projectId);
            var keyField = catalog.Entities.SelectMany(item => item.Fields)
                .FirstOrDefault(item => item.FieldId == key.FieldId);
            if (keyField == null || !IsIntegralType(keyField.DataType))
                throw new ArgumentException("KeyColumn must originate from an integral entity field compatible with the long read-only entity key.");
        }

        return new ViewAgentApprovalPlanDto
        {
            Summary = summary,
            Reason = reason,
            Query = query,
            EntityFields = declaredFields,
            ViewName = viewName,
            ReplaceExistingView = plan.ReplaceExistingView,
            ModuleId = plan.ModuleId,
            ExistingEntityId = plan.ExistingEntityId,
            EntityName = entityName,
            DisplayName = string.IsNullOrWhiteSpace(plan.DisplayName) ? entityName : plan.DisplayName.Trim(),
            KeyColumn = keyColumn,
            GenerateFrontend = plan.GenerateFrontend,
            GenerateAfterApproval = plan.GenerateAfterApproval,
            Incremental = plan.Incremental,
            BuildAfterGeneration = plan.BuildAfterGeneration,
            ResolvedSql = preview.Sql,
            ExpectedColumns = declaredFields.Count > 0
                ? declaredFields.Select(item => item.ColumnName).ToList()
                : preview.SelectedFields.Select(item => item.Alias).ToList()
        };
    }

    private async Task<OpenApiImportPreviewDto> ValidateOpenApiPlanAsync(long projectId, OpenApiAgentImportPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.Summary))
            throw new ArgumentException("The OpenAPI import summary is required.");
        if (string.IsNullOrWhiteSpace(plan.ExpectedDocumentHash) || string.IsNullOrWhiteSpace(plan.ExpectedChangeSetHash))
            throw new ArgumentException("OpenAPI document and change-set hashes from preview_openapi_import are required.");
        await ValidateOpenApiRequestAsync(projectId, plan);

        var preview = await _openApiImportService.PreviewAsync(BuildOpenApiPreview(projectId, plan));
        if (!string.Equals(plan.ExpectedDocumentHash, preview.DocumentHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plan.ExpectedChangeSetHash, preview.ChangeSetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The OpenAPI document or change set changed after preview. Preview it again and submit the new hashes.");
        }
        var conflicts = preview.Changes.Where(item => item.State == OpenApiSyncState.Conflict).ToList();
        if (conflicts.Count > 0)
            throw new InvalidOperationException($"The OpenAPI preview contains {conflicts.Count} unresolved conflicts.");

        var pendingKeys = preview.Changes
            .Where(item => item.State == OpenApiSyncState.PendingDelete)
            .Select(item => item.ChangeKey)
            .ToHashSet(StringComparer.Ordinal);
        var removeKeys = NormalizeKeys(plan.RemoveChangeKeys);
        var detachKeys = NormalizeKeys(plan.DetachChangeKeys);
        if (removeKeys.Overlaps(detachKeys))
            throw new ArgumentException("One OpenAPI change cannot be both removed and detached.");
        if (removeKeys.Concat(detachKeys).Any(key => !pendingKeys.Contains(key)))
            throw new ArgumentException("Removal decisions must reference PendingDelete change keys from the current preview.");
        if (plan.ApplyRemovals && detachKeys.Count > 0)
            throw new ArgumentException("ApplyRemovals cannot be combined with detach decisions.");
        return preview;
    }

    private async Task ValidateOpenApiRequestAsync(long projectId, OpenApiAgentImportRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureModuleAsync(projectId, request.ModuleId);
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("The OpenAPI document name is required.");
        if (!Uri.TryCreate(request.SourceUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("SourceUrl must be an absolute HTTP or HTTPS OpenAPI URL.");
        }
    }

    /// <summary>
    /// Resolves the target module before it is validated.
    /// <para>
    /// <see cref="ViewAgentPlanDto.ModuleName"/> is documented as the fallback "Used when ModuleId
    /// is not known until earlier metadata tasks have run" — which is exactly the work-plan case,
    /// where the view task runs after the entity tasks have created the module. The work-plan
    /// adapter honours that fallback; this path went straight to the numeric check, so a plan that
    /// named its module failed with "ModuleId must be positive." instead of resolving it.
    /// </para>
    /// </summary>
    private async Task ResolveAndEnsureModuleAsync(long projectId, ViewAgentPlanDto plan)
    {
        if (plan.ModuleId <= 0 && !string.IsNullOrWhiteSpace(plan.ModuleName))
        {
            var module = (await _moduleService.GetByProjectIdAsync(projectId))
                .FirstOrDefault(item => string.Equals(
                    item.ModuleName,
                    plan.ModuleName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            if (module != null)
                plan.ModuleId = module.Id;
        }

        await EnsureModuleAsync(projectId, plan.ModuleId, plan.ModuleName);
    }

    private async Task EnsureModuleAsync(long projectId, long moduleId, string? moduleName = null)
    {
        if (moduleId <= 0)
        {
            var known = string.Join(", ", (await _moduleService.GetByProjectIdAsync(projectId))
                .Select(item => item.ModuleName)
                .Take(20));
            throw new ArgumentException(
                $"ModuleId must be positive. ModuleName '{moduleName}' did not resolve to a module in project {projectId}. Known modules: [{known}].");
        }
        var modules = await _moduleService.GetByProjectIdAsync(projectId);
        if (!modules.Any(item => item.Id == moduleId))
            throw new InvalidOperationException($"Module {moduleId} does not belong to project {projectId}.");
    }

    private static PreviewOpenApiImportDto BuildOpenApiPreview(long projectId, OpenApiAgentImportRequestDto request) => new()
    {
        ProjectId = projectId,
        ModuleId = request.ModuleId,
        DocumentId = request.DocumentId,
        Name = request.Name.Trim(),
        SourceUrl = request.SourceUrl.Trim()
    };

    private static HashSet<string> NormalizeKeys(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToHashSet(StringComparer.Ordinal);

    private static List<ViewEntityFieldDefinitionDto> NormalizeDeclaredFields(
        IEnumerable<ViewEntityFieldDefinitionDto>? fields)
    {
        var normalized = (fields ?? Array.Empty<ViewEntityFieldDefinitionDto>())
            .Select((field, index) =>
            {
                var name = field.Name?.Trim() ?? string.Empty;
                if (!PascalIdentifier.IsMatch(name))
                    throw new ArgumentException($"Entity field name '{name}' must be an ASCII PascalCase identifier.");
                var columnName = string.IsNullOrWhiteSpace(field.ColumnName)
                    ? name
                    : field.ColumnName.Trim();
                if (columnName.Length == 0 || columnName.Length > 100)
                    throw new ArgumentException($"Entity field '{name}' requires a SQL output column name of at most 100 characters.");
                var dataType = CSharpDataTypeNormalizer.Normalize(field.DataType);
                if (!Regex.IsMatch(dataType, "^[A-Za-z_][A-Za-z0-9_.]*(?:\\[\\])?\\??$"))
                    throw new ArgumentException($"Entity field '{name}' has an unsafe C# data type.");
                return new ViewEntityFieldDefinitionDto
                {
                    Name = name,
                    DisplayName = string.IsNullOrWhiteSpace(field.DisplayName) ? name : field.DisplayName.Trim(),
                    ColumnName = columnName,
                    DataType = dataType,
                    IsNullable = field.IsNullable,
                    MaxLength = field.MaxLength,
                    Precision = field.Precision,
                    Scale = field.Scale,
                    ShowInList = field.ShowInList,
                    ShowInDetail = field.ShowInDetail,
                    ShowInSearch = field.ShowInSearch,
                    IsSortable = field.IsSortable,
                    FormControlType = string.IsNullOrWhiteSpace(field.FormControlType) ? null : field.FormControlType.Trim(),
                    OrderNum = field.OrderNum == 0 ? index : field.OrderNum
                };
            })
            .ToList();

        var duplicateName = normalized.GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName != null)
            throw new ArgumentException($"Duplicate entity field name: {duplicateName.Key}.");
        var duplicateColumn = normalized.GroupBy(field => field.ColumnName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateColumn != null)
            throw new ArgumentException($"Duplicate SQL output column mapping: {duplicateColumn.Key}. Use distinct AS aliases.");
        return normalized;
    }

    private static bool IsIntegralType(string? dataType)
    {
        var value = (dataType ?? string.Empty).Trim().TrimEnd('?').ToLowerInvariant();
        return value is "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or
            "int16" or "int32" or "int64" or "integer" or "smallint" or "bigint" or "tinyint";
    }

    private static T Deserialize<T>(AiToolExecution execution)
    {
        if (string.IsNullOrWhiteSpace(execution.InputJson))
            throw new InvalidOperationException("The UI composition Agent payload is missing.");
        return JsonSerializer.Deserialize<T>(AiToolPayloadCodec.Decode(execution.InputJson), JsonOptions)
            ?? throw new InvalidOperationException("The UI composition Agent payload is invalid.");
    }
}

public sealed class ComplexPageAgentExecutionResult : IAgentClientActionResult
{
    public long ProjectId { get; set; }
    public long PageId { get; set; }
    public bool Created { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<AiClientActionDto> ClientActions { get; set; } = new();
    public bool HasClientActions => ClientActions.Count > 0;
}
