using System.ComponentModel;

namespace CodeMaster.Application.Dtos.CodeGen;

public sealed class ValidatePageDefinitionDto
{
    public long? CurrentPageId { get; set; }
    public CreateProjectPageDto Definition { get; set; } = new();
}

public sealed class PageDefinitionValidationDto
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public string? NormalizedLayoutJson { get; set; }
}

public sealed class ComplexPageAgentDecisionDto
{
    [Description("Stable decision target, for example dataSource:orders, binding:order_status:control, or backend:OrderSummaryDto.")]
    public string Target { get; set; } = string.Empty;

    [Description("Concrete reason for this decision, including involved entities or fields when applicable.")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ComplexPageAgentBackendChangeDto
{
    public string Key { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool RequiresSwaggerValidation { get; set; } = true;
    public bool SwaggerVerified { get; set; }
    public string? SwaggerEvidence { get; set; }
}

public sealed class ComplexPageAgentPlanDto
{
    [Description("Short user-facing summary of the complex-page plan.")]
    public string Summary { get; set; } = string.Empty;

    [Description("Existing complex-page id to update. Leave empty to create a new page.")]
    public long? ExistingPageId { get; set; }

    [Description("Required when updating: definitionVersion read from the latest complex-page context.")]
    public int? ExpectedDefinitionVersion { get; set; }

    [Description("Complete structured page definition. Raw Vue source and generated file paths are not accepted.")]
    public CreateProjectPageDto Definition { get; set; } = new();

    [Description("Required reasons for every data-source shape decision and every explicit control override.")]
    public List<ComplexPageAgentDecisionDto> Decisions { get; set; } = new();

    [Description("Optional backend/API changes. Any change that affects an API must include successful Swagger verification evidence.")]
    public List<ComplexPageAgentBackendChangeDto> BackendChanges { get; set; } = new();

    [Description("Ignored by the Agent. Complex-page generation is a manual action the user triggers from the project page; the agent only prepares the page metadata.")]
    public bool GenerateAfterApproval { get; set; }
    public bool Incremental { get; set; } = true;
    [Description("Ignored by the Agent. Building the generated project is a manual action the user triggers from the project page.")]
    public bool BuildAfterGeneration { get; set; }
}

public sealed class ComplexPageAgentPlanValidationDto
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
}

public sealed class ComplexPageAgentContextDto
{
    public long ProjectId { get; set; }
    public List<ProjectPageDto> Pages { get; set; } = new();
    public List<EntityServiceCapabilityDto> EntityCapabilities { get; set; } = new();
    public List<PageDisplayTemplateDto> DisplayTemplates { get; set; } = new();
    public PageDefinitionSnapshotDto? SelectedPage { get; set; }
}

public sealed class ComplexPageAgentApplyResultDto
{
    public long ProjectId { get; set; }
    public long PageId { get; set; }
    public bool Created { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class ComplexPageAgentContextQuery
{
    public long? PageId { get; set; }
}
