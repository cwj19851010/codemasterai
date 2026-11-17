using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Dtos.CodeGen;

public sealed class ViewBuilderCatalogDto
{
    public long ProjectId { get; set; }
    public DatabaseType DatabaseType { get; set; }
    public List<ViewBuilderEntityDto> Entities { get; set; } = new();
    public List<ViewBuilderJoinCandidateDto> JoinCandidates { get; set; } = new();
}

public sealed class ViewBuilderEntityDto
{
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool HasPrimaryKey { get; set; }
    public bool IsReadOnly { get; set; }
    public List<ViewBuilderFieldDto> Fields { get; set; } = new();
}

public sealed class ViewBuilderFieldDto
{
    public long FieldId { get; set; }
    public long EntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsSystemField { get; set; }
    public string FieldCategory { get; set; } = "Normal";
    public int OrderNum { get; set; }
}

public sealed class ViewBuilderJoinCandidateDto
{
    public long? RelationId { get; set; }
    public long SourceEntityId { get; set; }
    public long TargetEntityId { get; set; }
    public string RelationName { get; set; } = string.Empty;
    public string SourceField { get; set; } = string.Empty;
    public string TargetField { get; set; } = string.Empty;
    public string Cardinality { get; set; } = string.Empty;
    public string Ownership { get; set; } = string.Empty;
    public string DefaultJoinType { get; set; } = "Left";
    public bool IsRequired { get; set; }
    public bool IsInferred { get; set; } = true;
}

public sealed class PreviewViewSqlDto
{
    public long ProjectId { get; set; }
    /// <summary>
    /// Optional developer-edited SELECT/CTE. When supplied, the builder returns it
    /// unchanged and does not constrain it to the visual entity/field selections.
    /// </summary>
    public string? Sql { get; set; }
    public List<ViewBuilderEntitySelectionDto> Entities { get; set; } = new();
    public List<ViewBuilderJoinDto> Joins { get; set; } = new();
    public bool IncludeInferredJoins { get; set; } = true;
}

public sealed class ViewBuilderEntitySelectionDto
{
    public long EntityId { get; set; }
    public List<long> FieldIds { get; set; } = new();
}

public sealed class ViewBuilderJoinDto
{
    public long SourceEntityId { get; set; }
    public long TargetEntityId { get; set; }
    public string SourceField { get; set; } = string.Empty;
    public string TargetField { get; set; } = string.Empty;
    public string JoinType { get; set; } = "Left";
    public string? RelationName { get; set; }
    public bool IsInferred { get; set; }
}

public sealed class ViewBuilderSelectedFieldDto
{
    public long EntityId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public long FieldId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
}

public sealed class ViewBuilderSqlPreviewDto
{
    public long ProjectId { get; set; }
    public DatabaseType DatabaseType { get; set; }
    public string Sql { get; set; } = string.Empty;
    public List<ViewBuilderSelectedFieldDto> SelectedFields { get; set; } = new();
    public List<ViewBuilderJoinDto> Joins { get; set; } = new();
}

public sealed class CreateViewDefinitionDto
{
    public long ProjectId { get; set; }
    public string ViewName { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public List<string> ExpectedColumns { get; set; } = new();
    public bool ReplaceExistingView { get; set; } = true;
}

public sealed class ViewBuilderViewColumnDto
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public int Ordinal { get; set; }
}

public sealed class ViewBuilderViewResultDto
{
    public long ProjectId { get; set; }
    public DatabaseType DatabaseType { get; set; }
    public string ViewName { get; set; } = string.Empty;
    public string Sql { get; set; } = string.Empty;
    public bool ReplacedExistingView { get; set; }
    public bool RequiresColumnConfirmation { get; set; }
    public List<string> ExpectedColumns { get; set; } = new();
    public List<string> MissingColumns { get; set; } = new();
    public List<string> ExtraColumns { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<ViewBuilderViewColumnDto> Columns { get; set; } = new();
}

/// <summary>
/// User-declared mapping between one generated entity property and one output
/// column of the final view SQL. The declaration is trusted as code-generation
/// metadata; database column inference remains available when no declarations
/// are supplied.
/// </summary>
public sealed class ViewEntityFieldDefinitionDto
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";
    public bool IsNullable { get; set; }
    public int? MaxLength { get; set; }
    public int? Precision { get; set; }
    public int? Scale { get; set; }
    public bool ShowInList { get; set; } = true;
    public bool ShowInDetail { get; set; } = true;
    public bool ShowInSearch { get; set; }
    public bool IsSortable { get; set; }
    public string? FormControlType { get; set; }
    public int OrderNum { get; set; }
}

public sealed class RegisterReadOnlyViewEntityDto
{
    public long? ViewDefinitionId { get; set; }
    public long? ExistingEntityId { get; set; }
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public string ViewName { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? KeyColumn { get; set; }
    /// <summary>
    /// Whether the generated read-only consumer gets a primary key.
    /// <c>null</c> infers one from <see cref="KeyColumn"/> or the first integral output;
    /// <c>false</c> registers a keyless consumer (list/query only, no GetById, no EF key),
    /// which is the correct shape for a view that aggregates unrelated rows.
    /// </summary>
    public bool? HasPrimaryKey { get; set; }
    public List<ViewEntityFieldDefinitionDto> Fields { get; set; } = new();
    public bool GenerateFrontend { get; set; } = true;
    /// <summary>
    /// Allows a durable managed definition to register its declared consumer
    /// before the physical view exists. The final project migration deploys the
    /// view from the generated migration module.
    /// </summary>
    public bool AllowUnappliedDefinition { get; set; }
    public PageDataSourceType DataSourceType { get; set; } = PageDataSourceType.EntityService;
}

public sealed class ViewBuilderExistingViewDto
{
    public string ViewName { get; set; } = string.Empty;
}

public sealed class ReadOnlyViewEntityResultDto
{
    public long EntityId { get; set; }
    public long DataSourceId { get; set; }
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public string EntityName { get; set; } = string.Empty;
    public string ViewName { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public bool HasTenant { get; set; }
    public bool HasDataPermission { get; set; }
    public bool GenerateFrontend { get; set; }
    public List<ViewBuilderViewColumnDto> Columns { get; set; } = new();
}
