using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Extensibility;
using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;
using CodeMaster.McpServer.Services;
using CodeMaster.Domain.Entities.CodeGen;
using ModelContextProtocol.Server;

namespace CodeMaster.McpServer.Tools;

public interface IUiCompositionMcpToolHandler : ICodeMasterExtension
{
    Task<object> GetContextAsync(long projectId, long? pageId, string? workspacePath);

    Task<object> GetViewBuilderContextAsync(long projectId, string? workspacePath);

    Task<object> PreviewViewSqlAsync(long projectId, PreviewViewSqlDto input, string? workspacePath);

    Task<object> SaveViewDefinitionAsync(long projectId, SaveProjectViewDefinitionDto input, string? workspacePath);

    Task<object> DeployViewDefinitionAsync(long projectId, DeployProjectViewDefinitionDto input, bool confirmApply, string? workspacePath);

    Task<object> RegisterReadOnlyViewEntityAsync(long projectId, RegisterReadOnlyViewEntityDto input, bool confirmApply, string? workspacePath);

    Task<object> ImportExistingViewAsync(long projectId, RegisterReadOnlyViewEntityDto input, bool confirmApply, string? workspacePath);

    Task<object> MaterializeProjectAsync(long projectId, bool confirmApply, string? workspacePath);

    Task<object> ProposePlanAsync(
        long projectId,
        ComplexPageAgentPlanDto plan,
        bool confirmApply,
        string? workspacePath);
}

public sealed class UiCompositionMcpToolHandler : IUiCompositionMcpToolHandler
{
    private const int MaxOutputLength = 12_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly CodeMasterApiClient _apiClient;
    private readonly ProjectContextResolver _contextResolver;
    private readonly LocalCodegenExecutionService _localExecutor;

    public UiCompositionMcpToolHandler(
        CodeMasterApiClient apiClient,
        ProjectContextResolver contextResolver,
        LocalCodegenExecutionService localExecutor)
    {
        _apiClient = apiClient;
        _contextResolver = contextResolver;
        _localExecutor = localExecutor;
    }

    public async Task<object> GetContextAsync(long projectId, long? pageId, string? workspacePath)
    {
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        var pageQuery = pageId.HasValue ? $"?pageId={pageId.Value}" : string.Empty;
        var context = await _apiClient.GetJsonAsync(
            $"/api/codegen/complexpageagentplanning/getcontext/{projectId}{pageQuery}",
            workspacePath);
        return new
        {
            success = true,
            projectId = projectId.ToString(),
            context,
            message = "Complex-page context loaded. Use only the returned service capabilities, fields, display templates, page ids, and definitionVersion values when planning changes."
        };
    }

    public async Task<object> GetViewBuilderContextAsync(long projectId, string? workspacePath)
    {
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        var catalog = await _apiClient.GetJsonAsync(
            $"/api/codegen/projectviewbuilder/getentitycatalog/{projectId}",
            workspacePath);
        JsonElement definitions;
        try
        {
            definitions = await _apiClient.GetJsonAsync(
                $"/api/codegen/projectviewdefinition/getlist/{projectId}",
                workspacePath);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            definitions = JsonSerializer.SerializeToElement(Array.Empty<object>());
        }

        return new
        {
            success = true,
            projectId = projectId.ToString(),
            catalog,
            definitions,
            message = "View-builder context loaded from CodeMaster project metadata. Use entity/field ColumnName values and persisted view definitions for planning; generated files and physical database views are not planning inputs. Physical views are inspected only during deployment, import, or drift verification."
        };
    }

    public async Task<object> PreviewViewSqlAsync(long projectId, PreviewViewSqlDto input, string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(input);
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        input.ProjectId = projectId;
        var preview = await _apiClient.PostAsync<ViewBuilderSqlPreviewDto>(
            "/api/codegen/projectviewbuilder/previewsql",
            input,
            workspacePath);
        return new
        {
            success = true,
            projectId = projectId.ToString(),
            preview,
            message = "View SQL preview completed without changing the target database or CodeMaster metadata."
        };
    }

    public async Task<object> SaveViewDefinitionAsync(long projectId, SaveProjectViewDefinitionDto input, string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(input);
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        input.ProjectId = projectId;
        var saved = await _apiClient.PostAsync<ProjectViewDefinitionDto>(
            "/api/codegen/projectviewdefinition/save",
            input,
            workspacePath);
        return new
        {
            success = true,
            saved,
            message = "View definition, declared fields, dependencies, and lineage were saved. No physical view was deployed."
        };
    }

