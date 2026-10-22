using System.IO.Compression;
using System.Text;
using CodeMaster.Application.Services.CodeGen;
using Microsoft.Extensions.Configuration;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class ProjectInitializationTemplateExtractionTests
{
    [Fact]
    public async Task ExportedTemplate_InitializesAsIndependentCleanProjectWithPrinting()
    {
        var repositoryRoot = FindRepositoryRoot();
        var root = Path.Combine(CodeMasterTestPaths.Root, $"CodeMasterCleanProject-{Guid.NewGuid():N}");
        var output = Path.Combine(CodeMasterTestPaths.Root, $"CodeMasterCleanTemplate-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(
            repositoryRoot,
            "CodeMaster.Private",
            "Printing.Migrations",
            "bin",
            "Debug",
            "net10.0");
        var runtimeRoot2 = Path.Combine(
            repositoryRoot,
            "CodeMaster.Private",
            "Printing.Runtime",
            "bin",
            "Debug",
            "net10.0");

        try
        {
            var exporter = new TemplateExportService(repositoryRoot, [runtimeRoot, runtimeRoot2]);
            var zipPath = await exporter.ExportCleanTemplateAsync(output);
            var initializer = new ProjectInitializationService(new ConfigurationBuilder().Build());
            await initializer.Step1_ExtractTemplateAsync("CleanProbe", root, zipPath);

            Assert.True(File.Exists(Path.Combine(root, "CleanProbe.sln")));
            Assert.True(File.Exists(Path.Combine(root, "modules", "CM.Printing.Runtime.dll")));
            Assert.True(File.Exists(Path.Combine(root, "modules", "CM.Printing.Migrations.dll")));
            Assert.True(File.Exists(Path.Combine(root, "CleanProbe.Vue", "src", "views", "codegen", "print", "preview.vue")));
            Assert.True(File.Exists(Path.Combine(root, "CleanProbe.Vue", "src", "views", "codegen", "printTemplate", "index.vue")));
            Assert.False(Directory.Exists(Path.Combine(root, "CleanProbe.CodeGenerator")));

            var readme = await File.ReadAllTextAsync(Path.Combine(root, "README.md"));
            var packageJson = await File.ReadAllTextAsync(Path.Combine(root, "CleanProbe.Vue", "package.json"));
            var dashboard = await File.ReadAllTextAsync(Path.Combine(root, "CleanProbe.Vue", "src", "views", "dashboard", "index.vue"));
            var migratorProgram = await File.ReadAllTextAsync(Path.Combine(root, "CleanProbe.Migrator", "Program.cs"));
            Assert.DoesNotContain("MCP", readme, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Tauri", readme, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@tauri-apps", packageJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sidecar:publish", packageJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/codegen/project", dashboard, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Name = \"代码管理\"", migratorProgram);

            var forbiddenPaths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.agents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.codemaster{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => path.Contains("ProjectDataSource", StringComparison.OrdinalIgnoreCase) ||
                               path.Contains($"{Path.DirectorySeparatorChar}codegen{Path.DirectorySeparatorChar}project", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(forbiddenPaths);

            var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".csproj", ".json", ".js", ".vue", ".html", ".css", ".scss",
                ".xml", ".config", ".txt", ".md", ".sln", ".props", ".targets", ".ps1"
            };
            var residualBranding = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => textExtensions.Contains(Path.GetExtension(path)))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.agents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.codemaster{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => File.ReadAllText(path).Contains("CodeMaster", StringComparison.OrdinalIgnoreCase))
                .Select(path => Path.GetRelativePath(root, path))
                .ToList();
            Assert.Empty(residualBranding);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task Step1_PreservesBinaryEntriesAndReplacesTextEntries()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"CodeMasterTemplateExtract-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(CodeMasterTestPaths.Root, $"CodeMasterTemplate-{Guid.NewGuid():N}.zip");
        var binaryBytes = new byte[]
        {
            0x4D, 0x5A, 0x00, 0xFF, 0xFE, 0x80, 0x43, 0x6F,
            0x64, 0x65, 0x4D, 0x61, 0x73, 0x74, 0x65, 0x72
        };

        try
        {
            await using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var solutionEntry = archive.CreateEntry("CodeMaster.sln");
                await using (var writer = new StreamWriter(solutionEntry.Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync("CodeMaster solution");

                var sourceEntry = archive.CreateEntry("CodeMaster.Core/Sample.cs");
                await using (var writer = new StreamWriter(sourceEntry.Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync("namespace CodeMaster.Core;");

                var binaryEntry = archive.CreateEntry("modules/CM.Printing.Migrations.dll");
                await using (var binaryStream = binaryEntry.Open())
                    await binaryStream.WriteAsync(binaryBytes);

                var moduleMetadataEntry = archive.CreateEntry("modules/CM.Printing.Migrations.deps.json");
                await using (var writer = new StreamWriter(moduleMetadataEntry.Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync("{\"owner\":\"CodeMaster\"}");
            }

            var service = new ProjectInitializationService(new ConfigurationBuilder().Build());
            await service.Step1_ExtractTemplateAsync("SampleProject", root, zipPath);

            Assert.Equal(
                binaryBytes,
                await File.ReadAllBytesAsync(Path.Combine(root, "modules", "CM.Printing.Migrations.dll")));
            Assert.Equal(
                "{\"owner\":\"CodeMaster\"}",
                await File.ReadAllTextAsync(Path.Combine(root, "modules", "CM.Printing.Migrations.deps.json")));
            Assert.Equal(
                "namespace SampleProject.Core;",
                await File.ReadAllTextAsync(Path.Combine(root, "SampleProject.Core", "Sample.cs")));
            Assert.True(File.Exists(Path.Combine(root, "SampleProject.sln")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (File.Exists(zipPath))
                File.Delete(zipPath);
        }
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
