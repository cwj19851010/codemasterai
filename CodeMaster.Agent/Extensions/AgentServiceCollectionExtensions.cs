using CodeMaster.Agent.Contracts;
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
        services.AddScoped<IAiAttachmentStore, AiAttachmentStore>();
        services.TryAddSingleton<IAiProjectValidationRunner, AiProjectValidationRunner>();
        // Source repair is disabled (2026-10-20): the agent never reads or patches
        // CodeMaster source code, so the executor is no longer registered.
        services.AddScoped<IAgentEventStore, SqlAgentEventStore>();
        services.TryAddSingleton<AiConversationReadCacheManager>();
        services.AddScoped<ICodeMasterAgentToolFactory, CodeMasterAgentToolFactory>();
        services.AddScoped<IAiConversationService, AiConversationService>();
        services.TryAddSingleton<IPlatformConstraintsProvider, PlatformConstraintsProvider>();
        return services;
    }
}
