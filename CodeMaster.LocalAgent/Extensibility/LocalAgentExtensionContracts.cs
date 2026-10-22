using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Extensibility;
using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;
using SqlSugar;

namespace CodeMaster.LocalAgent.Extensibility;

public interface ILocalMetadataBundleContributor : ICodeMasterExtension
{
    IReadOnlyCollection<Type> EntityTypes { get; }

    Task RestoreAsync(GenerationBundleDto bundle, ISqlSugarClient db);
}

public interface ILocalCodegenActionHandler : ICodeMasterExtension
{
    bool CanHandle(string action);

    bool ReportCompletion { get; }

    Task<LocalExecutionResult> ExecuteAsync(LocalCodegenActionContext context);

    void ConfigureCompletion(
        LocalExecutionCompleteDto completion,
        LocalExecutionRequest request,
        LocalExecutionResult result);
}

public sealed class LocalCodegenActionContext
{
    public required string Action { get; init; }

    public required LocalExecutionRequest Request { get; init; }

    public required CodeMasterServerClient ServerClient { get; init; }

    public required LocalMetadataStore MetadataStore { get; init; }
}
