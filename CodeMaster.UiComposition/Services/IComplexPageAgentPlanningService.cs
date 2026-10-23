using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IComplexPageAgentPlanningService : IApplicationService
{
    Task<ComplexPageAgentContextDto> GetContextAsync(long projectId, long? pageId = null);
    Task<ComplexPageAgentPlanValidationDto> ValidateAsync(long projectId, ComplexPageAgentPlanDto plan);
    Task<ComplexPageAgentApplyResultDto> ApplyAsync(long projectId, ComplexPageAgentPlanDto plan);
}
