using CodeMaster.Application.Services.CodeGen;
using Microsoft.Extensions.Configuration;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class ProjectInitializationDatabaseConfigTests
{
    [Theory]
    [InlineData("<DbProvider>PostgreSQL</DbProvider>")]
    [InlineData("<DbProvider Condition=\"'$(DbProvider)'==''\">PostgreSQL</DbProvider>")]
    public async Task Step3_UpdatesFixedAndConditionalDbProvider(string dbProviderElement)
    {
        var root = Path.Combine(Path.GetTempPath(), $"CodeMasterDbProvider-{Guid.NewGuid():N}");
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
