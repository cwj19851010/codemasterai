using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Domain.Entities.CodeGen;
using Mapster;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class ComplexPageAgentPlanningService : IComplexPageAgentPlanningService
{
    private readonly ISqlSugarClient _db;
    private readonly IProjectPageService _pageService;
    private readonly IPageDisplayTemplateService _displayTemplateService;

    public ComplexPageAgentPlanningService(
        ISqlSugarClient db,
        IProjectPageService pageService,
        IPageDisplayTemplateService displayTemplateService)
    {
        _db = db;
        _pageService = pageService;
        _displayTemplateService = displayTemplateService;
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ComplexPageAgentContextDto> GetContextAsync(long projectId, long? pageId = null)
    {
        await EnsureProjectAsync(projectId);
        var context = new ComplexPageAgentContextDto
        {
            ProjectId = projectId,
            Pages = await _pageService.GetListAsync(new ProjectPageQueryDto { ProjectId = projectId }),
            EntityCapabilities = await _pageService.GetEntityServiceCapabilitiesAsync(projectId),
            DisplayTemplates = await _displayTemplateService.GetListAsync(new PageDisplayTemplateQueryDto())
        };
        if (pageId.HasValue)
        {
            var page = await _pageService.GetByIdAsync(pageId.Value)
                ?? throw new InvalidOperationException($"Project page {pageId.Value} was not found.");
            if (page.ProjectId != projectId)
                throw new InvalidOperationException("The selected complex page does not belong to the current project.");
            context.SelectedPage = await _pageService.GetDefinitionSnapshotAsync(pageId.Value);
        }
        return context;
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ComplexPageAgentPlanValidationDto> ValidateAsync(long projectId, ComplexPageAgentPlanDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = new ComplexPageAgentPlanValidationDto();
        if (!await _db.Queryable<Project>().Where(item => item.Id == projectId && !item.IsDeleted).AnyAsync())
            result.Errors.Add($"Project {projectId} does not exist.");
        if (plan.Definition == null)
        {
            result.Errors.Add("A structured page definition is required.");
            return result;
        }
        if (plan.Definition.ProjectId != projectId)
            result.Errors.Add("The planned page definition escapes the current project boundary.");
        if (string.IsNullOrWhiteSpace(plan.Summary))
            result.Errors.Add("The plan summary is required.");
        if (LooksLikeVueSource(plan.Definition.LayoutJson))
        {
            result.Errors.Add("Raw Vue source is not accepted. Submit only the structured layout tree and bindings.");
            result.Suggestions.Add("Use page/row/column/container nodes with stable componentId values, then call the generator after approval.");
        }

        var decisions = plan.Decisions
            .Where(item => !string.IsNullOrWhiteSpace(item.Target))
            .GroupBy(item => item.Target.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var decision in plan.Decisions.Where(item => string.IsNullOrWhiteSpace(item.Target) || string.IsNullOrWhiteSpace(item.Reason)))
            result.Errors.Add("Every Agent decision must include both a stable target and a concrete reason.");
        foreach (var source in plan.Definition.DataSources)
        {
            if (!decisions.TryGetValue($"dataSource:{source.SourceKey}", out var decision) || string.IsNullOrWhiteSpace(decision.Reason))
                result.Errors.Add($"Data source '{source.SourceKey}' is missing a data-shape/DTO decision reason (target: dataSource:{source.SourceKey}).");
        }
        foreach (var binding in plan.Definition.Bindings.Where(item => !string.IsNullOrWhiteSpace(item.ControlType)))
        {
            var identity = string.IsNullOrWhiteSpace(binding.BindingId) ? binding.ComponentId : binding.BindingId;
            if (!decisions.TryGetValue($"binding:{identity}:control", out var decision) || string.IsNullOrWhiteSpace(decision.Reason))
                result.Errors.Add($"Control override '{identity}' requires a reason (target: binding:{identity}:control).");
        }
        foreach (var change in plan.BackendChanges)
        {
            if (string.IsNullOrWhiteSpace(change.Key) ||
                !decisions.TryGetValue($"backend:{change.Key}", out var decision) ||
                string.IsNullOrWhiteSpace(decision.Reason))
                result.Errors.Add($"Backend change '{change.Key}' requires a decision reason (target: backend:{change.Key}).");
            if (change.RequiresSwaggerValidation && (!change.SwaggerVerified || string.IsNullOrWhiteSpace(change.SwaggerEvidence)))
                result.Errors.Add($"Backend change '{change.Key}' must pass Swagger verification before the page can bind to it.");
        }

        if (plan.ExistingPageId.HasValue)
        {
            var existing = await _pageService.GetByIdAsync(plan.ExistingPageId.Value);
            if (existing == null || existing.ProjectId != projectId)
                result.Errors.Add("The page selected for update does not belong to the current project.");
            else if (!plan.ExpectedDefinitionVersion.HasValue || plan.ExpectedDefinitionVersion.Value != existing.DefinitionVersion)
                result.Errors.Add("The complex-page definition version is stale. Read the latest Agent context before proposing an update.");
        }
        else if (plan.ExpectedDefinitionVersion.HasValue)
        {
            result.Warnings.Add("ExpectedDefinitionVersion is ignored when creating a new complex page.");
        }

        var pageValidation = await _pageService.ValidateDefinitionAsync(new ValidatePageDefinitionDto
        {
            CurrentPageId = plan.ExistingPageId,
            Definition = plan.Definition
        });
        result.Errors.AddRange(pageValidation.Errors);
        result.Warnings.AddRange(pageValidation.Warnings);
        result.Suggestions.AddRange(pageValidation.Suggestions);
        if (plan.BuildAfterGeneration && !plan.GenerateAfterApproval)
            result.Errors.Add("BuildAfterGeneration requires GenerateAfterApproval.");

        result.Errors = result.Errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.Warnings = result.Warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.Suggestions = result.Suggestions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    [Permission("codegen:projectpage:create|codegen:projectpage:update")]
    public async Task<ComplexPageAgentApplyResultDto> ApplyAsync(long projectId, ComplexPageAgentPlanDto plan)
    {
        var validation = await ValidateAsync(projectId, plan);
        if (!validation.IsValid)
            throw new InvalidOperationException("The complex-page plan is invalid: " + string.Join(" ", validation.Errors));

        if (plan.ExistingPageId.HasValue)
        {
            var update = plan.Definition.Adapt<UpdateProjectPageDto>();
            update.DefinitionVersion = plan.ExpectedDefinitionVersion;
            await _pageService.SaveDefinitionAsync(plan.ExistingPageId.Value, update);
            return new ComplexPageAgentApplyResultDto
            {
                ProjectId = projectId,
                PageId = plan.ExistingPageId.Value,
                Created = false,
                Summary = plan.Summary.Trim()
            };
        }

        var pageId = await _pageService.CreateAsync(plan.Definition);
        return new ComplexPageAgentApplyResultDto
        {
            ProjectId = projectId,
            PageId = pageId,
            Created = true,
            Summary = plan.Summary.Trim()
        };
    }

    private async Task EnsureProjectAsync(long projectId)
    {
        if (!await _db.Queryable<Project>().Where(item => item.Id == projectId && !item.IsDeleted).AnyAsync())
            throw new InvalidOperationException($"Project {projectId} does not exist.");
    }

    private static bool LooksLikeVueSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return text.Contains("<template", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(".vue", StringComparison.OrdinalIgnoreCase);
    }
}
