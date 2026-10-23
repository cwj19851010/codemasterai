using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IComplexPageGenerationService : IApplicationService
{
    Task<ComplexPageGenerationResultDto> GenerateAsync(long pageId, bool incremental = true);
}
