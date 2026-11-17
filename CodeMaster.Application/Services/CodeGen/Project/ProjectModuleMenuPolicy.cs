namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 目录菜单（= 项目模块）在目标项目 sys_menu 里的身份约定。
/// </summary>
/// <remarks>
/// 目录菜单和 <see cref="ProjectModule"/> 是一对一的。
/// 模块改名会同时改掉 <c>Path</c> 和 <c>TitleKey</c>，只有模块 Id 不会变，
/// 所以把模块 Id 写成 remark 上的标记，作为改名后认回老目录的稳定键。
/// </remarks>
public static class ProjectModuleMenuPolicy
{
    public const string RemarkPrefix = "cm-module:";

    public static string BuildRemark(long moduleId) => $"{RemarkPrefix}{moduleId}";

    public static bool IsModuleMenu(string? remark, long moduleId)
        => string.Equals(remark, BuildRemark(moduleId), StringComparison.Ordinal);
}
