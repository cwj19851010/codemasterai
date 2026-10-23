using CodeMaster.Core.Dtos;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.Application.Dtos.CodeGen;

public sealed class PageDisplayTemplateQueryDto
{
    public PageDisplayTemplateScope? Scope { get; set; }
    public string? TargetType { get; set; }
    public string? Keyword { get; set; }
    public bool IncludeDisabled { get; set; }
}

public sealed class PageDisplayTemplateDto : EntityDto
{
    public string TemplateKey { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public PageDisplayTemplateScope Scope { get; set; }
    public string? TargetType { get; set; }
    public string TemplateContent { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Sort { get; set; }
    public bool IsSystem { get; set; }
    public bool IsEnabled { get; set; }
}

public class CreatePageDisplayTemplateDto
{
    public string TemplateKey { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public PageDisplayTemplateScope Scope { get; set; } = PageDisplayTemplateScope.Container;
    public string? TargetType { get; set; }
    public string TemplateContent { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Sort { get; set; }
    public bool IsSystem { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public sealed class UpdatePageDisplayTemplateDto : CreatePageDisplayTemplateDto
{
}