    public async Task<object> DeployViewDefinitionAsync(
        long projectId,
        DeployProjectViewDefinitionDto input,
        bool confirmApply,
        string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(input);
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        if (!confirmApply)
        {
            return new
            {
                success = true,
                applied = false,
                requiresConfirmation = true,
                projectId = projectId.ToString(),
                definitionId = input.DefinitionId.ToString(),
                message = "The deployment is validated as an explicit CodeMaster operation but has not been applied. Repeat with confirmApply=true to deploy or replace the physical view."
            };
        }

        var deployed = await _apiClient.PostAsync<ProjectViewDeploymentResultDto>(
            "/api/codegen/projectviewdefinition/deploy",
            input,
            workspacePath);
        return new
        {
            success = true,
            applied = true,
            projectId = projectId.ToString(),
            deployed,
            message = deployed.AlreadyApplied
                ? "The same view definition hash was already applied; CodeMaster returned the recorded result."
                : "The physical view was deployed from the versioned CodeMaster definition."
        };
    }

    public async Task<object> RegisterReadOnlyViewEntityAsync(
        long projectId,
        RegisterReadOnlyViewEntityDto input,
        bool confirmApply,
        string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(input);
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        input.ProjectId = projectId;
        if (!confirmApply)
        {
            return new
            {
                success = true,
                applied = false,
                requiresConfirmation = true,
                projectId = projectId.ToString(),
                viewName = input.ViewName,
                entityName = input.EntityName,
                message = "The read-only consumer registration is ready but has not been applied. Repeat with confirmApply=true to persist the entity/data-source metadata."
            };
        }

        var registered = await _apiClient.PostAsync<ReadOnlyViewEntityResultDto>(
            "/api/codegen/projectviewbuilder/registerreadonlyviewentity",
            input,
            workspacePath);
        return new
        {
            success = true,
            applied = true,
            projectId = projectId.ToString(),
            registered,
            message = "The read-only view consumer was registered through CodeMaster metadata services."
        };
    }

    public async Task<object> ImportExistingViewAsync(
        long projectId,
        RegisterReadOnlyViewEntityDto input,
        bool confirmApply,
        string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(input);
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        input.ProjectId = projectId;
        input.DataSourceType = PageDataSourceType.View;
        if (!confirmApply)
        {
            return new
            {
                success = true,
                applied = false,
                requiresConfirmation = true,
                projectId = projectId.ToString(),
                viewName = input.ViewName,
                entityName = input.EntityName,
                message = "The existing database view import is ready but has not been applied. Repeat with confirmApply=true to register it as an imported read-only consumer."
            };
        }

        var imported = await _apiClient.PostAsync<ReadOnlyViewEntityResultDto>(
            "/api/codegen/projectviewbuilder/importexistingview",
            input,
            workspacePath);
        return new
        {
            success = true,
            applied = true,
            projectId = projectId.ToString(),
            imported,
            message = "The existing database view was imported through CodeMaster without claiming ownership of its SQL."
        };
    }

    public async Task<object> MaterializeProjectAsync(long projectId, bool confirmApply, string? workspacePath)
    {
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        if (!confirmApply)
        {
            return new
            {
                success = true,
                applied = false,
                requiresConfirmation = true,
                projectId = projectId.ToString(),
                actions = new[] { "generateProjectSmartCode", "migrateDatabase", "generateComplexPages", "buildProject", "testProject" },
                message = "The project materialization pipeline is ready but has not run. Repeat with confirmApply=true to execute it through LocalAgent."
            };
        }

        var session = await _apiClient.ResolveSessionAsync(workspacePath);
        var projectRoot = (await _contextResolver.ResolveAsync(workspacePath))?.ProjectRoot;
        var results = new List<object>();
        var generation = await ExecuteLocalAsync("generateProjectSmartCode", projectId, null, true, projectRoot, session);
        results.Add(CompactMaterializationResult(generation));
        if (!generation.Success)
            return new { success = false, applied = true, projectId = projectId.ToString(), results, message = "Entity generation failed; later materialization steps were not started." };

        var migration = await ExecuteLocalAsync("migrateDatabase", projectId, null, true, projectRoot, session);
        results.Add(CompactMaterializationResult(migration));
        if (!migration.Success)
            return new { success = false, applied = true, projectId = projectId.ToString(), results, message = "Database migration/view deployment failed; build and verification were not started." };

        JsonElement pages;
        try
        {
            pages = await _apiClient.GetJsonAsync(
                $"/api/codegen/projectpage/getlist?projectId={projectId}",
                workspacePath);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            pages = JsonSerializer.SerializeToElement(Array.Empty<ProjectPageDto>());
        }
        var pageList = pages.Deserialize<List<ProjectPageDto>>(JsonOptions) ?? [];
        foreach (var page in pageList)
        {
            var pageResult = await ExecuteLocalAsync("generateComplexPage", projectId, page.Id, true, projectRoot, session);
            results.Add(CompactMaterializationResult(pageResult));
            if (!pageResult.Success)
                return new { success = false, applied = true, projectId = projectId.ToString(), results, message = $"Complex page '{page.PageKey}' generation failed; build and verification were not started." };
        }

        var build = await ExecuteLocalAsync("buildProject", projectId, null, true, projectRoot, session);
        results.Add(CompactMaterializationResult(build));
        if (!build.Success)
            return new { success = false, applied = true, projectId = projectId.ToString(), results, message = "Generated project build failed; tests were not started." };

        var test = await ExecuteLocalAsync("testProject", projectId, null, true, projectRoot, session);
        results.Add(CompactMaterializationResult(test));
        return new
        {
            success = test.Success,
            applied = true,
            projectId = projectId.ToString(),
            results,
            message = test.Success
                ? "Project materialization completed through LocalAgent: entities, database/views, complex pages, build, and tests."
                : "Project materialization reached the test step, which failed. Review the returned diagnostics."
        };
    }

