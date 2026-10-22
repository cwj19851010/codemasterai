using CodeMaster.Infrastructure.DynamicApi;

namespace CodeMaster.CodeGenerator.Tests.Services;

public sealed class GeneratedProjectModuleRuntimeTests
{
    [Theory]
    [InlineData("Templates/CodeMaster.WebApi/CodeMaster.WebApi.csproj", "CopyGeneratedModulesToWebApiOutput")]
    [InlineData("Templates/CodeMaster.Migrator/CodeMaster.Migrator.csproj", "CopyGeneratedModulesToMigratorOutput")]
    public void GeneratedProjectTemplates_CopySourceModulesToBuildOutput(string relativePath, string targetName)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains($"Name=\"{targetName}\"", source);
        Assert.Contains("..\\modules\\**\\*", source);
        Assert.Contains("$(TargetDir)modules", source);
        Assert.Contains("SkipPrintingWebApiCopy", source);
    }

    [Fact]
    public void WebApiModuleLoader_SkipsMigratorAssemblies()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-module-loader-{Guid.NewGuid():N}");
        var modules = Path.Combine(root, "modules");
        Directory.CreateDirectory(modules);

        try
        {
            var sourceAssembly = typeof(GeneratedProjectModuleRuntimeTests).Assembly.Location;
            File.Copy(sourceAssembly, Path.Combine(modules, "CodeMaster.Probe.Migrator.dll"));

            var loaded = CodeMasterModuleAssemblyLoader.LoadOptionalModules(root);

            Assert.Empty(loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GeneratedWebApiTemplate_AlignsPlaywrightAsyncInterfacesDependency()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Templates",
            "CodeMaster.WebApi",
            "CodeMaster.WebApi.csproj"));

        Assert.Contains("Microsoft.Bcl.AsyncInterfaces", source);
        Assert.Contains("Version=\"6.0.0\"", source);
    }

    [Fact]
    public void GeneratedProjectTemplates_UseStableIndependentRuntimeLoaders()
    {
        var root = FindRepositoryRoot();
        var webLoader = File.ReadAllText(Path.Combine(
            root,
            "Templates",
            "CodeMaster.Infrastructure",
            "DynamicApi",
            "RuntimeWebModuleLoader.cs"));
        var migrationLoader = File.ReadAllText(Path.Combine(
            root,
            "Templates",
            "CodeMaster.Migrator",
            "Persistence",
            "EfCore",
            "RuntimeMigrationModuleLoader.cs"));

        Assert.Contains("CM.*.Runtime.dll", webLoader);
        Assert.Contains("ConfigureServices", webLoader);
        Assert.Contains("MapEndpoints", webLoader);
        Assert.Contains("CM.*.Migrations.dll", migrationLoader);
        Assert.Contains("MigrateAsync", migrationLoader);
    }

    [Fact]
    public void GeneratedPrintMenu_UsesTheReservedTerminalReportsDirectory()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Templates",
            "CodeMaster.Migrator",
            "SeedData",
            "CodeGen",
            "PrintTemplateModule.cs"));

        Assert.Contains("menu.Path == \"/reports\"", source);
        Assert.Contains("menuName: \"报表管理\"", source);
        Assert.Contains("titleKey: \"reports\"", source);
        Assert.Contains("private const int ReportsDirectoryOrder = 9999", source);
        Assert.Contains("orderNum: ReportsDirectoryOrder", source);
        Assert.Contains("menu.OrderNum = ReportsDirectoryOrder", source);
        Assert.Contains("AssignMenusToRoleAsync", source);
        Assert.Contains("menu.Path == \"/reporting\"", source);
        Assert.Contains("移除重复的报表管理目录", source);
        Assert.DoesNotContain("directories.FirstOrDefault(menu => menu.Path == \"/codegen\")", source);
    }

    [Fact]
    public void GeneratedMigratorTemplate_DoesNotRegisterPlatformCodegenSeedModules()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Templates",
            "CodeMaster.Migrator",
            "Program.cs"));

        Assert.Contains("new PrintTemplateModule()", source);
        Assert.DoesNotContain("new ProjectModule()", source);
        Assert.DoesNotContain("new ProjectModuleModule()", source);
        Assert.DoesNotContain("new ModuleEntityModule()", source);
        Assert.DoesNotContain("Name = \"代码管理\"", source);
        Assert.DoesNotContain("Path = \"codegen\"", source);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeMaster.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate CodeMaster repository root.");
    }
}
