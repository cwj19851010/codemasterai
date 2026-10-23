using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IProjectPageService : IApplicationService
{
    Task<List<ProjectPageDto>> GetListAsync(ProjectPageQueryDto query);
    Task<PagedResultDto<ProjectPageDto>> GetPagedListAsync(ProjectPageQueryDto query);
    Task<ProjectPageDto?> GetByIdAsync(long id);
    Task<PageDefinitionSnapshotDto> GetDefinitionSnapshotAsync(long id);
    Task<long> CreateAsync(CreateProjectPageDto input);
    Task<int> UpdateAsync(long id, UpdateProjectPageDto input);
    Task<int> DeleteAsync(long id);
    Task<long> CopyAsync(long id, string pageKey, string pageName);
    Task<ProjectPageDto> SaveDefinitionAsync(long id, UpdateProjectPageDto input);
    Task<ProjectPageDto> RebindComponentAsync(long id, RebindPageComponentDto input);
    Task<PageDefinitionValidationDto> ValidateDefinitionAsync(ValidatePageDefinitionDto input);
    Task<List<PageLayoutPresetDto>> GetLayoutPresetsAsync();
    Task<string> GetLayoutTreeAsync(string presetKey);
    Task<List<EntityServiceCapabilityDto>> GetEntityServiceCapabilitiesAsync(long projectId);
    Task<List<PageImpactDto>> GetAffectedPagesByEntityIdAsync(long entityId);
    Task<int> MarkNeedsSyncAsync(long entityId);
    Task<string> ComputeEntitySchemaHashAsync(long entityId);
}