    public async Task<object> ProposePlanAsync(
        long projectId,
        ComplexPageAgentPlanDto plan,
        bool confirmApply,
        string? workspacePath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        projectId = await ResolveProjectIdAsync(projectId, workspacePath);
        if (plan.Definition.ProjectId == 0)
            plan.Definition.ProjectId = projectId;

        var validation = await _apiClient.PostAsync<ComplexPageAgentPlanValidationDto>(
            $"/api/codegen/complexpageagentplanning/validate/{projectId}",
            plan,
            workspacePath);
        if (!validation.IsValid)
        {
            return new
            {
                success = false,
                applied = false,
                projectId = projectId.ToString(),
                validation.Errors,
                validation.Warnings,
                validation.Suggestions,
                message = "The complex-page plan is invalid. Read the latest context, correct the structured definition, and submit it again."
            };
        }

        if (!confirmApply)
        {
            return new
            {
                success = true,
                applied = false,
                requiresConfirmation = true,
                projectId = projectId.ToString(),
                plan.Summary,
                existingPageId = plan.ExistingPageId?.ToString(),
                expectedDefinitionVersion = plan.ExpectedDefinitionVersion,
                dataSourceCount = plan.Definition.DataSources.Count,
                bindingCount = plan.Definition.Bindings.Count,
                decisionCount = plan.Decisions.Count,
                backendChangeCount = plan.BackendChanges.Count,
                plan.GenerateAfterApproval,
                plan.Incremental,
                plan.BuildAfterGeneration,
                validation.Warnings,
                validation.Suggestions,
                message = "The plan is valid but has not been applied. Ask the user to approve this exact plan, then call propose_complex_page_plan again with confirmApply=true."
            };
        }

        var applied = await _apiClient.PostAsync<ComplexPageAgentApplyResultDto>(
            $"/api/codegen/complexpageagentplanning/apply/{projectId}",
            plan,
            workspacePath);
        var projectRoot = (await _contextResolver.ResolveAsync(workspacePath))?.ProjectRoot;
        var session = await _apiClient.ResolveSessionAsync(workspacePath);

        LocalExecutionResult? generation = null;
        LocalExecutionResult? build = null;
        if (plan.GenerateAfterApproval)
        {
            generation = await ExecuteLocalAsync(
                "generateComplexPage",
                projectId,
                applied.PageId,
                plan.Incremental,
                projectRoot,
                session);
            if (!generation.Success)
                return BuildAppliedResult(projectId, applied, validation, generation, null);
        }

        if (plan.BuildAfterGeneration)
        {
            build = await ExecuteLocalAsync(
                "buildProject",
                projectId,
                applied.PageId,
                plan.Incremental,
                projectRoot,
                session);
        }

        return BuildAppliedResult(projectId, applied, validation, generation, build);
    }

    private async Task<long> ResolveProjectIdAsync(long projectId, string? workspacePath)
    {
        var resolved = await McpProjectContextHelper.ResolveProjectIdAsync(
            _contextResolver,
            projectId,
            workspacePath);
        if (resolved <= 0)
            throw new ArgumentException("projectId is required and could not be resolved from project context.");
        return resolved;
    }

