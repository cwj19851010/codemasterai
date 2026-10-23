using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Materializes the complex pages a project-level code generation leaves behind.
///
/// <para>The entity pass in <see cref="ModuleEntityService"/> writes CRUD pages only.
/// Complex pages are separate metadata rows, and the work plan deliberately does not
/// generate them per task ("Work-plan approval applies structured metadata only"),
/// so they sit on <see cref="PageSyncStatus.NeedsSync"/> until something else moves
/// them. Nothing did: a project whose plan created pages materialized with its
/// entity pages and none of the business screens the user asked for.</para>
///
/// <para>Each page is generated through the same service the per-page button uses, so
/// the file bundle, region protection and tree merge behave identically.</para>
/// </summary>
public sealed class ComplexPageProjectGenerationObserver : IProjectCodeGenerationObserver
{
    private readonly ISqlSugarClient _db;
    private readonly IComplexPageGenerationService _pageGeneration;

    public ComplexPageProjectGenerationObserver(
        ISqlSugarClient db,
        IComplexPageGenerationService pageGeneration)
    {
        _db = db;
        _pageGeneration = pageGeneration;
    }

    public async Task ProjectGeneratedAsync(ProjectCodeGenerationContext context)
    {
        if (context.ProjectId <= 0)
            return;

        // Entity generation is the contract of the call that reaches this hook; pages are a
        // value-add on top. A database hiccup while materializing a page must not turn a
        // successful project generation into a 500, so nothing here is allowed to escape.
        try
        {
            await MaterializeAsync(context);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ComplexPage] project {context.ProjectId} page materialization aborted: {ex.Message}");
        }
    }

    private async Task MaterializeAsync(ProjectCodeGenerationContext context)
    {
        if (!_db.DbMaintenance.IsAnyTable("sys_project_page", false))
            return;

        List<long> pageIds;
        try
        {
            pageIds = await _db.Queryable<ProjectPage>()
                .Where(page => page.ProjectId == context.ProjectId && !page.IsDeleted &&
                               page.SyncStatus != PageSyncStatus.Generated)
                .OrderBy(page => page.OrderNum)
                .OrderBy(page => page.Id)
                .Select(page => page.Id)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ComplexPage] cannot list pending pages of project {context.ProjectId}: {ex.Message}");
            return;
        }
        if (pageIds.Count == 0)
            return;

        foreach (var pageId in pageIds)
        {
            try
            {
                // GenerateAsync records its own failures on the page row and returns
                // Success=false, so one bad page cannot take the others down.
                var result = await _pageGeneration.GenerateAsync(pageId, context.Incremental);
                if (!result.Success)
                    Console.Error.WriteLine($"[ComplexPage] page {pageId} materialization failed: {result.Error}");
            }
            catch (Exception ex)
            {
                // GenerateAsync swallows, but a failure that happens while recording the
                // failure (MarkGenerationFailedAsync) surfaces here.
                Console.Error.WriteLine($"[ComplexPage] page {pageId} materialization failed: {ex.Message}");
                if (!_db.DbMaintenance.IsAnyTable("sys_project_page", false))
                    break;
                try
                {
                    await _db.Updateable<ProjectPage>()
                        .SetColumns(page => new ProjectPage
                        {
                            SyncStatus = PageSyncStatus.NeedsSync,
                            LastError = ex.Message
                        })
                        .Where(page => page.Id == pageId)
                        .ExecuteCommandAsync();
                }
                catch (Exception recordEx)
                {
                    Console.Error.WriteLine($"[ComplexPage] cannot record failure of page {pageId}: {recordEx.Message}");
                    break;
                }
            }
        }
    }
}
