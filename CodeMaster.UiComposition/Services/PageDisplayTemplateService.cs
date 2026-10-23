using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.DynamicApi;
using CodeMaster.UiComposition.Entities;
using Mapster;
using SqlSugar;
using System.Text.RegularExpressions;

namespace CodeMaster.Application.Services.CodeGen;

[DynamicApiPermission(requirePermission: true)]
public sealed class PageDisplayTemplateService : IPageDisplayTemplateService
{
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    private readonly ISqlSugarClient _db;

    public PageDisplayTemplateService(ISqlSugarClient db)
    {
        _db = db;
    }

    [Permission("codegen:projectpage:list")]
    public async Task<List<PageDisplayTemplateDto>> GetListAsync(PageDisplayTemplateQueryDto? query = null)
    {
        query ??= new PageDisplayTemplateQueryDto();
        var statement = _db.Queryable<PageDisplayTemplate>().Where(item => !item.IsDeleted);
        if (query.Scope.HasValue)
            statement = statement.Where(item => item.Scope == query.Scope.Value);
        if (!string.IsNullOrWhiteSpace(query.TargetType))
            statement = statement.Where(item => item.TargetType == query.TargetType);
        if (!query.IncludeDisabled)
            statement = statement.Where(item => item.IsEnabled);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            statement = statement.Where(item => item.TemplateKey.Contains(keyword) || item.TemplateName.Contains(keyword));
        }
        return (await statement.OrderBy(item => item.Sort).OrderBy(item => item.Id).ToListAsync())
            .Adapt<List<PageDisplayTemplateDto>>();
    }

    [Permission("codegen:projectpage:view")]
    public async Task<PageDisplayTemplateDto?> GetByIdAsync(long id)
        => (await _db.Queryable<PageDisplayTemplate>()
                .Where(item => item.Id == id && !item.IsDeleted)
                .FirstAsync())?.Adapt<PageDisplayTemplateDto>();

    [Permission("codegen:projectpage:create")]
    public async Task<long> CreateAsync(CreatePageDisplayTemplateDto input)
    {
        ValidateInput(input, null);
        var entity = input.Adapt<PageDisplayTemplate>();
        ApplyAudit(entity, true);
        return await _db.Insertable(entity).ExecuteReturnSnowflakeIdAsync();
    }

    [Permission("codegen:projectpage:update")]
    public async Task<int> UpdateAsync(long id, UpdatePageDisplayTemplateDto input)
    {
        var existing = await _db.Queryable<PageDisplayTemplate>()
            .Where(item => item.Id == id && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"显示模板 {id} 不存在。");
        ValidateInput(input, id);
        var entity = input.Adapt<PageDisplayTemplate>();
        entity.Id = id;
        entity.TenantId = existing.TenantId;
        entity.CreateTime = existing.CreateTime;
        entity.CreateBy = existing.CreateBy;
        entity.CreateUserId = existing.CreateUserId;
        entity.UpdateTime = DateTime.UtcNow;
        entity.UpdateBy = "CodeMaster";
        entity.UpdateUserId = 0;
        return await _db.Updateable(entity)
            .IgnoreColumns(item => new { item.IsDeleted, item.DeleteTime, item.DeleteBy, item.DeleteUserId })
            .ExecuteCommandAsync();
    }

    [Permission("codegen:projectpage:delete")]
    public async Task<int> DeleteAsync(long id)
        => await _db.Updateable<PageDisplayTemplate>()
            .SetColumns(item => new PageDisplayTemplate { IsDeleted = true, DeleteTime = DateTime.UtcNow, DeleteBy = "CodeMaster" })
            .Where(item => item.Id == id && !item.IsDeleted)
            .ExecuteCommandAsync();

    private void ValidateInput(CreatePageDisplayTemplateDto input, long? id)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        var key = input.TemplateKey?.Trim() ?? string.Empty;
        if (!KeyPattern.IsMatch(key))
            throw new ArgumentException("TemplateKey 必须以字母开头，只能包含字母、数字、下划线或短横线。", nameof(input));
        if (string.IsNullOrWhiteSpace(input.TemplateName))
            throw new ArgumentException("TemplateName 不能为空。", nameof(input));
        if (!Enum.IsDefined(input.Scope))
            throw new ArgumentException("Scope 无效。", nameof(input));
        PageDisplayTemplateContract.Validate(input.TemplateContent, input.Scope);
        var duplicate = _db.Queryable<PageDisplayTemplate>()
            .Where(item => item.TemplateKey == key && !item.IsDeleted);
        if (id.HasValue) duplicate = duplicate.Where(item => item.Id != id.Value);
        if (duplicate.Any())
            throw new ArgumentException($"TemplateKey '{key}' 已存在。", nameof(input));
        input.TemplateKey = key;
        input.TemplateName = input.TemplateName.Trim();
        input.TargetType = string.IsNullOrWhiteSpace(input.TargetType) ? null : input.TargetType.Trim();
    }

    private static void ApplyAudit(PageDisplayTemplate entity, bool created)
    {
        entity.CreateBy = "CodeMaster";
        entity.CreateUserId = 0;
        entity.UpdateBy = "CodeMaster";
        entity.UpdateUserId = 0;
        entity.CreateTime = DateTime.UtcNow;
        entity.UpdateTime = created ? null : DateTime.UtcNow;
    }
}