    private async Task<LocalExecutionResult> ExecuteLocalAsync(
        string action,
        long projectId,
        long? pageId,
        bool incremental,
        string? projectRoot,
        McpSession session)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            projectId,
            pageId,
            targetPath = projectRoot,
            incremental
        });
        return await _localExecutor.ExecuteAsync(action, new LocalExecutionRequest
        {
            Action = action,
            Payload = payload,
            ServerBaseUrl = session.ServerBaseUrl,
            AccessToken = session.AccessToken
        });
    }

    private static object CompactMaterializationResult(LocalExecutionResult result)
    {
        var output = result.Output ?? string.Empty;
        if (output.Length > MaxOutputLength)
            output = output[..MaxOutputLength] + $"\n... {output.Length - MaxOutputLength} characters omitted ...";
        return new { result.Success, result.Message, result.Data, output };
    }

    private static object BuildAppliedResult(
        long projectId,
        ComplexPageAgentApplyResultDto applied,
        ComplexPageAgentPlanValidationDto validation,
        LocalExecutionResult? generation,
        LocalExecutionResult? build)
    {
        var success = generation?.Success != false && build?.Success != false;
        return new
        {
            success,
            applied = true,
            projectId = projectId.ToString(),
            pageId = applied.PageId.ToString(),
            applied.Created,
            applied.Summary,
            validation.Warnings,
            validation.Suggestions,
            generation = CompactLocalResult(generation),
            build = CompactLocalResult(build),
            message = success
                ? "The approved complex-page definition was saved and all requested local actions completed."
                : "The complex-page definition was saved, but a requested local generation or build action failed. Review the returned diagnostics before retrying."
        };
    }

    private static object? CompactLocalResult(LocalExecutionResult? result)
    {
        if (result == null) return null;
        var output = result.Output ?? string.Empty;
        if (output.Length > MaxOutputLength)
            output = output[..MaxOutputLength] + $"\n... {output.Length - MaxOutputLength} characters omitted ...";
        return new
        {
            result.Success,
            result.Message,
            result.Data,
            output
        };
    }
}

[McpServerToolType]
public sealed class UiCompositionMcpTools
{
    [McpServerTool(Name = "get_complex_page_context")]
    [Description("Read complex pages, entity service capabilities, display templates, and an optional structured page snapshot. Call this before creating or updating a complex page.")]
    public static Task<object?> GetContextAsync(
        IUiCompositionMcpToolHandler handler,
        [Description("Optional project id. Omit it when the current workspace contains .codemaster/project-context.json.")] string? projectId = null,
        [Description("Optional existing complex-page id whose complete definition snapshot should be returned.")] string? pageId = null,
        [Description("Optional generated-project directory used to resolve project context and authentication.")] string? workspacePath = null)
    {
        return SafeInvokeAsync(() => handler.GetContextAsync(
            ParseId(projectId),
            ParseNullableId(pageId),
            workspacePath));
    }

    [McpServerTool(Name = "get_view_builder_context")]
    [Description("Read the CodeMaster project metadata catalog and persisted view definitions before previewing or saving a view. Generated files and physical database views are not planning inputs; physical views are inspected only during deployment, import, or drift verification.")]
    public static Task<object?> GetViewBuilderContextAsync(
        IUiCompositionMcpToolHandler handler,
        [Description("Optional project id. Omit it when the current workspace contains .codemaster/project-context.json.")] string? projectId = null,
        [Description("Optional generated-project directory used to resolve project context and authentication.")] string? workspacePath = null)
        => SafeInvokeAsync(() => handler.GetViewBuilderContextAsync(ParseId(projectId), workspacePath));

    [McpServerTool(Name = "preview_view_sql")]
    [Description("Preview a read-only view SELECT/CTE or generate initial SQL from CodeMaster entity/field selections. This never changes metadata or the target database.")]
    public static Task<object?> PreviewViewSqlAsync(
        IUiCompositionMcpToolHandler handler,
        PreviewViewSqlDto input,
        [Description("Optional project id. Omit it when the current workspace contains .codemaster/project-context.json.")] string? projectId = null,
        string? workspacePath = null)
        => SafeInvokeAsync(() => handler.PreviewViewSqlAsync(ParseId(projectId), input, workspacePath));

