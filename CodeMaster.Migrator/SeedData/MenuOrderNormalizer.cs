using CodeMaster.Domain.Entities.System;
using CodeMaster.Migrator.Persistence.EfCore;
using Microsoft.EntityFrameworkCore;

namespace CodeMaster.Migrator.SeedData;

/// <summary>
/// "代码管理"目录下各菜单的展示顺序。
/// 各模块的菜单只在"不存在"时创建，历史库的顺序不会随代码更新，
/// 这里按 path 统一校正一次，幂等，新库老库都适用。
/// </summary>
public static class MenuOrderNormalizer
{
    private static readonly Dictionary<string, int> CodeGenMenuOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project"] = 1,
        ["projectModule"] = 2,
        ["moduleEntity"] = 3,
        ["dataSourceView"] = 4,
        ["dataSourceApi"] = 5,
        ["projectPage"] = 6,
        ["templateConfig"] = 7,
        ["component"] = 8,
        ["entityDesigner"] = 9
    };

    public static async Task NormalizeAsync(CodeMasterDbContext dbContext)
    {
        var paths = CodeGenMenuOrder.Keys.ToArray();
        var menus = await dbContext.Set<SysMenu>()
            .Where(menu => menu.Path != null && paths.Contains(menu.Path))
            .ToListAsync();

        var changed = 0;
        foreach (var menu in menus)
        {
            if (menu.Path is null || !CodeGenMenuOrder.TryGetValue(menu.Path, out var order))
                continue;
            if (menu.OrderNum == order)
                continue;

            menu.OrderNum = order;
            changed++;
        }

        if (changed == 0)
            return;

        await dbContext.SaveChangesAsync();
        Console.WriteLine($"  - 校正代码管理菜单顺序: {changed} 项");
    }
}
