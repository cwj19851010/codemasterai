internal static class CodeMasterTestPaths
{
    public static string Root { get; } = CreateRoot();

    private static string CreateRoot()
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            ".artifacts", "test-temp", "CodeMaster.Agent.Tests"));
        Directory.CreateDirectory(root);
        return root;
    }
}
