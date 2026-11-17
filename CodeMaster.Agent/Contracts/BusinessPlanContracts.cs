using System.ComponentModel;
using System.Text.Json.Serialization;
using CodeMaster.Agent.Serialization;

namespace CodeMaster.Agent.Contracts;

/// <summary>
/// Business-level plan definition that the chat model emits in its first turn.
///
/// The model describes ONLY what the business needs (modules, entities, fields,
/// relations, views, complex pages). It never names CodeMaster internal ids,
/// per-task definitions, dependency graph, or other execution concerns. The server-side
/// <see cref="Services.BusinessPlanCompiler"/> translates this definition into the
/// standard six-stage executable plan (EntityFields, Views, Relations, FieldControls,
/// ComplexPages, ConsistencyCheck). The plan ends at metadata; the agent never generates code.
/// </summary>
public sealed class BusinessPlanDefinition
{
    [JsonPropertyName("modules")]
    public List<BusinessModuleDefinition> Modules { get; set; } = [];

    [JsonPropertyName("entities")]
    public List<BusinessEntityDefinition> Entities { get; set; } = [];

    [JsonPropertyName("relations")]
    public List<BusinessRelationDefinition> Relations { get; set; } = [];

    [JsonPropertyName("views")]
    public List<BusinessViewDefinition> Views { get; set; } = [];

    [JsonPropertyName("pages")]
    public List<BusinessPageDefinition> Pages { get; set; } = [];

    /// <summary>
    /// Dictionary types the generated project needs. A field that declares
    /// <see cref="BusinessFieldDefinition.Dictionary"/> must be backed by an entry here, otherwise
    /// the generated select renders an empty list: the field only stores the dictionary code, it
    /// never creates the dictionary itself.
    /// </summary>
    [JsonPropertyName("dictionaries")]
    public List<BusinessDictionaryDefinition> Dictionaries { get; set; } = [];
}

public sealed class BusinessDictionaryDefinition
{
    /// <summary>Stable dictionary code referenced by field dictionary / SelectOptions, e.g. contract_status.</summary>
    [JsonPropertyName("dictType")]
    [Description("Stable snake_case code, e.g. invoice_type. This is the value a field's `dictionary` property references — NOT a data type such as string.")]
    public string DictType { get; set; } = string.Empty;

    /// <summary>Localized dictionary title shown in the generated project's dictionary page.</summary>
    [JsonPropertyName("dictName")]
    [Description("Human-readable title shown in the dictionary page, e.g. 发票类型. Not the code.")]
    public string DictName { get; set; } = string.Empty;

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    /// <summary>Optional entries. The type alone is already useful; entries may be filled later in the generated project.</summary>
    [JsonPropertyName("items")]
    public List<BusinessDictionaryItemDefinition> Items { get; set; } = [];
}

public sealed class BusinessDictionaryItemDefinition
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Stored value written to the business column.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; set; }

    [JsonPropertyName("sort")]
    public int? Sort { get; set; }
}

public sealed class BusinessModuleDefinition
{
    /// <summary>Stable key the model uses to reference this module across the definition.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>PascalCase technical name stored as CodeMaster ModuleName.</summary>
    [JsonPropertyName("technicalName")]
    public string TechnicalName { get; set; } = string.Empty;

    /// <summary>Localized human-readable title used as CodeMaster ModuleDescription.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("routePath")]
    public string? RoutePath { get; set; }

    [JsonPropertyName("orderNum")]
    public int? OrderNum { get; set; }
}

public sealed class BusinessEntityDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Module key (must reference one of <see cref="BusinessPlanDefinition.Modules"/>).</summary>
    [JsonPropertyName("moduleKey")]
    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>PascalCase technical entity name (CodeMaster Name).</summary>
    [JsonPropertyName("technicalName")]
    public string TechnicalName { get; set; } = string.Empty;

    /// <summary>Localized display title (CodeMaster Description).</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("businessDescription")]
    public string? BusinessDescription { get; set; }

    [JsonPropertyName("tableName")]
    public string? TableName { get; set; }

    [JsonPropertyName("isTree")]
    public bool? IsTree { get; set; }

    [JsonPropertyName("isReadOnly")]
    public bool? IsReadOnly { get; set; }

    [JsonPropertyName("hasTenant")]
    public bool? HasTenant { get; set; }

    [JsonPropertyName("hasAudit")]
    public bool? HasAudit { get; set; }

    [JsonPropertyName("hasSoftDelete")]
    public bool? HasSoftDelete { get; set; }

    [JsonPropertyName("fields")]
    public List<BusinessFieldDefinition> Fields { get; set; } = [];
}

public sealed class BusinessFieldDefinition
{
    [JsonPropertyName("technicalName")]
    public string TechnicalName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Simple data type keyword. The compiler maps to CodeMaster FormControlType and database
    /// column type. Allowed values: string, int, long, decimal, datetime, date, bool.
    /// </summary>
    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool? Required { get; set; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; set; }

    [JsonPropertyName("dictionary")]
    [Description("Dictionary code for an enumeration field (status, type, category, level, method …), e.g. contract_status. The field then renders as a select bound to that dictionary. Declare the same code under dictionaries — a code that is referenced but not declared renders an empty select.")]
    public string? Dictionary { get; set; }

    /// <summary>Free-form hint: "primary key", "foreign key", "lookup", "summary", etc.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

public sealed class BusinessRelationDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty; // OwnedOneToMany, OwnedOneToOne, ReferenceManyToOne

    [JsonPropertyName("sourceEntityKey")]
    public string SourceEntityKey { get; set; } = string.Empty;

    [JsonPropertyName("sourceField")]
    public string? SourceField { get; set; }

    [JsonPropertyName("targetEntityKey")]
    public string TargetEntityKey { get; set; } = string.Empty;

    [JsonPropertyName("targetField")]
    public string? TargetField { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}

public sealed class BusinessViewDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("entityKey")]
    public string EntityKey { get; set; } = string.Empty;

    [JsonPropertyName("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Single read-only SELECT statement; the compiler wraps it as a view_definition.</summary>
    [JsonPropertyName("sql")]
    public string Sql { get; set; } = string.Empty;
}

public sealed class BusinessPageDefinition
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("entityKey")]
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>"index", "add", "edit", "detail".</summary>
    [JsonPropertyName("pageType")]
    public string PageType { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;
}