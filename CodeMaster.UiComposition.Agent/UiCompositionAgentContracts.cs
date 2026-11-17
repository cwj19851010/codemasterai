using System.ComponentModel;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.UiComposition.Dtos;

namespace CodeMaster.UiComposition.Agent;

public sealed class UiCompositionAgentContextQuery
{
    public bool IncludeDataSources { get; set; } = true;
    public bool IncludeOpenApiDocuments { get; set; } = true;
    public bool IncludeComplexPages { get; set; } = true;
}

public sealed class UiCompositionAgentContextDto
{
    public long ProjectId { get; set; }
    public List<ProjectDataSourceDto> DataSources { get; set; } = new();
    public List<OpenApiDocumentDto> OpenApiDocuments { get; set; } = new();
    public List<ProjectPageDto> ComplexPages { get; set; } = new();
}

public class ViewAgentPlanDto
{
    [Description("Short user-facing summary of the view and read-only data source to create.")]
    public string Summary { get; set; } = string.Empty;

    [Description("Why a database view is required instead of an existing entity or API data source.")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Deterministic fix-ups applied while resolving the plan, surfaced to the caller so a silent
    /// rename never looks like the model got the name right on its own.
    /// </summary>
    public List<string> AutomaticRepairs { get; set; } = new();

    [Description("Final read-only SELECT/CTE SQL, or structured entity/field/join selections used to generate an initial SQL statement. Sql takes precedence when supplied.")]
    public PreviewViewSqlDto Query { get; set; } = new();

    [Description("Optional generated-entity field declarations. Name is the C# property, ColumnName is the final SQL output alias, and DataType is the trusted C# type. When omitted, fields are inferred from the created view.")]
    public List<ViewEntityFieldDefinitionDto> EntityFields { get; set; } = new();

    [Description("Database view identifier.")]
    public string ViewName { get; set; } = string.Empty;

    [Description("Must be true when the view name already exists and replacing it is intentional.")]
    public bool ReplaceExistingView { get; set; }

    [Description("Existing target module id for the generated read-only entity.")]
    public long ModuleId { get; set; }

    [Description("Existing or same-plan technical module name. Used when ModuleId is not known until earlier metadata tasks have run.")]
    public string? ModuleName { get; set; }

    [Description("Optional exact existing read-only entity id to adopt as this view consumer. The entity name and table mapping must match the plan.")]
    public long? ExistingEntityId { get; set; }

    [Description("ASCII PascalCase name for the generated read-only entity. It must not collide with an existing writable business entity in the target module; name the consumer after the view instead, for example ProjectProfitView or VProjectProfit.")]
    public string EntityName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    [Description("Integral SQL output column mapped to the generated entity Id. When omitted, CodeMaster defaults to Id; if the SELECT exposes no such column, or names one that is not integral, the consumer is registered keyless (list/query only, no GetById or detail page). That is the correct shape for a UNION/aggregate report whose rows have no unique identity, so such a plan is repaired rather than rejected.")]
    public string KeyColumn { get; set; } = string.Empty;

    public bool GenerateFrontend { get; set; } = true;
    [Description("When true, save the view definition and read-only consumer metadata now, then deploy the physical view during the final project migration. Work-plan view tasks use this mode; standalone proposals keep the existing immediate deployment behavior.")]
    public bool DefinitionOnly { get; set; }
    [Description("Ignored by the Agent. Code generation is a manual action the user triggers from the project page; the agent only writes metadata.")]
    public bool GenerateAfterApproval { get; set; }
    public bool Incremental { get; set; } = true;
    [Description("Ignored by the Agent. Building the generated project is a manual action the user triggers from the project page.")]
    public bool BuildAfterGeneration { get; set; }
}

public sealed class ViewAgentApprovalPlanDto : ViewAgentPlanDto
{
    public string ResolvedSql { get; set; } = string.Empty;
    public List<string> ExpectedColumns { get; set; } = new();
}

public class OpenApiAgentImportRequestDto
{
    [Description("Existing target module id that will own imported metadata-only DTO entities.")]
    public long ModuleId { get; set; }

    [Description("Existing imported document id when re-importing. Leave empty for the first import.")]
    public long? DocumentId { get; set; }

    public string Name { get; set; } = string.Empty;

    [Description("Absolute HTTP or HTTPS URL of an OpenAPI 3.x document. Pasted document bodies are intentionally not accepted by the Agent approval channel.")]
    public string SourceUrl { get; set; } = string.Empty;
}

public sealed class OpenApiAgentImportPlanDto : OpenApiAgentImportRequestDto
{
    [Description("Short user-facing summary of the OpenAPI import or synchronization.")]
    public string Summary { get; set; } = string.Empty;

    [Description("Document hash returned by preview_openapi_import.")]
    public string ExpectedDocumentHash { get; set; } = string.Empty;

    [Description("Change-set hash returned by preview_openapi_import.")]
    public string ExpectedChangeSetHash { get; set; } = string.Empty;

    public bool ApplyRemovals { get; set; }
    public List<string> RemoveChangeKeys { get; set; } = new();
    public List<string> DetachChangeKeys { get; set; } = new();
    [Description("Ignored by the Agent. Code generation is a manual action the user triggers from the project page; the agent only writes metadata.")]
    public bool GenerateAfterApproval { get; set; }
    public bool Incremental { get; set; } = true;
    [Description("Ignored by the Agent. Building the generated project is a manual action the user triggers from the project page.")]
    public bool BuildAfterGeneration { get; set; }
}

public sealed class ViewAgentExecutionResult : IAgentClientActionResult
{
    public long ProjectId { get; set; }
    public long EntityId { get; set; }
    public long DataSourceId { get; set; }
    public string ViewName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
    public List<AiClientActionDto> ClientActions { get; set; } = new();
    public bool HasClientActions => ClientActions.Count > 0;
}

public sealed class OpenApiAgentExecutionResult : IAgentClientActionResult
{
    public long ProjectId { get; set; }
    public long DocumentId { get; set; }
    public string Summary { get; set; } = string.Empty;
    public int AddedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int PendingDeleteCount { get; set; }
    public int RemovedCount { get; set; }
    public List<long> EntityIds { get; set; } = new();
    public List<long> DataSourceIds { get; set; } = new();
    public List<long> AffectedPageIds { get; set; } = new();
    public List<AiClientActionDto> ClientActions { get; set; } = new();
    public bool HasClientActions => ClientActions.Count > 0;
}
