using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IProjectViewDefinitionService : IApplicationService
{
    Task<List<ProjectViewDefinitionDto>> GetListAsync(long projectId);
    Task<ProjectViewDefinitionDto?> GetByIdAsync(long id);
    Task<ProjectViewDefinitionDto> SaveAsync(SaveProjectViewDefinitionDto input);
    Task<ProjectViewDeploymentResultDto> DeployAsync(DeployProjectViewDefinitionDto input);
    Task<int> DeleteAsync(long id);
    Task<ProjectViewSchemaDiffDto> GetSchemaDiffAsync(long definitionId);
}
