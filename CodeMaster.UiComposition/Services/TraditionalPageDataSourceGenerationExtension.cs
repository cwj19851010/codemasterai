using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Application.ScriptBuilder;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;
using SqlSugar;

namespace CodeMaster.UiComposition.Services;

/// <summary>
/// Applies an optional project data-source binding to legacy CRUD pages after
/// the normal template generator has produced its ScriptSection.  With no
/// binding this extension is a no-op, preserving the historical byte output.
/// </summary>
public sealed class TraditionalPageDataSourceGenerationExtension : ICodeGenerationPageExtension
{
    private readonly ISqlSugarClient _db;

    public TraditionalPageDataSourceGenerationExtension(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task ProcessPageAsync(CodeGenerationPageContext context)
    {
        if (!_db.DbMaintenance.IsAnyTable("sys_page_data_source_binding", false)) return;
        if (context.PageType.Equals("add", StringComparison.OrdinalIgnoreCase)) return;

        var bindings = await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.ModuleEntityId == context.EntityId &&
                           item.PageType == context.PageType &&
                           !item.IsDeleted)
            .OrderBy(item => item.Id)
            .ToListAsync();
        if (bindings.Count == 0) return;
        if (bindings.Count > 1)
            throw new InvalidOperationException(
                $"Traditional page {context.EntityId}/{context.PageType} has multiple data-source overrides. Keep exactly one active binding.");
        var binding = bindings[0];

        var source = await _db.Queryable<ProjectDataSource>()
            .ClearFilter()
            .Where(item => item.Id == binding.DataSourceId)
            .FirstAsync();
        if (source == null || source.IsDeleted)
            throw new InvalidOperationException($"传统页面数据源 {binding.DataSourceId} 不存在或已删除。");
        if (binding.BindingStatus != PageBindingStatus.Active)
            throw new InvalidOperationException($"传统页面数据源绑定状态为 {binding.BindingStatus}，请先完成同步确认。");
        if (!string.IsNullOrWhiteSpace(binding.LastKnownSchemaHash) &&
            !string.Equals(binding.LastKnownSchemaHash, source.SchemaHash, StringComparison.OrdinalIgnoreCase))
        {
            await _db.Updateable<PageDataSourceBinding>()
                .SetColumns(item => new PageDataSourceBinding
                {
                    BindingStatus = PageBindingStatus.NeedsReview,
                    UpdateTime = DateTime.UtcNow,
                    UpdateBy = "CodeMaster"
                })
                .Where(item => item.Id == binding.Id)
                .ExecuteCommandAsync();
            throw new InvalidOperationException("传统页面数据源 DTO 已变化，请确认字段映射后再生成。");
        }

        var entity = await _db.Queryable<ModuleEntity>()
            .Where(item => item.Id == context.EntityId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"实体 {context.EntityId} 不存在。");
        if (!source.EntityId.HasValue)
            throw new InvalidOperationException("传统页面覆盖数据源必须关联实体，以便生成前端 API 导入路径。");
        var sourceEntity = await _db.Queryable<ModuleEntity>()
            .Where(item => item.Id == source.EntityId.Value && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"数据源实体 {source.EntityId} 不存在。");
        var module = await _db.Queryable<ProjectModule>()
            .Where(item => item.Id == sourceEntity.ModuleId && !item.IsDeleted)
            .FirstAsync() ?? throw new InvalidOperationException($"数据源模块 {sourceEntity.ModuleId} 不存在。");

        var method = NormalizeMethod(source.MethodName);
        var customAlias = ToCamelCase(source.SourceKey) + "Api";
        var originalAlias = ToCamelCase(entity.Name) + "Api";
        var originalMethods = context.PageType.Equals("index", StringComparison.OrdinalIgnoreCase)
            ? new[] { "getPagedList", "getList" }
            : new[] { "getById", "getDetail" };
        var importPath = $"@/api/{CodeGenNaming.ToPathSegment(module.ModuleName)}/{CodeGenNaming.ToPathSegment(sourceEntity.Name)}";
        var replaced = false;

        var scriptOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (!string.IsNullOrWhiteSpace(context.Result.MainScriptJson))
        {
            var section = JsonSerializer.Deserialize<ScriptSection>(context.Result.MainScriptJson, scriptOptions)
                ?? throw new InvalidOperationException("传统页面 ScriptSection 无法解析。");
            foreach (var function in section.Functions)
            {
                foreach (var originalMethod in originalMethods)
                {
                    var originalCall = $"{originalAlias}.{originalMethod}";
                    var replacement = $"{customAlias}.{method}";
                    if (!function.Body.Contains(originalCall, StringComparison.Ordinal)) continue;
                    function.Body = function.Body.Replace(originalCall, replacement, StringComparison.Ordinal);
                    replaced = true;
                }
            }
            if (!section.Imports.Any(item => item.Mode == "namespace" &&
                                              item.Names == customAlias &&
                                              item.From == importPath))
            {
                section.Imports.Add(new ImportInfo { From = importPath, Mode = "namespace", Names = customAlias });
            }
            context.Result.MainScriptJson = JsonSerializer.Serialize(section, new JsonSerializerOptions { WriteIndented = false });
        }

        foreach (var originalMethod in originalMethods)
        {
            var originalCall = $"{originalAlias}.{originalMethod}";
            var replacement = $"{customAlias}.{method}";
            if (context.Result.MainScriptContent.Contains(originalCall, StringComparison.Ordinal))
            {
                context.Result.MainScriptContent = context.Result.MainScriptContent.Replace(originalCall, replacement, StringComparison.Ordinal);
                replaced = true;
            }
        }
        if (!context.Result.MainScriptContent.Contains($"import * as {customAlias} from '{importPath}';", StringComparison.Ordinal))
            context.Result.MainScriptContent = $"import * as {customAlias} from '{importPath}';\n" + context.Result.MainScriptContent;

        if (!replaced)
            throw new InvalidOperationException($"传统页面 {context.PageType} 未找到可覆盖的数据源调用。");
    }

