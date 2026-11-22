using CodeMaster.Domain.Entities.System;
using CodeMaster.Migrator.Persistence.EfCore;
using Microsoft.EntityFrameworkCore;
using Yitter.IdGenerator;

namespace CodeMaster.Migrator.SeedData.CodeGen;

/// <summary>
/// 报表模板模块（生成项目自带报表运行时的管理入口）。
/// 报表模板在 CodeMaster 设计器中设计并下发到本项目数据库；运行期取数、渲染、
/// 导出全部由本项目完成，与 CodeMaster 无关。
/// 仅当打印迁移运行时（CM.Printing.Migrations）随项目下发时才种菜单，
/// 未启用打印能力的项目不会出现指向空页面的菜单。
/// </summary>
public class PrintTemplateModule : ISeedModule
{
    private const string ListPath = "printTemplate";
    private const string ListComponent = "codegen/printTemplate/index";
    private const string DetailComponent = "codegen/printTemplate/detail";
    private const string PrintRuntimeAssembly = "CM.Printing.Migrations";
    private const int ReportsDirectoryOrder = 9999;

    public string ModuleName => "报表管理-报表模板";

    public Task<bool> HasMenuAsync(CodeMasterDbContext dbContext)
        => dbContext.Set<SysMenu>().AnyAsync(menu => menu.Path == ListPath && menu.Component == ListComponent);

    public async Task AddMenusAsync(CodeMasterDbContext dbContext, long adminRoleId)
    {
        if (!IsPrintRuntimePresent())
        {
            Console.WriteLine("  - 未检测到打印运行时插件，跳过报表模板菜单");
            return;
        }

        var parentMenu = await FindPreferredParentAsync(dbContext);
        var parentId = parentMenu?.Id;
        if (!parentId.HasValue)
        {
            parentId = await MenuBuilder.CreateDirectoryAsync(
                dbContext,
                menuName: "报表管理",
                titleKey: "reports",
                path: "/reports",
                icon: "DataAnalysis",
                orderNum: ReportsDirectoryOrder);
            await MenuBuilder.AssignMenusToRoleAsync(dbContext, adminRoleId, new List<long> { parentId.Value });
            Console.WriteLine("  - 创建报表管理目录");
        }
        else
        {
            NormalizeReportsDirectory(parentMenu!);
            await MenuBuilder.AssignMenusToRoleAsync(dbContext, adminRoleId, new List<long> { parentMenu!.Id });
        }

        var listMenu = new SysMenu
        {
            Id = YitIdHelper.NextId(),
            ParentId = parentId.Value,
            MenuName = "报表模板",
            TitleKey = "printtemplate",
            Path = ListPath,
            Component = ListComponent,
            MenuType = "C",
            Visible = true,
            Status = 0,
            IsCache = true,
            Icon = "Printer",
            OrderNum = 6,
            Perms = "print:template:list",
            MenuScope = 2,
            CreateTime = DateTime.UtcNow
        };
        await dbContext.Set<SysMenu>().AddAsync(listMenu);

        var detailMenu = new SysMenu
        {
            Id = YitIdHelper.NextId(),
            ParentId = listMenu.Id,
            MenuName = "报表模板详情",
            TitleKey = "printtemplatedetail",
            Path = "detail",
            Component = DetailComponent,
            MenuType = "C",
            Visible = false,
            Status = 0,
            IsCache = false,
            Perms = "print:template:view",
            MenuScope = 2,
            CreateTime = DateTime.UtcNow
        };
        await dbContext.Set<SysMenu>().AddAsync(detailMenu);

        var exportButton = new SysMenu
        {
            Id = YitIdHelper.NextId(),
            ParentId = listMenu.Id,
            MenuName = "报表导出",
            TitleKey = "printtemplateexport",
            Path = string.Empty,
            Component = string.Empty,
            MenuType = "F",
            Visible = false,
            Status = 0,
            Perms = "print:template:export",
            MenuScope = 2,
            CreateTime = DateTime.UtcNow
        };
        await dbContext.Set<SysMenu>().AddAsync(exportButton);

        await MenuBuilder.AssignMenusToRoleAsync(
            dbContext,
            adminRoleId,
            new List<long> { listMenu.Id, detailMenu.Id, exportButton.Id });

        Console.WriteLine("  - 添加报表模板菜单（3个）");
    }

