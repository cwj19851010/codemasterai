using CodeMaster.Core.Entities;
using CodeMaster.Core.Enums;
using SqlSugar;

namespace CodeMaster.UiComposition.Entities;

public enum ProjectViewOwnership
{
    Managed = 1,
    ImportedExternal = 2
}

public enum ProjectViewDeploymentState
{
    DefinitionOnly = 0,
    Pending = 1,
    Applied = 2,
    Drifted = 3,
    Failed = 4,
    External = 5
}

public enum ProjectViewDependencyKind
{
    ManagedEntity = 1,
    ManagedView = 2,
    ExternalTable = 3,
    ExternalView = 4
}

public enum ProjectViewLineageKind
{
    Direct = 1,
    Computed = 2,
    Aggregate = 3,
    Constant = 4,
    Unknown = 5
}

[SugarTable("sys_project_view_definition")]
public sealed class ProjectViewDefinition : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "definition_key", Length = 100, IsNullable = false)]
    public string DefinitionKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "view_name", Length = 200, IsNullable = false)]
    public string ViewName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "schema_name", Length = 200, IsNullable = true)]
    public string? SchemaName { get; set; }

    [SugarColumn(ColumnName = "database_type", IsNullable = false)]
    public DatabaseType DatabaseType { get; set; }

    [SugarColumn(ColumnName = "sql_text", ColumnDataType = "text", IsNullable = true)]
    public string? Sql { get; set; }

    [SugarColumn(ColumnName = "sql_hash", Length = 64, IsNullable = true)]
    public string? SqlHash { get; set; }

    [SugarColumn(ColumnName = "definition_version", IsNullable = false)]
    public long DefinitionVersion { get; set; } = 1;

    [SugarColumn(ColumnName = "definition_hash", Length = 64, IsNullable = false)]
    public string DefinitionHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "key_column", Length = 200, IsNullable = true)]
    public string? KeyColumn { get; set; }

    [SugarColumn(ColumnName = "ownership", IsNullable = false)]
    public ProjectViewOwnership Ownership { get; set; } = ProjectViewOwnership.Managed;

    [SugarColumn(ColumnName = "deployment_state", IsNullable = false)]
    public ProjectViewDeploymentState DeploymentState { get; set; } = ProjectViewDeploymentState.DefinitionOnly;

    [SugarColumn(ColumnName = "declared_schema_json", ColumnDataType = "text", IsNullable = false)]
    public string DeclaredSchemaJson { get; set; } = "[]";

    [SugarColumn(ColumnName = "declared_schema_hash", Length = 64, IsNullable = false)]
    public string DeclaredSchemaHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "actual_schema_json", ColumnDataType = "text", IsNullable = true)]
    public string? ActualSchemaJson { get; set; }

    [SugarColumn(ColumnName = "actual_schema_hash", Length = 64, IsNullable = true)]
    public string? ActualSchemaHash { get; set; }

    [SugarColumn(ColumnName = "applied_definition_hash", Length = 64, IsNullable = true)]
    public string? AppliedDefinitionHash { get; set; }

    [SugarColumn(ColumnName = "module_entity_id", IsNullable = true)]
    public long? ModuleEntityId { get; set; }

    [SugarColumn(ColumnName = "project_data_source_id", IsNullable = true)]
    public long? ProjectDataSourceId { get; set; }

    [SugarColumn(ColumnName = "allow_replace", IsNullable = false)]
    public bool AllowReplace { get; set; }

    [SugarColumn(ColumnName = "last_deployed_at", IsNullable = true)]
    public DateTime? LastDeployedAt { get; set; }

    [SugarColumn(ColumnName = "last_inspected_at", IsNullable = true)]
    public DateTime? LastInspectedAt { get; set; }

    [SugarColumn(ColumnName = "last_error", Length = 2000, IsNullable = true)]
    public string? LastError { get; set; }
}

[SugarTable("sys_project_view_dependency")]
public sealed class ProjectViewDependency : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "definition_id", IsNullable = false)]
    public long DefinitionId { get; set; }

    [SugarColumn(ColumnName = "dependency_key", Length = 100, IsNullable = false)]
    public string DependencyKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dependency_kind", IsNullable = false)]
    public ProjectViewDependencyKind Kind { get; set; }

    [SugarColumn(ColumnName = "physical_schema", Length = 200, IsNullable = true)]
    public string? PhysicalSchema { get; set; }

    [SugarColumn(ColumnName = "physical_name", Length = 200, IsNullable = false)]
    public string PhysicalName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "source_alias", Length = 100, IsNullable = true)]
    public string? SourceAlias { get; set; }

    [SugarColumn(ColumnName = "source_role", Length = 100, IsNullable = true)]
    public string? SourceRole { get; set; }

    [SugarColumn(ColumnName = "module_entity_id", IsNullable = true)]
    public long? ModuleEntityId { get; set; }

    [SugarColumn(ColumnName = "source_view_definition_id", IsNullable = true)]
    public long? SourceViewDefinitionId { get; set; }
}

[SugarTable("sys_project_view_field_lineage")]
public sealed class ProjectViewFieldLineage : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "definition_id", IsNullable = false)]
    public long DefinitionId { get; set; }

    [SugarColumn(ColumnName = "output_name", Length = 200, IsNullable = false)]
    public string OutputName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "output_column", Length = 200, IsNullable = false)]
    public string OutputColumn { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "data_type", Length = 100, IsNullable = false)]
    public string DataType { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "is_nullable", IsNullable = false)]
    public bool IsNullable { get; set; }

    [SugarColumn(ColumnName = "ordinal", IsNullable = false)]
    public int Ordinal { get; set; }

    [SugarColumn(ColumnName = "lineage_kind", IsNullable = false)]
    public ProjectViewLineageKind LineageKind { get; set; } = ProjectViewLineageKind.Unknown;

    [SugarColumn(ColumnName = "source_dependency_id", IsNullable = true)]
    public long? SourceDependencyId { get; set; }

    [SugarColumn(ColumnName = "source_entity_id", IsNullable = true)]
    public long? SourceEntityId { get; set; }

    [SugarColumn(ColumnName = "source_field_id", IsNullable = true)]
    public long? SourceFieldId { get; set; }

    [SugarColumn(ColumnName = "expression", ColumnDataType = "text", IsNullable = true)]
    public string? Expression { get; set; }

    [SugarColumn(ColumnName = "lineage_notes", Length = 1000, IsNullable = true)]
    public string? LineageNotes { get; set; }
}
