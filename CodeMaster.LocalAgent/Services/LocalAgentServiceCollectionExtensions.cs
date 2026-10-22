using CodeMaster.LocalAgent.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeMaster.LocalAgent.Services;

public static class LocalAgentServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMasterLocalExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.Configure<LocalAgentOptions>(configuration.GetSection("LocalAgent"));

        var configuredOptions = configuration.GetSection("LocalAgent").Get<LocalAgentOptions>() ?? new LocalAgentOptions();
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            localApplicationData = AppContext.BaseDirectory;
        var metadataRoot = string.IsNullOrWhiteSpace(configuredOptions.MetadataRoot)
            ? Path.Combine(localApplicationData, "CodeMaster", "LocalAgent")
            : configuredOptions.MetadataRoot;
        var environmentLogRoot = Environment.GetEnvironmentVariable(
            LocalAgentDiagnosticLog.LogDirectoryEnvironmentVariable);
        var logRoot = !string.IsNullOrWhiteSpace(environmentLogRoot)
            ? environmentLogRoot
            : string.IsNullOrWhiteSpace(configuredOptions.LogRoot)
                ? Path.Combine(metadataRoot, "logs")
                : configuredOptions.LogRoot;

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("CodeMaster.LocalAgent")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(metadataRoot, "keys")));
        if (OperatingSystem.IsWindows())
            dataProtection.ProtectKeysWithDpapi();

        services.AddHttpClient<CodeMasterServerClient>();
        services.AddSingleton(new LocalAgentDiagnosticLog(logRoot));
        services.AddSingleton<LocalMetadataStore>();
        services.AddSingleton<LocalAiProviderStore>();
        services.AddSingleton<LocalAgentConversationSessionStore>();
        services.AddSingleton<LocalAgentChatService>();
        services.AddSingleton<LocalCodegenExecutionService>();
        return services;
    }
}
