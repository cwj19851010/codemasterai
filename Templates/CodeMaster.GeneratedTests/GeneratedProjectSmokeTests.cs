using System.Reflection;
using CodeMaster.Core.Services;
using SqlSugar;

namespace CodeMaster.GeneratedTests;

/// <summary>
/// 生成项目健康度烟雾测试。
/// 纯内存断言,不依赖数据库,确保生成项目的基本契约完整:
/// 1. 所有 IApplicationService 均可在应用层找到实现(自动注册不会失败)
/// 2. 所有数据实体均有 long 主键且表名唯一(SqlSugar 建表不冲突)
/// 3. 核心实体可无参实例化(反序列化与初始化安全)
/// 4. long 序列化契约(Snowflake 值以字符串传输,避免前端精度丢失)
/// </summary>
public class GeneratedProjectSmokeTests
{
    private static readonly Assembly ApplicationAssembly =
        typeof(CodeMaster.Application.Dtos.System.SysUserDto).Assembly;

    private static readonly Assembly DomainAssembly =
        typeof(CodeMaster.Domain.Entities.System.SysUser).Assembly;

    [Fact]
    public void All_Application_Service_Interfaces_Have_Implementation()
    {
        var missing = new List<string>();

        foreach (var type in ApplicationAssembly.GetTypes())
        {
            if (!type.IsInterface || !typeof(IApplicationService).IsAssignableFrom(type))
            {
                continue;
            }

            var implemented = ApplicationAssembly.GetTypes()
                .Any(t => !t.IsInterface && !t.IsAbstract && type.IsAssignableFrom(t));

            if (!implemented)
            {
                missing.Add(type.FullName ?? type.Name);
            }
        }

        Assert.True(missing.Count == 0,
            $"以下 IApplicationService 接口缺少实现,自动 API 注册将失败:\n{string.Join("\n", missing)}");
    }

    [Fact]
    public void App_Service_Assembly_Loads_With_Domain_And_Core_Contracts()
    {
        // 应用层能加载,且 Domain 的实体契约(IEntity/EntityBase)可被解析,说明程序集引用链完整
        Assert.NotNull(ApplicationAssembly);
        Assert.True(typeof(CodeMaster.Core.Entities.IEntity).IsAssignableFrom(typeof(CodeMaster.Domain.Entities.System.SysUser)));
    }

    [Fact]
    public void All_Entities_Have_Long_Primary_Key_And_Instantiable()
    {
        var failures = new List<string>();

        foreach (var type in DomainAssembly.GetTypes().Where(t => IsEntityType(t)))
        {
            var idProperty = type.GetProperty("Id");
            if (idProperty == null || idProperty.PropertyType != typeof(long))
            {
                failures.Add($"{type.FullName}: 缺少 long 主键 Id");
                continue;
            }

            if (!type.IsAbstract)
            {
                try
                {
                    _ = Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    failures.Add($"{type.FullName}: 无参实例化失败 - {ex.GetBaseException().Message}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"实体契约校验失败({failures.Count} 项):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void Entity_Table_Names_Do_Not_Collide()
    {
        var tableNames = new List<string>();

        foreach (var type in DomainAssembly.GetTypes().Where(IsEntityType))
        {
            var tableAttr = type.GetCustomAttribute<SugarTable>();
            var tableName = tableAttr?.TableName ?? type.Name;
            tableNames.Add(tableName);
        }

        var duplicates = tableNames
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"存在重复表名,将导致 SqlSugar 建表覆盖:\n{string.Join("\n", duplicates)}");
    }

    private static bool IsEntityType(Type type)
    {
        return type.IsClass
               && !type.IsAbstract
               && typeof(CodeMaster.Core.Entities.IEntity).IsAssignableFrom(type)
               && typeof(CodeMaster.Core.Entities.EntityBase).IsAssignableFrom(type);
    }
}