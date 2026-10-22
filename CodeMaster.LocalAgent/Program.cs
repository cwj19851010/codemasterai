using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;
using CodeMaster.Infrastructure.DynamicApi;

var builder = WebApplication.CreateBuilder(args);

var configuredOptions = builder.Configuration.GetSection("LocalAgent").Get<LocalAgentOptions>() ?? new LocalAgentOptions();
var runStdio = args.Any(arg => string.Equals(arg, "--stdio", StringComparison.OrdinalIgnoreCase)) ||
               string.Equals(configuredOptions.Mode, "stdio", StringComparison.OrdinalIgnoreCase);

var optionalModules = CodeMasterModuleAssemblyLoader.LoadOptionalModules(
    AppContext.BaseDirectory,
    Environment.GetEnvironmentVariable("CODEMASTER_LOCAL_AGENT_MODULE_PATHS"));
builder.Services.AddCodeMasterExtensions(optionalModules);

if (runStdio)
{
    // stdout is reserved for newline-delimited JSON protocol messages in sidecar mode.
    builder.Logging.ClearProviders();
}

builder.Services.AddCodeMasterLocalExecution(builder.Configuration);
builder.Services.AddSingleton<StdioLocalAgentHost>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            if (string.Equals(origin, "tauri://localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(origin, "http://tauri.localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                   origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);
        });
    });
});

if (!runStdio)
{
    if (string.IsNullOrWhiteSpace(configuredOptions.ClientToken))
    {
        throw new InvalidOperationException(
            "CodeMaster.LocalAgent HTTP mode requires a non-empty LocalAgent:ClientToken. " +
            "Add \"LocalAgent\": { \"ClientToken\": \"<your-token>\" } to appsettings.json, " +
            "and set the same token as the agent token in the CodeMaster client settings. " +
            "Alternatively run in sidecar mode with --stdio.");
    }

    builder.WebHost.UseUrls($"http://127.0.0.1:{configuredOptions.Port}");
}

var app = builder.Build();

if (runStdio)
{
    await app.Services.GetRequiredService<StdioLocalAgentHost>().RunAsync();
    return;
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new
{
    success = true,
    name = "CodeMaster.LocalAgent",
    time = DateTimeOffset.UtcNow
}));

app.MapPost("/api/codegen/execution/{action}", async (
    string action,
    LocalExecutionRequest request,
    LocalCodegenExecutionService executor,
    HttpContext httpContext) =>
{
    if (!string.IsNullOrWhiteSpace(configuredOptions.ClientToken))
    {
        var clientToken = httpContext.Request.Headers["X-CodeMaster-Client-Token"].ToString();
        if (!StringComparer.Ordinal.Equals(clientToken, configuredOptions.ClientToken))
        {
            return Results.Unauthorized();
        }
    }

    var result = await executor.ExecuteAsync(action, request);
    return Results.Ok(result);
});

app.Run();
