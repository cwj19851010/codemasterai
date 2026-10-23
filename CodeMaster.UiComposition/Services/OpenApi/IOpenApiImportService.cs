using CodeMaster.Core.Services;
using CodeMaster.UiComposition.Dtos;

namespace CodeMaster.Application.Services.CodeGen;

public interface IOpenApiImportService : IApplicationService
{
    Task<List<OpenApiDocumentDto>> GetDocumentsAsync(long projectId);
    Task<OpenApiImportPreviewDto> PreviewAsync(PreviewOpenApiImportDto input);
    Task<OpenApiApplyResultDto> ApplyAsync(ApplyOpenApiImportDto input);
    Task<OpenApiSchemaTreeDto?> GetSchemaTreeAsync(long entityId);
}
