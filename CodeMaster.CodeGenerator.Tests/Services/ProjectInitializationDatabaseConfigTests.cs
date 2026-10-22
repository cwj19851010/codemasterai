using CodeMaster.Application.Services.CodeGen;
using Microsoft.Extensions.Configuration;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class ProjectInitializationDatabaseConfigTests
{
    [Fact]
    public async Task Step4_RewritesEnvironmentBasedViteProxyFallback()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"CodeMasterPorts-{Guid.NewGuid():N}");
        var projectName = "SampleProject";
        var vuePath = Path.Combine(root, $"{projectName}.Vue");
        try
        {
            Directory.CreateDirectory(vuePath);
            await File.WriteAllTextAsync(
                Path.Combine(root, ".codemaster-init-state.json"),
                "{\"PortConfigured\":false}");
            await File.WriteAllTextAsync(
                Path.Combine(vuePath, "vite.config.js"),
                "const apiProxyTarget = process.env.CODEMASTER_API_PROXY_TARGET || 'http://127.0.0.1:5000'\n" +
                "export default { server: { port: 5173, proxy: { '/api': { target: apiProxyTarget } } } }");

            var service = new ProjectInitializationService(new ConfigurationBuilder().Build());
            await service.Step4_UpdatePortConfigAsync(projectName, root, frontendPort: 5911, backendPort: 5910);

            var content = await File.ReadAllTextAsync(Path.Combine(vuePath, "vite.config.js"));
            Assert.Contains("http://127.0.0.1:5910", content);
            Assert.Contains("port: 5911", content);
            Assert.DoesNotContain("http://127.0.0.1:5000", content);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("<DbProvider>PostgreSQL</DbProvider>")]
    [InlineData("<DbProvider Condition=\"'$(DbProvider)'==''\">PostgreSQL</DbProvider>")]
    public async Task Step3_UpdatesFixedAndConditionalDbProvider(string dbProviderElement)
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"CodeMasterDbProvider-{Guid.NewGuid():N}");
        var projectName = "SampleProject";
        var webApiPath = Path.Combine(root, $"{projectName}.WebApi");
        var migratorPath = Path.Combine(root, $"{projectName}.Migrator");
        Directory.CreateDirectory(webApiPath);
        Directory.CreateDirectory(migratorPath);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, ".codemaster-init-state.json"),
                "{\"DatabaseConfigured\":false}");
            await File.WriteAllTextAsync(
                Path.Combine(webApiPath, "appsettings.json"),
                "{\"ConnectionStrings\":{\"DefaultConnection\":\"old\"},\"DbProvider\":\"PostgreSQL\"}");
            await File.WriteAllTextAsync(
                Path.Combine(webApiPath, "appsettings.Development.json"),
                "{\"ConnectionStrings\":{\"DefaultConnection\":\"old\"},\"DbProvider\":\"PostgreSQL\"}");
            await File.WriteAllTextAsync(
                Path.Combine(migratorPath, "appsettings.json"),
                "{\"ConnectionStrings\":{\"DefaultConnection\":\"old\"},\"DbProvider\":\"PostgreSQL\"}");
            await File.WriteAllTextAsync(
                Path.Combine(migratorPath, $"{projectName}.Migrator.csproj"),
                $"<Project><PropertyGroup>{dbProviderElement}</PropertyGroup></Project>");

            var service = new ProjectInitializationService(new ConfigurationBuilder().Build());
            await service.Step3_UpdateDatabaseConfigAsync(
                projectName,
                root,
                "MySQL",
                "Server=127.0.0.1;Database=SampleDb;User Id=test;Password=test");

            var csproj = await File.ReadAllTextAsync(
                Path.Combine(migratorPath, $"{projectName}.Migrator.csproj"));
            Assert.Contains("<DbProvider>MySql</DbProvider>", csproj);
            Assert.DoesNotContain("PostgreSQL", csproj);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
