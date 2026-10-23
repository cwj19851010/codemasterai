using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IProjectDataSourceService : IApplicationService
{
    Task<List<ProjectDataSourceDto>> GetListAsync(DataSourceQueryDto query);
    Task<PagedResultDto<ProjectDataSourceDto>> GetPagedListAsync(DataSourceQueryDto query);
    Task<ProjectDataSourceDto?> GetByIdAsync(long id);
    Task<ProjectDataSourceCapabilityCatalogDto> GetCapabilityCatalogAsync(long projectId);
    Task<long> CreateAsync(CreateProjectDataSourceDto input);
    Task<long> RegisterViewAsync(CreateProjectDataSourceDto input);
    Task<int> UpdateAsync(long id, UpdateProjectDataSourceDto input);
    Task<DataSourceChangeResultDto> UpdateWithImpactAsync(long id, UpdateProjectDataSourceDto input);
    Task<int> DeleteAsync(long id);
    Task<DataSourceChangeResultDto> DeleteWithImpactAsync(long id);
    Task<string> ComputeSchemaHashAsync(long id);
    Task<List<DataSourceImpactDto>> GetAffectedPagesAsync(long dataSourceId);
    Task<List<DataSourceImpactDto>> QueryAffectedPagesAsync(AffectedPageQueryDto query);
    Task<long> BindPageAsync(CreatePageDataSourceBindingDto input);
    Task<int> UnbindPageAsync(long bindingId);
    Task<List<PageDataSourceBindingDto>> GetPageBindingsAsync(long? pageId, long? moduleEntityId, string? pageType);
}
