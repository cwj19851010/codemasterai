using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IComplexPageExternalSyncService : IApplicationService
{
    Task<ComplexPageExternalChangeDto> InspectAsync(long pageId);
    Task<ComplexPageExternalChangeDto> ImportTemplateAsync(long pageId, bool confirmed = false);
    Task<ComplexPageExternalChangeDto> RejectImportAsync(long pageId);
    Task<ComplexPageExternalChangeDto> RestoreBackupAsync(long pageId);
}
