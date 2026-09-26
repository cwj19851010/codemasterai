namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// One supported field-control type. Driven by the seed data in
/// <c>CodeMaster.Migrator/SeedData/System/TemplateModule.cs</c>; this record is the
/// agent-facing description rendered to the model and the rule shape used to
/// pick a control from a C# type.
/// </summary>
public sealed record ControlConvention
{
    /// <summary>Stable id used in <c>FormControlType</c> (e.g. "select-table").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-friendly display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Natural-language description rendered to the model so it can pick the
    /// correct control from the field's C# type and business meaning.
    /// </summary>
    public string SuitableFor { get; init; } = string.Empty;

    /// <summary>C# data types that this control accepts (e.g. ["string"]).</summary>
    public IReadOnlyList<string> CSharpTypes { get; init; } = Array.Empty<string>();

    /// <summary>True for controls that must bind to a related entity (select-table, cascader).</summary>
    public bool RequiresRelatedEntity { get; init; }

    /// <summary>True for cascader only; the related entity must be a tree entity.</summary>
    public bool RequiresTreeEntity { get; init; }

    /// <summary>Sections where the control is registered (add/edit/search/list/detail).</summary>
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();

    public string RenderForPrompt() =>
        $"- `{Id}` ({DisplayName}): {SuitableFor} [c#: {string.Join(", ", CSharpTypes)}]";

