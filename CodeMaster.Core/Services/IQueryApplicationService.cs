using CodeMaster.Core.Dtos;
using CodeMaster.Core.Entities;

namespace CodeMaster.Core.Services;

/// <summary>
/// Query-only application service contract for entities without a primary key.
/// </summary>
public interface IQueryApplicationService<TEntity, TGetListOutputDto, in TGetListInput> : IApplicationService
    where TEntity : class, IBaseEntity, new()
{
    Task<PagedResultDto<TGetListOutputDto>> GetPagedListAsync(TGetListInput input);

    Task<List<TGetListOutputDto>> GetListAsync(TGetListInput input);

    /// <summary>
    /// 获取选项数据。默认复用不分页查询，供页面下拉/选项数据源使用。
    /// </summary>
    Task<List<TGetListOutputDto>> GetOptionListAsync(TGetListInput input);

    Task<byte[]> ExportAsync(TGetListInput input);
}