    [McpServerTool(Name = "save_view_definition")]
    [Description("Persist a versioned CodeMaster view definition with final SQL, declared output fields, dependencies, and lineage. This is definition-only and does not deploy a physical view or generate consumer code.")]
    public static Task<object?> SaveViewDefinitionAsync(
        IUiCompositionMcpToolHandler handler,
        SaveProjectViewDefinitionDto input,
        [Description("Optional project id. Omit it when the current workspace contains .codemaster/project-context.json.")] string? projectId = null,
        string? workspacePath = null)
        => SafeInvokeAsync(() => handler.SaveViewDefinitionAsync(ParseId(projectId), input, workspacePath));

    [McpServerTool(Name = "deploy_view_definition")]
    [Description("Deploy or replace one saved CodeMaster-managed view definition. The first call should use confirmApply=false; repeat with true only after reviewing the exact definition version/hash.")]
    public static Task<object?> DeployViewDefinitionAsync(
        IUiCompositionMcpToolHandler handler,
        DeployProjectViewDefinitionDto input,
        [Description("False validates the target definition and returns a confirmation summary; true applies the physical view DDL.")] bool confirmApply = false,
        string? projectId = null,
        string? workspacePath = null)
        => SafeInvokeAsync(() => handler.DeployViewDefinitionAsync(ParseId(projectId), input, confirmApply, workspacePath));

    [McpServerTool(Name = "register_read_only_view_entity")]
    [Description("Register or adopt the read-only ModuleEntity and ProjectDataSource consumer for a deployed or definition-only managed view using declared field mappings.")]
    public static Task<object?> RegisterReadOnlyViewEntityAsync(
        IUiCompositionMcpToolHandler handler,
        RegisterReadOnlyViewEntityDto input,
        [Description("False validates and discloses the registration; true persists the entity/data-source metadata.")] bool confirmApply = false,
        string? projectId = null,
        string? workspacePath = null)
        => SafeInvokeAsync(() => handler.RegisterReadOnlyViewEntityAsync(ParseId(projectId), input, confirmApply, workspacePath));

    [McpServerTool(Name = "import_existing_view")]
    [Description("Import an existing database view as an external/read-only CodeMaster consumer. SQL ownership remains external and no replacement is performed by default.")]
    public static Task<object?> ImportExistingViewAsync(
        IUiCompositionMcpToolHandler handler,
        RegisterReadOnlyViewEntityDto input,
        [Description("False validates and discloses the import; true persists the imported read-only consumer metadata.")] bool confirmApply = false,
        string? projectId = null,
        string? workspacePath = null)
        => SafeInvokeAsync(() => handler.ImportExistingViewAsync(ParseId(projectId), input, confirmApply, workspacePath));

    [McpServerTool(Name = "materialize_project")]
    [Description("Run the single CodeMaster project materialization pipeline through LocalAgent: smart-generate entities, apply database migrations (including prepared managed views), generate complex pages, build, and test.")]
    public static Task<object?> MaterializeProjectAsync(
        IUiCompositionMcpToolHandler handler,
        [Description("False returns the exact pipeline summary; true executes it locally through CodeMaster LocalAgent.")] bool confirmApply = false,
        string? projectId = null,
        string? workspacePath = null)
        => SafeInvokeAsync(() => handler.MaterializeProjectAsync(ParseId(projectId), confirmApply, workspacePath));

    [McpServerTool(Name = "propose_complex_page_plan")]
    [Description("Validate a complete structured complex-page plan. The first call must leave confirmApply=false; after explicit user approval, repeat the exact plan with confirmApply=true to save, generate, and optionally build it. Raw Vue and arbitrary file paths are rejected.")]
    public static Task<object?> ProposePlanAsync(
        IUiCompositionMcpToolHandler handler,
        [Description("Complete structured complex-page definition, decisions, and optional verified backend changes.")] ComplexPageAgentPlanDto plan,
        [Description("False validates only. Set true only after the user approves the exact validated plan.")] bool confirmApply = false,
        [Description("Optional project id. Omit it when the current workspace contains .codemaster/project-context.json.")] string? projectId = null,
        [Description("Optional generated-project directory used to resolve project context, authentication, generation, and build paths.")] string? workspacePath = null)
    {
        return SafeInvokeAsync(() => handler.ProposePlanAsync(
            ParseId(projectId),
            plan,
            confirmApply,
            workspacePath));
    }

    private static long ParseId(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;

    private static long? ParseNullableId(string? value) => long.TryParse(value, out var parsed) ? parsed : null;

    private static async Task<object?> SafeInvokeAsync(Func<Task<object>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            return new { success = false, applied = false, message = ex.Message };
        }
    }
}