    /// <summary>Default registry shipped with the agent. Mirrors the seed data in
    /// <c>TemplateModule.cs:GetControlTemplates</c>.</summary>
    public static IReadOnlyList<ControlConvention> Defaults() => new[]
    {
        new ControlConvention
        {
            Id = "input", DisplayName = "文本框",
            SuitableFor = "Short single-line text (≤200 chars), titles, codes, single-line remarks. The default for ordinary string fields.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "add", "edit", "search", "detail" }
        },
        new ControlConvention
        {
            Id = "textarea", DisplayName = "多行文本",
            SuitableFor = "Long text (>200 chars), multi-line remarks, descriptions, free-form notes.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "add", "edit" }
        },
        new ControlConvention
        {
            Id = "number", DisplayName = "数字输入",
            SuitableFor = "Numeric input. Pair with MinValue/MaxValue/Precision/Scale for range constraints.",
            CSharpTypes = new[] { "int", "long", "decimal", "double", "float", "short" },
            Sections = new[] { "add", "edit" }
        },
        new ControlConvention
        {
            Id = "select", DisplayName = "下拉选择",
            SuitableFor = "Enumerations, fixed option lists, dictionary items, or a simple api key lookup with a single label field.",
            CSharpTypes = new[] { "string", "int", "long" },
            Sections = new[] { "add", "edit", "search", "list", "detail" }
        },
        new ControlConvention
        {
            Id = "select-enum", DisplayName = "下拉选择(枚举)",
            SuitableFor = "Drop-down for an enumeration type. Use SelectDataSource=\"enum\" and the enum's type name.",
            CSharpTypes = new[] { "string", "int" },
            Sections = new[] { "add", "edit", "search" }
        },
        new ControlConvention
        {
            Id = "select-table", DisplayName = "弹窗/列表选择",
            SuitableFor = "Foreign key to another entity where the user must see multiple display fields (e.g. customer name + phone). Requires RelatedEntityName, RelatedEntityIdField, and RelatedEntityDisplayFields.",
            CSharpTypes = new[] { "long", "int" },
            RequiresRelatedEntity = true,
            Sections = new[] { "add", "edit", "search", "list", "detail" }
        },
        new ControlConvention
        {
            Id = "cascader", DisplayName = "级联选择",
            SuitableFor = "Parent-child hierarchy such as province/city/district, organization tree, or category tree. The related entity MUST be a tree entity (IsTree=true).",
            CSharpTypes = new[] { "long", "int" },
            RequiresRelatedEntity = true,
            RequiresTreeEntity = true,
            Sections = new[] { "add", "edit", "search", "detail" }
        },
        new ControlConvention
        {
            Id = "date", DisplayName = "日期",
            SuitableFor = "Date without time of day (birthday, contract date). Pairs with DateOnly.",
            CSharpTypes = new[] { "DateOnly" },
            Sections = new[] { "add", "edit", "search", "detail" }
        },
        new ControlConvention
        {
            Id = "datetime", DisplayName = "日期时间",
            SuitableFor = "Date with time of day (created at, operated at). Pairs with DateTime or DateTimeOffset.",
            CSharpTypes = new[] { "DateTime", "DateTimeOffset" },
            Sections = new[] { "add", "edit", "search", "detail" }
        },
        new ControlConvention
        {
            Id = "switch", DisplayName = "开关",
            SuitableFor = "Boolean on/off, enabled/disabled.",
            CSharpTypes = new[] { "bool" },
            Sections = new[] { "add", "edit", "search", "detail" }
        },
        new ControlConvention
        {
            Id = "checkbox", DisplayName = "复选框",
            SuitableFor = "Boolean with an explicit label (e.g. \"agreed to terms\").",
            CSharpTypes = new[] { "bool" },
            Sections = new[] { "add", "edit" }
        },
        new ControlConvention
        {
            Id = "radio-group", DisplayName = "单选组",
            SuitableFor = "Two to five mutually exclusive options displayed inline.",
            CSharpTypes = new[] { "string", "int" },
            Sections = new[] { "add", "edit" }
        },
        new ControlConvention
        {
            Id = "checkbox-group", DisplayName = "复选组",
            SuitableFor = "Multi-select with a fixed option list.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "add", "edit" }
        },
        new ControlConvention
        {
            Id = "file", DisplayName = "文件上传",
            SuitableFor = "Attachment upload (PDF, Word, generic binary). Stores a URL string.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "add", "edit", "list", "detail" }
        },
        new ControlConvention
        {
            Id = "image", DisplayName = "图片",
            SuitableFor = "Image upload (avatar, cover, preview). Stores a URL string.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "add", "edit", "list", "detail" }
        },
        new ControlConvention
        {
            Id = "editor", DisplayName = "富文本",
            SuitableFor = "Rich text content (announcements, article body). Stores HTML string.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "add", "edit", "list", "detail" }
        },
        new ControlConvention
        {
            Id = "table-column", DisplayName = "子表列",
            SuitableFor = "Used only inside a one-to-many child table; never as a standalone form control.",
            CSharpTypes = Array.Empty<string>(),
            Sections = new[] { "list" }
        },
        new ControlConvention
        {
            Id = "search-input", DisplayName = "搜索文本框",
            SuitableFor = "Single-line text used only inside the search form area.",
            CSharpTypes = new[] { "string" },
            Sections = new[] { "search" }
        }
    };
}

/// <summary>
/// Resolves a control id from C# type + business meaning. Used by the agent
/// when the model fails to pick a control, and by code generation when the
/// metadata is missing.
/// </summary>
public static class ControlConventionResolver
{
    /// <summary>Pick a control from a C# data type. Returns null when no default fits.</summary>
    public static ControlConvention? RecommendFromCSharpType(
        IReadOnlyList<ControlConvention> controls,
        string cSharpType,
        bool hasRelatedEntity,
        bool relatedEntityIsTree)
    {
        ControlConvention? best = null;
        var bestScore = 0;
        foreach (var control in controls)
        {
            if (control.CSharpTypes.Count == 0 || !control.CSharpTypes.Contains(cSharpType, StringComparer.OrdinalIgnoreCase))
                continue;
            var score = 1;
            if (hasRelatedEntity)
            {
                if (!control.RequiresRelatedEntity) continue;
                score += 5;
                if (control.RequiresTreeEntity && relatedEntityIsTree) score += 5;
                if (control.RequiresTreeEntity && !relatedEntityIsTree) score -= 5;
            }
            else if (control.RequiresRelatedEntity)
            {
                continue;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = control;
            }
        }
        return best;
    }
}