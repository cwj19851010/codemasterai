using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Compatibility guard for OpenAPI bindings created before ModuleEntity.GenerationMode existed.
/// OpenAPI imports are metadata contracts and never generate DTO or backend artifacts.
/// </summary>
public sealed class OpenApiDtoCodeGenerationExtension : ICodeGenerationEntityExtension
{
    private readonly ISqlSugarClient _db;

    public OpenApiDtoCodeGenerationExtension(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task<bool> CanHandleAsync(long entityId)
    {
        if (!_db.DbMaintenance.IsAnyTable("sys_openapi_schema_binding", false)) return false;
        return await _db.Queryable<OpenApiSchemaBinding>()
            .AnyAsync(item => item.ModuleEntityId == entityId && !item.IsDeleted);
    }

    public Task GenerateAsync(CodeGenerationEntityContext context)
        => throw new InvalidOperationException(
            $"OpenAPI entity {context.EntityName} is metadata-only and cannot generate DTO or backend code.");

    public Task<bool> IsOutputCurrentAsync(CodeGenerationEntityContext context)
        => Task.FromResult(true);
}
