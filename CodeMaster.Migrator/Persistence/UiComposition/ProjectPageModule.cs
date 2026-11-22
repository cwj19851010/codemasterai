using CodeMaster.Domain.Entities.System;
using CodeMaster.Migrator.Persistence.EfCore;
using CodeMaster.Migrator.SeedData;
using Microsoft.EntityFrameworkCore;
using Yitter.IdGenerator;

namespace CodeMaster.Migrator.SeedData.CodeGen;

/// <summary>
/// 注册复杂页面能力，并维护私有数据源管理菜单。
/// 该模块每次迁移都会执行幂等修正，因此既适用于新数据库，也适用于已经存在实体菜单的数据库。
/// </summary>
public sealed class ProjectPageModule : ISeedModule
{
    public string ModuleName => "复杂页面与数据源管理";

    public Task<bool> HasMenuAsync(CodeMasterDbContext dbContext)
        => dbContext.Set<SysMenu>().AnyAsync(item =>
            item.Path == "projectPage" && item.Component == "codegen/projectPage/index" && item.MenuType == "C");

    public Task AddMenusAsync(CodeMasterDbContext dbContext, long adminRoleId)
        => EnsureMenusAsync(dbContext, adminRoleId);

    public async Task AddInitialDataAsync(CodeMasterDbContext dbContext)
    {
        var adminRoleId = await dbContext.Set<SysRole>()
            .Where(role => role.RoleKey == "admin")
            .Select(role => role.Id)
            .FirstOrDefaultAsync();
        if (adminRoleId != 0)
            await EnsureMenusAsync(dbContext, adminRoleId);
    }

    private static async Task EnsureMenusAsync(CodeMasterDbContext dbContext, long adminRoleId)
    {
        var codegenRoot = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(item => item.Path == "codegen" && item.MenuType == "M");
        if (codegenRoot == null)
            return;

        // 实体 / 视图 / API 从 2026-11 起并入"代码管理"，
        // 不再单独建立"数据源管理"顶层目录。
        codegenRoot.OrderNum = Math.Max(codegenRoot.OrderNum, 4);

        var assignedMenuIds = new List<long>();

        var entityMenuIds = await EnsureEntityMenusAsync(dbContext, codegenRoot.Id);
        assignedMenuIds.AddRange(entityMenuIds);

        var viewMenu = await EnsurePageMenuAsync(
            dbContext,
            parentId: codegenRoot.Id,
            path: "dataSourceView",
            component: "codegen/projectPage/dataSource/view/index",
            menuName: "视图",
            titleKey: "data_source_view",
            icon: "View",
            orderNum: 4,
            permission: "codegen:projectpage:list");
        assignedMenuIds.Add(viewMenu.Id);

        var apiMenu = await EnsurePageMenuAsync(
            dbContext,
            parentId: codegenRoot.Id,
            path: "dataSourceApi",
            component: "codegen/projectPage/dataSource/api/index",
            menuName: "API",
            titleKey: "data_source_api",
            icon: "Connection",
            orderNum: 5,
            permission: "codegen:projectpage:list");
        assignedMenuIds.Add(apiMenu.Id);

        if (codegenRoot != null)
        {
            var projectPageMenuIds = await EnsureProjectPageMenusAsync(dbContext, codegenRoot.Id);
            assignedMenuIds.AddRange(projectPageMenuIds);
        }

        // 历史库里的"数据源管理"顶层目录：子菜单已迁到代码管理，目录空了就删掉
        var legacyDirectory = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(item => item.Path == "dataSource" && item.MenuType == "M");
        if (legacyDirectory != null)
        {
            var stillHasChildren = await dbContext.Set<SysMenu>()
                .AnyAsync(item => item.ParentId == legacyDirectory.Id);
            if (!stillHasChildren)
                dbContext.Set<SysMenu>().Remove(legacyDirectory);
        }

        await MenuBuilder.AssignMenusToRoleAsync(dbContext, adminRoleId, assignedMenuIds.Distinct().ToList());

        // 上面的菜单新增、改父级、以及遗留目录的删除都需要落库
        await dbContext.SaveChangesAsync();
    }

