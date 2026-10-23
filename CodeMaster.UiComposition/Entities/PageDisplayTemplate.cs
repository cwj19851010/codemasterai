using CodeMaster.Core.Entities;
using SqlSugar;

namespace CodeMaster.UiComposition.Entities;

public enum PageDisplayTemplateScope
{
    Container = 1,
    Field = 2
}

/// <summary>
/// Reusable Vue template fragments for complex-page containers and fields.
/// The entity intentionally lives in the private UI-composition module so the
/// public CodeMaster schema does not expose the complex-page implementation.
/// </summary>
[SugarTable("sys_page_display_template")]
public sealed class PageDisplayTemplate : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "template_key", Length = 120, IsNullable = false)]
    public string TemplateKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "template_name", Length = 200, IsNullable = false)]
    public string TemplateName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "scope", IsNullable = false)]
    public PageDisplayTemplateScope Scope { get; set; } = PageDisplayTemplateScope.Container;

    [SugarColumn(ColumnName = "target_type", Length = 100, IsNullable = true)]
    public string? TargetType { get; set; }

    [SugarColumn(ColumnName = "template_content", ColumnDataType = "text", IsNullable = false)]
    public string TemplateContent { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "description", Length = 500, IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "sort", IsNullable = false)]
    public int Sort { get; set; }

    [SugarColumn(ColumnName = "is_system", IsNullable = false)]
    public bool IsSystem { get; set; } = true;

    [SugarColumn(ColumnName = "is_enabled", IsNullable = false)]
    public bool IsEnabled { get; set; } = true;
}
