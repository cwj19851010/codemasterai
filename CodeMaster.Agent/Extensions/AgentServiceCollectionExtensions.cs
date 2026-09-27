using CodeMaster.Agent.PlatformConstraints;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeMaster.Agent.Extensions;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddCodeMasterAgent(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var repairOptions = services.AddOptions<AiProjectRepairOptions>();
        if (configuration != null)
            repairOptions.Bind(configuration.GetSection("Agent:Repair"));
        services.AddDataProtection();
        services.AddScoped<IAiCurrentUser, AiCurrentUser>();
        services.AddScoped<IAiAuthorizationService, AiAuthorizationService>();
        services.AddScoped<IAiSecretProtector, AiSecretProtector>();
        services.AddScoped<IAiProviderFactory, AiProviderFactory>();
        services.AddScoped<IAiProviderService, AiProviderService>();
        services.AddScoped<ICodeMasterProjectBlueprintService, CodeMasterProjectBlueprintService>();
        services.AddScoped<ICodeMasterProjectChangeSetService, CodeMasterProjectChangeSetService>();
        services.AddScoped<IProjectChangeSetDefinitionNormalizer, ProjectChangeSetDefinitionNormalizer>();
        services.AddScoped<IProjectWorkOperationDefinitionNormalizer, ProjectWorkOperationDefinitionNormalizer>();
        services.AddScoped<ICodeMasterAgentBatchOperationAdapter, ProjectChangeSetBatchOperationAdapter>();
        services.AddScoped<ICodeMasterAgentBatchOperationAdapter, ProjectConsistencyCheckBatchOperationAdapter>();
        services.AddScoped<ICodeMasterAgentBatchOperationAdapter, GenerateProjectBatchOperationAdapter>();
        services.AddScoped<IAiProjectWorkPlanService, AiProjectWorkPlanService>();
        services.TryAddSingleton<IAiProjectValidationRunner, AiProjectValidationRunner>();
        services.AddScoped<IAiProjectSourceRepairExecutor, AiProjectSourceRepairExecutor>();
        services.TryAddSingleton<AiConversationReadCacheManager>();
        services.AddScoped<ICodeMasterAgentToolFactory, CodeMasterAgentToolFactory>();
        services.AddScoped<IAiConversationService, AiConversationService>();
        services.TryAddSingleton<IPlatformConstraintsProvider, PlatformConstraintsProvider>();
        return services;
    }
}
