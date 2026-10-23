using CodeMaster.Domain.Entities.CodeGen;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 项目级代码生成结束后，把受管视图定义同步到生成项目的
/// <c>GeneratedViewMigrationModule.cs</c>。
///
/// <para>在此之前只有视图定义的新增/部署/删除会触发导出。走"全量生成代码"这条主路径时
/// 视图迁移文件不会被刷新，生成项目的 Migrator 里仍是旧 SQL——新增的视图永远不会落到
/// 目标库。这里把它挂到生成后置钩子上，与复杂页面物化同一个时机。</para>
///
/// <para>视图是生成流程的附加产物：导出失败不能把一次成功的代码生成变成 500，因此
/// 异常一律吞掉并写 stderr。</para>
/// </summary>
public sealed class ViewProjectGenerationObserver : IProjectCodeGenerationObserver
{
    private readonly ISqlSugarClient _db;

    public ViewProjectGenerationObserver(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task ProjectGeneratedAsync(ProjectCodeGenerationContext context)
    {
        if (context.ProjectId <= 0)
            return;

        try
        {
            if (!_db.DbMaintenance.IsAnyTable("sys_project_view_definition", false))
                return;
            await GeneratedViewMigrationExporter.ExportForProjectAsync(_db, context.ProjectId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[GeneratedView] project {context.ProjectId} view migration export aborted: {ex.Message}");
        }
    }
}
