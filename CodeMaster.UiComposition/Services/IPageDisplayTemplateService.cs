using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Services;

namespace CodeMaster.Application.Services.CodeGen;

public interface IPageDisplayTemplateService : IApplicationService
{
    Task<List<PageDisplayTemplateDto>> GetListAsync(PageDisplayTemplateQueryDto? query = null);
    Task<PageDisplayTemplateDto?> GetByIdAsync(long id);
    Task<long> CreateAsync(CreatePageDisplayTemplateDto input);
    Task<int> UpdateAsync(long id, UpdatePageDisplayTemplateDto input);
    Task<int> DeleteAsync(long id);
}
