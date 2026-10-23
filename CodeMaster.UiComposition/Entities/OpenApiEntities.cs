using CodeMaster.Core.Entities;
using SqlSugar;

namespace CodeMaster.UiComposition.Entities;

public enum OpenApiDocumentStatus
{
    Active = 1,
    Invalid = 2,
    Missing = 3
}

[Flags]
public enum OpenApiDtoRole
{
    None = 0,
    Request = 1,
    Response = 2,
    Shared = 4,
    Parameter = 8,
    Inline = 16
}

public enum OpenApiMemberKind
{
    Scalar = 1,
    Object = 2,
    Collection = 3,
    Dictionary = 4,
    Union = 5
}

public enum OpenApiSyncState
{
    Active = 1,
    Added = 2,
    Modified = 3,
    PendingDelete = 4,
    RenameCandidate = 5,
    Conflict = 6,
    Detached = 7,
    Missing = 8
}

[SugarTable("sys_openapi_document")]
public sealed class OpenApiDocumentRegistration : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "module_id", IsNullable = false)]
    public long ModuleId { get; set; }

    [SugarColumn(ColumnName = "name", Length = 200, IsNullable = false)]
    public string Name { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "source_url", Length = 2000, IsNullable = true)]
    public string? SourceUrl { get; set; }

    [SugarColumn(ColumnName = "title", Length = 300, IsNullable = true)]
    public string? Title { get; set; }

    [SugarColumn(ColumnName = "description", Length = 2000, IsNullable = true)]
    public string? Description { get; set; }

    [SugarColumn(ColumnName = "openapi_version", Length = 50, IsNullable = false)]
    public string OpenApiVersion { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "document_hash", Length = 64, IsNullable = false)]
    public string DocumentHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "raw_document", ColumnDataType = "text", IsNullable = false)]
    public string RawDocument { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "normalized_snapshot_json", ColumnDataType = "text", IsNullable = false)]
    public string NormalizedSnapshotJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "status", IsNullable = false)]
    public OpenApiDocumentStatus Status { get; set; } = OpenApiDocumentStatus.Active;

    [SugarColumn(ColumnName = "last_synced_time", IsNullable = true)]
    public DateTime? LastSyncedTime { get; set; }

    [SugarColumn(ColumnName = "last_error", Length = 2000, IsNullable = true)]
    public string? LastError { get; set; }
}

[SugarTable("sys_openapi_schema_binding")]
public sealed class OpenApiSchemaBinding : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "document_id", IsNullable = false)]
    public long DocumentId { get; set; }

    [SugarColumn(ColumnName = "project_id", IsNullable = false)]
    public long ProjectId { get; set; }

    [SugarColumn(ColumnName = "module_id", IsNullable = false)]
    public long ModuleId { get; set; }

    [SugarColumn(ColumnName = "module_entity_id", IsNullable = false)]
    public long ModuleEntityId { get; set; }

    [SugarColumn(ColumnName = "schema_key", Length = 500, IsNullable = false)]
    public string SchemaKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "schema_name", Length = 300, IsNullable = false)]
    public string SchemaName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dto_name", Length = 300, IsNullable = false)]
    public string DtoName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "dto_role", IsNullable = false)]
    public OpenApiDtoRole DtoRole { get; set; } = OpenApiDtoRole.Shared;

    [SugarColumn(ColumnName = "schema_hash", Length = 64, IsNullable = false)]
    public string SchemaHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "snapshot_json", ColumnDataType = "text", IsNullable = false)]
    public string SnapshotJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "sync_state", IsNullable = false)]
    public OpenApiSyncState SyncState { get; set; } = OpenApiSyncState.Active;

    [SugarColumn(ColumnName = "last_synced_time", IsNullable = true)]
    public DateTime? LastSyncedTime { get; set; }
}

[SugarTable("sys_openapi_member_binding")]
public sealed class OpenApiMemberBinding : EntityBaseWithTenant
{
    [SugarColumn(ColumnName = "document_id", IsNullable = false)]
    public long DocumentId { get; set; }

    [SugarColumn(ColumnName = "schema_binding_id", IsNullable = false)]
    public long SchemaBindingId { get; set; }

    [SugarColumn(ColumnName = "entity_field_id", IsNullable = true)]
    public long? EntityFieldId { get; set; }

    [SugarColumn(ColumnName = "target_schema_binding_id", IsNullable = true)]
    public long? TargetSchemaBindingId { get; set; }

    [SugarColumn(ColumnName = "member_key", Length = 700, IsNullable = false)]
    public string MemberKey { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "external_id", Length = 300, IsNullable = true)]
    public string? ExternalId { get; set; }

    [SugarColumn(ColumnName = "json_name", Length = 300, IsNullable = false)]
    public string JsonName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "member_name", Length = 300, IsNullable = false)]
    public string MemberName { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "member_kind", IsNullable = false)]
    public OpenApiMemberKind MemberKind { get; set; } = OpenApiMemberKind.Scalar;

    [SugarColumn(ColumnName = "data_type", Length = 300, IsNullable = true)]
    public string? DataType { get; set; }

    [SugarColumn(ColumnName = "format", Length = 100, IsNullable = true)]
    public string? Format { get; set; }

    [SugarColumn(ColumnName = "is_required", IsNullable = false)]
    public bool IsRequired { get; set; }

    [SugarColumn(ColumnName = "is_nullable", IsNullable = false)]
    public bool IsNullable { get; set; }

    [SugarColumn(ColumnName = "is_collection", IsNullable = false)]
    public bool IsCollection { get; set; }

    [SugarColumn(ColumnName = "member_hash", Length = 64, IsNullable = false)]
    public string MemberHash { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "snapshot_json", ColumnDataType = "text", IsNullable = false)]
    public string SnapshotJson { get; set; } = "{}";

    [SugarColumn(ColumnName = "sync_state", IsNullable = false)]
    public OpenApiSyncState SyncState { get; set; } = OpenApiSyncState.Active;

    [SugarColumn(ColumnName = "last_synced_time", IsNullable = true)]
    public DateTime? LastSyncedTime { get; set; }
}
