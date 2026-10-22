using System.Text.Json;
using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;
using CodeMaster.McpServer.Services;

namespace CodeMaster.McpServer.Tools;

public sealed class ProjectOperationTool
{
    private const int MaxMcpOutputLength = 12_000;
    private const int OutputHeadLength = 2_000;

    private readonly LocalCodegenExecutionService _localExecutor;
    private readonly CodeMasterApiClient _apiClient;
    private readonly ProjectContextResolver _contextResolver;

    public ProjectOperationTool(
        LocalCodegenExecutionService localExecutor,
        CodeMasterApiClient apiClient,
        ProjectContextResolver contextResolver)
    {
        _localExecutor = localExecutor;
        _apiClient = apiClient;
        _contextResolver = contextResolver;
    }

    public static McpTool Definition => new()
    {
        Name = "run_project_operation",
        Description = "Run initialization, migration, printing runtime distribution, build, start, stop, status, project menu sync, project language sync, or database configuration sync on the local generated project. Metadata and templates are downloaded from CodeMaster WebApi; the CodeMaster database is never opened by MCP.",
        InputType = typeof(ProjectOperationInput),
        InputSchema = JsonSerializer.SerializeToNode(new
        {
            type = "object",
            properties = new
            {
                operation = new { type = "string", description = "initialize, initialize_step, printing, start_frontend, start_backend, start_all, stop_frontend, stop_backend, stop_all, status, migrate_database, build, test, sync_menus, sync_languages, or sync_database_config." },
                projectId = new { oneOf = new object[] { new { type = "integer" }, new { type = "string" } }, description = "Target project id." },
                targetPath = new { type = "string", description = "Optional local initialization target path." },
                step = new { type = "integer", description = "Step number 1-11 when operation=initialize_step." },
                workspacePath = new { type = "string", description = "Optional generated project directory. Used to resolve projectId and serverBaseUrl from .codemaster/project-context.json." }
            },
            required = new[] { "operation" }
        })!
    };

    public async Task<object?> HandleAsync(object? input)
    {
        var args = (ProjectOperationInput?)input ?? throw new ArgumentException("Invalid input");
        args.ProjectId = await McpProjectContextHelper.ResolveProjectIdAsync(
            _contextResolver,
            args.ProjectId,
            args.WorkspacePath);

        if (args.ProjectId <= 0)
            return new { success = false, message = "projectId is required." };

        var action = ResolveAction(args.Operation, args.Step);
        return action == null
            ? new
            {
                success = false,
                message = "operation must be one of: initialize, initialize_step, printing, start_frontend, start_backend, start_all, stop_frontend, stop_backend, stop_all, status, migrate_database, build, test, sync_menus, sync_languages, sync_database_config."
            }
            : await ExecuteAsync(action, args);
    }

    internal static string? ResolveAction(string operation, int step) => operation switch
    {
        "initialize" => "initializeProject",
        "initialize_step" when step is >= 1 and <= 11 => $"initializeStep{step}",
        "start_frontend" => "startFrontend",
        "start_backend" => "startBackend",
        "start_all" => "startProject",
        "stop_frontend" => "stopFrontend",
        "stop_backend" => "stopBackend",
        "stop_all" => "stopProject",
        "status" => "getProjectStatus",
        "migrate_database" => "migrateDatabase",
        "printing" => "distributePrintingRuntime",
        "build" => "buildProject",
        "test" => "testProject",
        "sync_menus" => "syncProjectMenus",
        "sync_languages" => "syncProjectLanguages",
        "sync_database_config" => "syncDatabaseConfig",
        _ => null
    };

    private async Task<object> ExecuteAsync(string action, ProjectOperationInput args)
    {
        var session = await _apiClient.ResolveSessionAsync(args.WorkspacePath);
        var targetPath = ResolveTargetPath(args.TargetPath, args.WorkspacePath);
        var payload = JsonSerializer.SerializeToElement(new
        {
            projectId = args.ProjectId,
            targetPath
        });

        var result = await _localExecutor.ExecuteAsync(action, new LocalExecutionRequest
        {
            Action = action,
            Payload = payload,
            ServerBaseUrl = session.ServerBaseUrl,
            AccessToken = session.AccessToken
        });

        return CompactOperationResult(result);
    }

    internal static string? ResolveTargetPath(string? targetPath, string? workspacePath)
    {
        var candidate = string.IsNullOrWhiteSpace(targetPath) ? workspacePath : targetPath;
        return string.IsNullOrWhiteSpace(candidate) ? null : Path.GetFullPath(candidate);
    }

    private static object CompactOperationResult(LocalExecutionResult result)
    {
        var output = result.Output ?? string.Empty;
        if (output.Length <= MaxMcpOutputLength)
        {
            return new
            {
                success = result.Success,
                message = result.Message,
                data = result.Data,
                output,
                outputTruncated = false
            };
        }

        var tailLength = MaxMcpOutputLength - OutputHeadLength;
        var compactOutput = output[..OutputHeadLength] +
                            Environment.NewLine +
                            $"... MCP output truncated ({output.Length - MaxMcpOutputLength} characters omitted) ..." +
                            Environment.NewLine +
                            output[^tailLength..];

        return new
        {
            success = result.Success,
            message = result.Message,
            data = result.Data,
            output = compactOutput,
            outputTruncated = true,
            originalOutputLength = output.Length
        };
    }
}

public sealed class ProjectOperationInput
{
    public string Operation { get; set; } = string.Empty;
    public long ProjectId { get; set; }
    public string? TargetPath { get; set; }
    public int Step { get; set; }
    public string? WorkspacePath { get; set; }
}
