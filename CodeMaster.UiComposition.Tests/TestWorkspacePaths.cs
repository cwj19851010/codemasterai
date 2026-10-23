/// <summary>
/// 测试临时目录。按解决方案文件定位仓库根，而不是数 <c>..</c> 的层数——
/// 项目可能嵌在 CodeMaster.Private/ 下（5 层）也可能在仓库根（4 层），
/// 数层数会让临时库落到仓库外面。
/// </summary>
internal static class CodeMasterTestPaths
{
    public static string Root { get; } = CreateRoot();

    private static string CreateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeMaster.sln")))
            directory = directory.Parent;

        var repositoryRoot = directory?.FullName
            ?? throw new InvalidOperationException("找不到包含 CodeMaster.sln 的仓库根目录。");

        var root = Path.Combine(repositoryRoot, ".artifacts", "test-temp", "CodeMaster.UiComposition.Tests");
        Directory.CreateDirectory(root);
        return root;
    }
}
