using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IProjectViewBuilderService : IApplicationService
{
    Task<ViewBuilderCatalogDto> GetEntityCatalogAsync(long projectId);
    Task<ViewBuilderSqlPreviewDto> PreviewSqlAsync(PreviewViewSqlDto input);
    Task<ViewBuilderViewResultDto> CreateViewAsync(CreateViewDefinitionDto input);
    Task<List<ViewBuilderExistingViewDto>> GetExistingViewsAsync(long projectId);
    Task<ReadOnlyViewEntityResultDto> RegisterReadOnlyViewEntityAsync(RegisterReadOnlyViewEntityDto input);
    Task<ReadOnlyViewEntityResultDto> ImportExistingViewAsync(RegisterReadOnlyViewEntityDto input);
}