    public async Task AddInitialDataAsync(CodeMasterDbContext dbContext)
    {
        if (!IsPrintRuntimePresent())
            return;

        var listMenu = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(menu => menu.Path == ListPath && menu.Component == ListComponent);
        if (listMenu == null)
            return;

        var preferredParent = await FindPreferredParentAsync(dbContext);
        if (preferredParent != null)
            NormalizeReportsDirectory(preferredParent);
        if (preferredParent != null && listMenu.ParentId != preferredParent.Id)
        {
            listMenu.ParentId = preferredParent.Id;
            Console.WriteLine($"  - 报表模板菜单已归并到 {preferredParent.MenuName}");
        }

        var generatedParent = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(menu => menu.Path == "/reports" && menu.MenuType == "M");
        if (generatedParent == null || preferredParent?.Id == generatedParent.Id)
            return;

        var hasOtherChildren = await dbContext.Set<SysMenu>()
            .AnyAsync(menu => menu.ParentId == generatedParent.Id && menu.Id != listMenu.Id);
        if (hasOtherChildren)
            return;

        var roleLinks = await dbContext.Set<SysRoleMenu>()
            .Where(link => link.MenuId == generatedParent.Id)
            .ToListAsync();
        dbContext.Set<SysRoleMenu>().RemoveRange(roleLinks);
        dbContext.Set<SysMenu>().Remove(generatedParent);
        Console.WriteLine("  - 移除重复的报表管理目录");
    }

    public Dictionary<string, Dictionary<string, string>> GetTranslations() => new()
    {
        { "reports", new() { { "zh-CN", "报表管理" }, { "en-US", "Reports" } } },
        { "printtemplate", new() { { "zh-CN", "报表模板" }, { "en-US", "Report Templates" } } },
        { "printtemplatedetail", new() { { "zh-CN", "报表模板详情" }, { "en-US", "Report Template Detail" } } },
        { "printtemplateexport", new() { { "zh-CN", "报表导出" }, { "en-US", "Report Export" } } }
    };

    /// <summary>
    /// 打印运行时以模块 DLL 形式下发到生成项目；Migrator 启动时会扫描 modules 目录
    /// 加载模块程序集，据此判断本项目是否启用打印能力。
    /// </summary>
    private static bool IsPrintRuntimePresent()
        => RuntimeMigrationModuleLoader.HasModule(PrintRuntimeAssembly);

    private static async Task<SysMenu?> FindPreferredParentAsync(CodeMasterDbContext dbContext)
    {
        var directories = await dbContext.Set<SysMenu>()
            .Where(menu => menu.MenuType == "M" && menu.ParentId == null)
            .ToListAsync();
        return directories.FirstOrDefault(menu => menu.Path == "/reporting" || menu.TitleKey == "reporting")
               ?? directories.FirstOrDefault(menu => menu.Path == "/reports" || menu.TitleKey == "reports")
               ?? directories.FirstOrDefault(menu => menu.MenuName == "报表管理");
    }

    private static void NormalizeReportsDirectory(SysMenu menu)
    {
        menu.MenuName = "报表管理";
        menu.TitleKey = "reports";
        menu.Component = "Layout";
        menu.OrderNum = ReportsDirectoryOrder;
        menu.Visible = true;
        menu.Status = 0;
        menu.IsDeleted = false;
        menu.DeleteTime = null;
        menu.Icon = string.IsNullOrWhiteSpace(menu.Icon) ? "DataAnalysis" : menu.Icon;
        menu.UpdateTime = DateTime.UtcNow;
    }
}
