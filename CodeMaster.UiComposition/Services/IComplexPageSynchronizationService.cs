using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IComplexPageSynchronizationService : IApplicationService
{
    Task<ComplexPageSyncPreviewDto> PreviewAsync(long pageId);
    Task<ComplexPageSyncResultDto> SynchronizeAsync(long pageId);
    Task<List<ComplexPageSyncResultDto>> SynchronizeProjectAsync(long projectId);
}
