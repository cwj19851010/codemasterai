using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.UiComposition.Services.OpenApi;

internal sealed class NormalizedOpenApiDocument
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OpenApiVersion { get; set; } = string.Empty;
    public string DocumentHash { get; set; } = string.Empty;
    public List<NormalizedOpenApiOperation> Operations { get; set; } = new();
    public List<NormalizedOpenApiSchema> Schemas { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

internal sealed class NormalizedOpenApiOperation
{
    public string OperationKey { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string RoutePath { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public string? RequestSchemaKey { get; set; }
    public string? ResponseSchemaKey { get; set; }
    public string? ResponseEnvelopeSchemaKey { get; set; }
    public PageDataShape DataShape { get; set; } = PageDataShape.Single;
    public List<NormalizedOpenApiParameter> Parameters { get; set; } = new();
    public List<string> RequestContentTypes { get; set; } = new();
    public List<string> ResponseContentTypes { get; set; } = new();
    public string ContractHash { get; set; } = string.Empty;
}

internal sealed class NormalizedOpenApiParameter
{
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsRequired { get; set; }
    public string? DataType { get; set; }
    public string? Format { get; set; }
    public string? SchemaKey { get; set; }
}

internal sealed class NormalizedOpenApiSchema
{
    public string SchemaKey { get; set; } = string.Empty;
    public string SchemaName { get; set; } = string.Empty;
    public string DtoName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public OpenApiDtoRole DtoRole { get; set; } = OpenApiDtoRole.Shared;
    public List<NormalizedOpenApiMember> Members { get; set; } = new();
    public string SchemaHash { get; set; } = string.Empty;
}

internal sealed class NormalizedOpenApiMember
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
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public List<string> EnumValues { get; set; } = new();
    public bool IsUnsupported { get; set; }
    public string MemberHash { get; set; } = string.Empty;
}