    private static async Task<List<long>> EnsureEntityMenusAsync(CodeMasterDbContext dbContext, long parentId)
    {
        var listMenu = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(item => item.Path == "moduleEntity" && item.Component == "codegen/moduleEntity/index");
        if (listMenu == null)
        {
            listMenu = new SysMenu { Id = YitIdHelper.NextId(), CreateTime = DateTime.UtcNow };
            await dbContext.Set<SysMenu>().AddAsync(listMenu);
        }

        ConfigurePageMenu(
            listMenu,
            parentId,
            "实体",
            "entity",
            "moduleEntity",
            "codegen/moduleEntity/index",
            "Document",
            3,
            "codegen:moduleEntity:list");

        var ids = new List<long> { listMenu.Id };
        var subPages = new[]
        {
            new { Path = "add", Name = "新增实体", Permission = "codegen:moduleEntity:create", Cache = true },
            new { Path = "edit", Name = "编辑实体", Permission = "codegen:moduleEntity:update", Cache = false },
            new { Path = "detail", Name = "实体详情", Permission = "codegen:moduleEntity:view", Cache = false }
        };

        foreach (var page in subPages)
        {
            var component = $"codegen/moduleEntity/{page.Path}";
            var menu = await dbContext.Set<SysMenu>()
                .FirstOrDefaultAsync(item => item.Component == component && item.Perms == page.Permission);
            if (menu == null)
            {
                menu = new SysMenu { Id = YitIdHelper.NextId(), CreateTime = DateTime.UtcNow };
                await dbContext.Set<SysMenu>().AddAsync(menu);
            }

            // 实体列表已经移动到 /dataSource/moduleEntity，但公共实体页面仍使用
            // /codegen/moduleentity/add|edit|detail。保留顶级隐藏兼容路由，
            // 避免把实体重新显示在“代码管理”目录下。
            menu.ParentId = null;
            menu.MenuName = page.Name;
            menu.Path = $"codegen/moduleentity/{page.Path}";
            menu.Component = component;
            menu.MenuType = "C";
            menu.Visible = false;
            menu.Status = 0;
            menu.IsCache = page.Cache;
            menu.Perms = page.Permission;
            menu.MenuScope = 2;
            ids.Add(menu.Id);
        }

        return ids;
    }

    private static async Task<SysMenu> EnsurePageMenuAsync(
        CodeMasterDbContext dbContext,
        long parentId,
        string path,
        string component,
        string menuName,
        string titleKey,
        string icon,
        int orderNum,
        string permission)
    {
        var menu = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(item => item.Path == path && item.Component == component);
        if (menu == null)
        {
            menu = new SysMenu { Id = YitIdHelper.NextId(), CreateTime = DateTime.UtcNow };
            await dbContext.Set<SysMenu>().AddAsync(menu);
        }

        ConfigurePageMenu(menu, parentId, menuName, titleKey, path, component, icon, orderNum, permission);
        return menu;
    }

    private static void ConfigurePageMenu(
        SysMenu menu,
        long parentId,
        string menuName,
        string titleKey,
        string path,
        string component,
        string icon,
        int orderNum,
        string permission)
    {
        menu.ParentId = parentId;
        menu.MenuName = menuName;
        menu.TitleKey = titleKey;
        menu.Path = path;
        menu.Component = component;
        menu.MenuType = "C";
        menu.Visible = true;
        menu.Status = 0;
        menu.IsCache = true;
        menu.Icon = icon;
        menu.OrderNum = orderNum;
        menu.Perms = permission;
        menu.MenuScope = 2;
    }

    private static async Task<List<long>> EnsureProjectPageMenusAsync(CodeMasterDbContext dbContext, long codegenRootId)
    {
        var menu = await dbContext.Set<SysMenu>()
            .FirstOrDefaultAsync(item => item.Path == "projectPage" && item.Component == "codegen/projectPage/index");
        if (menu == null)
        {
            menu = new SysMenu { Id = YitIdHelper.NextId(), CreateTime = DateTime.UtcNow };
            await dbContext.Set<SysMenu>().AddAsync(menu);
        }

        ConfigurePageMenu(
            menu,
            codegenRootId,
            "复杂页面",
            "complex_page",
            "projectPage",
            "codegen/projectPage/index",
            "Grid",
            6,
            "codegen:projectpage:list");

        var menuIds = new List<long> { menu.Id };
        var actions = new[]
        {
            new { Name = "查看复杂页面", Permission = "codegen:projectpage:view", Order = 10 },
            new { Name = "创建复杂页面", Permission = "codegen:projectpage:create", Order = 11 },
            new { Name = "更新与同步复杂页面", Permission = "codegen:projectpage:update", Order = 12 },
            new { Name = "删除复杂页面", Permission = "codegen:projectpage:delete", Order = 13 },
            new { Name = "生成复杂页面", Permission = "codegen:projectpage:generate", Order = 14 }
        };

        foreach (var action in actions)
        {
            var permissionMenu = await dbContext.Set<SysMenu>()
                .FirstOrDefaultAsync(item => item.Perms == action.Permission);
            if (permissionMenu == null)
            {
                permissionMenu = new SysMenu { Id = YitIdHelper.NextId(), CreateTime = DateTime.UtcNow };
                await dbContext.Set<SysMenu>().AddAsync(permissionMenu);
            }

            permissionMenu.ParentId = menu.Id;
            permissionMenu.MenuName = action.Name;
            permissionMenu.MenuType = "F";
            permissionMenu.Visible = false;
            permissionMenu.Status = 0;
            permissionMenu.IsCache = false;
            permissionMenu.OrderNum = action.Order;
            permissionMenu.Perms = action.Permission;
            permissionMenu.MenuScope = 2;
            menuIds.Add(permissionMenu.Id);
        }

        return menuIds;
    }

    public Dictionary<string, Dictionary<string, string>> GetTranslations() => new()
    {
        ["data_source_management"] = new() { ["zh-CN"] = "数据源管理", ["en-US"] = "Data Sources" },
        ["data_source_view"] = new() { ["zh-CN"] = "视图", ["en-US"] = "Views" },
        ["data_source_api"] = new() { ["zh-CN"] = "API", ["en-US"] = "APIs" },
        ["complex_page"] = new() { ["zh-CN"] = "复杂页面", ["en-US"] = "Complex Pages" }
    };
}