    public async Task ContributeIncrementalChangesAsync(long entityId, DateTime since, ComponentTreeChangeSet changes)
    {
        if (!_db.DbMaintenance.IsAnyTable("sys_page_data_source_binding", false)) return;
        var bindings = await _db.Queryable<PageDataSourceBinding>()
            .Where(item => item.ModuleEntityId == entityId && !item.IsDeleted)
            .ToListAsync();
        foreach (var binding in bindings)
        {
            var source = await _db.Queryable<ProjectDataSource>()
                .ClearFilter()
                .Where(item => item.Id == binding.DataSourceId)
                .FirstAsync();
            if (source == null || source.IsDeleted)
            {
                changes.DeletedDataSourceIds.Add(binding.DataSourceId);
                continue;
            }

            if (HasChangedSince(source.CreateTime, source.UpdateTime, since) ||
                source.LastSyncedTime.HasValue && source.LastSyncedTime.Value > since ||
                binding.BindingStatus == PageBindingStatus.NeedsReview)
                changes.ChangedDataSourceIds.Add(source.Id);
        }
    }

    private static string NormalizeMethod(string methodName)
    {
        var method = methodName.EndsWith("Async", StringComparison.OrdinalIgnoreCase)
            ? methodName[..^5]
            : methodName;
        if (method.Equals("GetPageList", StringComparison.OrdinalIgnoreCase)) method = "GetPagedList";
        if (method.Length == 0) throw new ArgumentException("数据源方法不能为空。");
        return char.ToLowerInvariant(method[0]) + method[1..];
    }

    private static string ToCamelCase(string value)
    {
        var clean = Regex.Replace(value, "[^A-Za-z0-9]+", " ").Trim();
        if (clean.Length == 0) return "dataSource";
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        return char.ToLowerInvariant(result[0]) + result[1..];
    }

    private static bool HasChangedSince(DateTime createTime, DateTime? updateTime, DateTime since)
        => createTime > since || updateTime.HasValue && updateTime.Value > since;
}
