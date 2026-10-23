using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.UiComposition.Dtos;

public sealed class OpenApiDocumentDto
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string OpenApiVersion { get; set; } = string.Empty;
    public string DocumentHash { get; set; } = string.Empty;
    public OpenApiDocumentStatus Status { get; set; }
    public DateTime? LastSyncedTime { get; set; }
    public string? LastError { get; set; }
}

public class PreviewOpenApiImportDto
{
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public long? DocumentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string? DocumentContent { get; set; }
}

public sealed class ApplyOpenApiImportDto : PreviewOpenApiImportDto
{
    public string ExpectedDocumentHash { get; set; } = string.Empty;
    public string ExpectedChangeSetHash { get; set; } = string.Empty;
    public bool ApplyRemovals { get; set; }
    public List<string> RemoveChangeKeys { get; set; } = new();
    public List<string> DetachChangeKeys { get; set; } = new();
}

public sealed class OpenApiImportPreviewDto
{
    public long? DocumentId { get; set; }
    public long ProjectId { get; set; }
    public long ModuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OpenApiVersion { get; set; } = string.Empty;
    public string DocumentHash { get; set; } = string.Empty;
    public string ChangeSetHash { get; set; } = string.Empty;
    public List<OpenApiOperationPreviewDto> Operations { get; set; } = new();
    public List<OpenApiSchemaPreviewDto> Schemas { get; set; } = new();
    public List<OpenApiChangeDto> Changes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class OpenApiApplyResultDto
{
    public long DocumentId { get; set; }
    public string DocumentHash { get; set; } = string.Empty;
    public string ChangeSetHash { get; set; } = string.Empty;
    public int AddedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int PendingDeleteCount { get; set; }
    public int RemovedCount { get; set; }
    public int ConflictCount { get; set; }
    public List<long> DataSourceIds { get; set; } = new();
    public List<long> EntityIds { get; set; } = new();
    public List<long> AffectedPageIds { get; set; } = new();
    public List<OpenApiAffectedPageDto> AffectedPages { get; set; } = new();
}

public sealed class OpenApiAffectedPageDto
{
    public long PageId { get; set; }
    public string PageKey { get; set; } = string.Empty;
    public string PageName { get; set; } = string.Empty;
    public PageSyncMode SyncMode { get; set; }
    public PageSyncStatus SyncStatus { get; set; }
}

public sealed class OpenApiOperationPreviewDto
{
    public long? ExistingDataSourceId { get; set; }
    public string OperationKey { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RoutePath { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? RequestSchemaKey { get; set; }
    public string? ResponseSchemaKey { get; set; }
    public string? ResponseEnvelopeSchemaKey { get; set; }
    public string? RequestDtoName { get; set; }
    public string? ResponseDtoName { get; set; }
    public string? ResponseEnvelopeDtoName { get; set; }
    public PageDataShape DataShape { get; set; } = PageDataShape.Single;
    public string ParametersJson { get; set; } = "[]";
    public string ContractJson { get; set; } = "{}";
    public OpenApiSyncState SyncState { get; set; } = OpenApiSyncState.Active;
}

public sealed class OpenApiSchemaPreviewDto
{
    public string SchemaKey { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public OpenApiDtoRole DtoRole { get; set; }
    public string SchemaHash { get; set; } = string.Empty;
    public long? ExistingEntityId { get; set; }
    public OpenApiSyncState SyncState { get; set; } = OpenApiSyncState.Active;
    public List<OpenApiMemberPreviewDto> Members { get; set; } = new();
}

public sealed class OpenApiMemberPreviewDto
{
    public string MemberKey { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public string JsonName { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public OpenApiMemberKind MemberKind { get; set; }
    public string? DataType { get; set; }
    public string? Format { get; set; }
    public bool IsRequired { get; set; }
    public bool IsNullable { get; set; }
    public bool IsCollection { get; set; }
    public string? TargetSchemaKey { get; set; }
    public string? TargetDtoName { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public List<string> EnumValues { get; set; } = new();
    public bool IsUnsupported { get; set; }
    public string MemberHash { get; set; } = string.Empty;
    public long? ExistingFieldId { get; set; }
    public OpenApiSyncState SyncState { get; set; } = OpenApiSyncState.Active;
}

public sealed class OpenApiChangeDto
{
    public string ChangeKey { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string TargetName { get; set; } = string.Empty;
    public string? ParentName { get; set; }
    public OpenApiSyncState State { get; set; }
    public bool IsBreaking { get; set; }
    public string Description { get; set; } = string.Empty;
    public long? EntityId { get; set; }
    public long? FieldId { get; set; }
    public string? PreviousSnapshotJson { get; set; }
    public string? CurrentSnapshotJson { get; set; }
}

public sealed class OpenApiSchemaTreeDto
{
    public long EntityId { get; set; }
    public string SchemaKey { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public List<OpenApiSchemaTreeMemberDto> Members { get; set; } = new();
}

public sealed class OpenApiSchemaTreeMemberDto
{
    public long? FieldId { get; set; }
    public string JsonName { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string? DataType { get; set; }
    public OpenApiMemberKind MemberKind { get; set; }
    public bool IsRequired { get; set; }
    public bool IsNullable { get; set; }
    public bool IsCollection { get; set; }
    public long? TargetEntityId { get; set; }
    public string? TargetDtoName { get; set; }
    public OpenApiSyncState SyncState { get; set; }
    public List<OpenApiSchemaTreeMemberDto> Children { get; set; } = new();
}
