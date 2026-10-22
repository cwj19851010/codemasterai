using System.IO.Compression;
using CodeMaster.Application.Services.CodeGen;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class TemplateExportServiceTests
{
    [Fact]
    public async Task CopyGeneratedProjectRuntimeModulesAsync_CopiesOnlyIndependentPrintingBundle()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-runtime-{Guid.NewGuid():N}");
        var bundle = Path.Combine(root, "bundle");
        var output = Path.Combine(root, "output");
        try
        {
            WriteRuntimeBundle(bundle);
            File.WriteAllText(Path.Combine(bundle, "CodeMaster.Application.dll"), "platform");
            File.WriteAllText(Path.Combine(bundle, "CodeMaster.Printing.dll"), "platform");

            var service = new TemplateExportService(FindRepositoryRoot(), [bundle]);
            var copied = await service.CopyGeneratedProjectRuntimeModulesAsync(output);

            Assert.True(copied >= 7);
            Assert.True(File.Exists(Path.Combine(output, "modules", "CM.Printing.Runtime.dll")));
            Assert.True(File.Exists(Path.Combine(output, "modules", "CM.Printing.Migrations.dll")));
            Assert.True(File.Exists(Path.Combine(output, "modules", "NPOI.OOXML.dll")));
            Assert.True(File.Exists(Path.Combine(output, "modules", "runtimes", "win-x64", "native", "libSkiaSharp.dll")));
            Assert.True(File.Exists(Path.Combine(output, "modules", ".playwright", "package", "cli.js")));
            Assert.False(File.Exists(Path.Combine(output, "modules", "CodeMaster.Application.dll")));
            Assert.False(File.Exists(Path.Combine(output, "modules", "CodeMaster.Printing.dll")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CopyGeneratedProjectRuntimeModulesAsync_RejectsIncompleteBundle()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-runtime-missing-{Guid.NewGuid():N}");
        var bundle = Path.Combine(root, "bundle");
        try
        {
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "CM.Printing.Runtime.dll"), "runtime");
            var service = new TemplateExportService(FindRepositoryRoot(), [bundle]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CopyGeneratedProjectRuntimeModulesAsync(Path.Combine(root, "output")));

            Assert.Contains("CM.Printing.Migrations.dll", error.Message);
            Assert.Contains("CODEMASTER_PRINTING_RUNTIME_PATH", error.Message);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportCleanTemplateAsync_PackagesPrintingRuntimeWithoutPlatformAssembliesOrHistoricalMigrations()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-zip-{Guid.NewGuid():N}");
        var bundle = Path.Combine(root, "bundle");
        var output = Path.Combine(root, "templates");
        try
        {
            WriteRuntimeBundle(bundle);
            File.WriteAllText(Path.Combine(bundle, "CodeMaster.Domain.dll"), "platform");
            var service = new TemplateExportService(FindRepositoryRoot(), [bundle]);

            var zipPath = await service.ExportCleanTemplateAsync(output);
            using var archive = ZipFile.OpenRead(zipPath);
            var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToList();

            Assert.Contains("modules/CM.Printing.Runtime.dll", entries);
            Assert.Contains("modules/CM.Printing.Migrations.dll", entries);
            Assert.Contains("modules/NPOI.OOXML.dll", entries);
            Assert.Contains("modules/runtimes/win-x64/native/libSkiaSharp.dll", entries);
            Assert.Contains("modules/.playwright/package/cli.js", entries);
            Assert.DoesNotContain(entries, entry => entry.EndsWith("CodeMaster.Domain.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries, entry =>
                entry.StartsWith("CodeMaster.CodeGenerator/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries, entry =>
                entry.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) &&
                entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

            var migratorProgram = ReadZipEntry(archive, "CodeMaster.Migrator/Program.cs");
            var userModule = ReadZipEntry(archive, "CodeMaster.Migrator/SeedData/System/UserModule.cs");
            var loginLogModule = ReadZipEntry(archive, "CodeMaster.Migrator/SeedData/Monitor/LoginLogModule.cs");
            var viteConfig = ReadZipEntry(archive, "CodeMaster.Vue/vite.config.js");
            var signalR = ReadZipEntry(archive, "CodeMaster.Vue/src/utils/signalr.js");
            var webSettings = ReadZipEntry(archive, "CodeMaster.WebApi/appsettings.json");
            var webDevelopmentSettings = ReadZipEntry(archive, "CodeMaster.WebApi/appsettings.Development.json");
            var migratorSettings = ReadZipEntry(archive, "CodeMaster.Migrator/appsettings.json");
            var packageJson = ReadZipEntry(archive, "CodeMaster.Vue/package.json");
            var readme = ReadZipEntry(archive, "README.md");
            Assert.Contains("Path = \"system\"", migratorProgram);
            Assert.Contains("Path = \"monitor\"", migratorProgram);
            Assert.DoesNotContain("Path = \"/system\"", migratorProgram);
            Assert.DoesNotContain("Path = \"/monitor\"", migratorProgram);
            Assert.Contains("m.Path == \"system\"", userModule);
            Assert.Contains("m.Path == \"monitor\"", loginLogModule);
            Assert.Contains("APP_API_PROXY_TARGET", viteConfig);
            Assert.Contains("'/hubs'", viteConfig);
            Assert.DoesNotContain("localhost:5000", viteConfig);
            Assert.Contains("return path", signalR);
            Assert.DoesNotContain("localhost:5000", signalR);
            Assert.Contains("your_mysql_password", webSettings);
            Assert.Contains("your_mysql_password", migratorSettings);
            Assert.DoesNotContain("101.37.24.154", webSettings);
            Assert.DoesNotContain("codemasterprivate", webSettings, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tauri.localhost", webDevelopmentSettings, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@tauri-apps", packageJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sidecar:publish", packageJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MCP", readme, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Tauri", readme, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("打印报表", readme);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveTauriBridgeLinesAsync_RemovesClientOnlyLayoutReferences()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-tauri-{Guid.NewGuid():N}");
        var mainPath = Path.Combine(root, "CodeMaster.Vue", "src", "main.js");
        var layoutPath = Path.Combine(root, "CodeMaster.Vue", "src", "layout", "index.vue");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(mainPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
            await File.WriteAllTextAsync(mainPath, "import { installTauriBridge } from '@/utils/tauriBridge'\napp.mount('#app')");
            await File.WriteAllTextAsync(layoutPath,
                """
                <update-checker v-if="isClient" class="header-item" />
                <theme-picker />
                import { isTauriRuntime } from '@/utils/tauriBridge'
                import UpdateChecker from '@/components/UpdateChecker.vue'
                const isClient = isTauriRuntime()
                """);

            var service = new TemplateExportService();
            await service.RemoveTauriBridgeLinesAsync(root);

            var main = await File.ReadAllTextAsync(mainPath);
            var layout = await File.ReadAllTextAsync(layoutPath);
            Assert.DoesNotContain("tauriBridge", main, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("app.mount", main);
            Assert.DoesNotContain("tauriBridge", layout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UpdateChecker", layout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("isClient", layout, StringComparison.Ordinal);
            Assert.Contains("theme-picker", layout);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TemplateManifest_UsesRepositoryIncludesAndExplicitCleanOverlays()
    {
        var configPath = Path.Combine(FindRepositoryRoot(), "template-config.json");
        var source = File.ReadAllText(configPath);

        Assert.Contains("\"sourceRoot\": \".\"", source);
        Assert.Contains("\"overlays\"", source);
        Assert.Contains("CodeMaster.Migrator/SeedData/CodeGen/PrintTemplateModule.cs", source);
        Assert.Contains("CodeMaster.Vue/src/views/codegen/print/preview.vue", source);
        Assert.Contains("CodeMaster.Vue/src/views/codegen/printTemplate/index.vue", source);
        Assert.Contains("CodeMaster.Vue/src/utils/printReport.js", source);
        Assert.Contains("CodeMaster.Vue/package-lock.json", source);
    }

    [Fact]
    public async Task RemoveCodeGenLinesAsync_RemovesMcpRegistrationsAndMiddleware()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var webApiDirectory = Path.Combine(root, "CodeMaster.WebApi");
        var programPath = Path.Combine(webApiDirectory, "Program.cs");

        try
        {
            Directory.CreateDirectory(webApiDirectory);
            await File.WriteAllLinesAsync(programPath,
            [
                "builder.Services.AddScoped<CodeMaster.Application.Services.Auth.IAuthService, CodeMaster.Application.Services.Auth.AuthService>();",
                "builder.Services.AddScoped<CodeMaster.Application.Services.Auth.IMcpTokenService, CodeMaster.Application.Services.Auth.McpTokenService>();",
                "// MCP tokens are validated before JWT authentication.",
                "app.UseMiddleware<CodeMaster.WebApi.Middleware.McpTokenAuthenticationMiddleware>();",
                "app.UseAuthentication();"
            ]);

            var service = new TemplateExportService();
            await service.RemoveCodeGenLinesAsync(root);

            var result = await File.ReadAllTextAsync(programPath);
            Assert.DoesNotContain("McpToken", result);
            Assert.Contains("IAuthService", result);
            Assert.Contains("app.UseAuthentication();", result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoveFrontendMcpReferencesAsync_RemovesRouteAndMenuItem()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var routerDirectory = Path.Combine(root, "CodeMaster.Vue", "src", "router");
        var layoutDirectory = Path.Combine(root, "CodeMaster.Vue", "src", "layout");
        var routerPath = Path.Combine(routerDirectory, "index.js");
        var layoutPath = Path.Combine(layoutDirectory, "index.vue");

        try
        {
            Directory.CreateDirectory(routerDirectory);
            Directory.CreateDirectory(layoutDirectory);
            await File.WriteAllLinesAsync(routerPath,
            [
                "children: [",
                "  {",
                "    path: 'dashboard',",
                "    name: 'Dashboard'",
                "  },",
                "  {",
                "    path: 'profile/mcp-token',",
                "    name: 'McpToken',",
                "    component: () => import('@/views/profile/mcpToken/index.vue'),",
                "    meta: { title: 'MCP Token', hidden: true }",
                "  },",
                "]"
            ]);
            await File.WriteAllLinesAsync(layoutPath,
            [
                "<el-dropdown-menu>",
                "  <el-dropdown-item @click=\"router.push('/profile/mcp-token')\">MCP Token</el-dropdown-item>",
                "  <el-dropdown-item divided @click=\"handleLogout\">Logout</el-dropdown-item>",
                "</el-dropdown-menu>"
            ]);

            var service = new TemplateExportService();
            await service.RemoveFrontendMcpReferencesAsync(root);

            var routerResult = await File.ReadAllTextAsync(routerPath);
            var layoutResult = await File.ReadAllTextAsync(layoutPath);
            Assert.DoesNotContain("mcp-token", routerResult, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mcpToken", routerResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Dashboard", routerResult);
            Assert.DoesNotContain("mcp-token", layoutResult, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<el-dropdown-item @click=\"handleLogout\">", layoutResult);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RemoveMissingSolutionProjects_RemovesProjectBlockAndConfigurationEntries()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        const string keptGuid = "{299C368A-8E3D-4061-A982-BB067CD786A0}";
        const string missingGuid = "{D989598D-39DC-4C4E-BDEA-9B40FBC1A4E7}";

        try
        {
            var coreDirectory = Path.Combine(root, "CodeMaster.Core");
            Directory.CreateDirectory(coreDirectory);
            File.WriteAllText(Path.Combine(coreDirectory, "CodeMaster.Core.csproj"), "<Project />");

            var solution = $$"""
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CodeMaster.Core", "CodeMaster.Core\CodeMaster.Core.csproj", "{{keptGuid}}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "CodeMaster.McpServer.Tests", "CodeMaster.McpServer.Tests\CodeMaster.McpServer.Tests.csproj", "{{missingGuid}}"
                EndProject
                Global
                    {{keptGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                    {{missingGuid}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
                EndGlobal
                """;

            var result = TemplateExportService.RemoveMissingSolutionProjects(solution, root);

            Assert.Contains("CodeMaster.Core", result);
            Assert.Contains(keptGuid, result);
            Assert.DoesNotContain("CodeMaster.McpServer.Tests", result);
            Assert.DoesNotContain(missingGuid, result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("import { modules } from '@view-modules'")]
    [InlineData("const modules = import.meta.glob('@/views/**/*.vue')")]
    [InlineData("const modules = import.meta.glob('/src/views/**/*.vue')")]
    public async Task RewriteFrontendViewModuleGlobAsync_UsesPortableRelativeGlob(string source)
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var routerDirectory = Path.Combine(root, "CodeMaster.Vue", "src", "router");
        var routerPath = Path.Combine(routerDirectory, "index.js");

        try
        {
            Directory.CreateDirectory(routerDirectory);
            await File.WriteAllTextAsync(
                routerPath,
                $$"""
                {{source}}
                let componentPath = `@/views/${view}.vue`
                if (!modules[`/src/views/${view}.vue`]) {
                  componentPath = `@/views/${view}/index.vue`
                }
                const matchedModule = modules[componentPath.replace('@', '/src')]
                """);

            var service = new TemplateExportService();
            await service.RewriteFrontendViewModuleGlobAsync(root);

            var result = await File.ReadAllTextAsync(routerPath);
            Assert.Contains("const modules = import.meta.glob('../views/**/*.vue')", result);
            Assert.DoesNotContain("@view-modules", result, StringComparison.Ordinal);
            Assert.DoesNotContain("import.meta.glob('@/views/", result, StringComparison.Ordinal);
            Assert.Contains("let componentPath = `../views/${view}.vue`", result);
            Assert.Contains("if (!modules[componentPath])", result);
            Assert.Contains("componentPath = `../views/${view}/index.vue`", result);
            Assert.Contains("const matchedModule = modules[componentPath]", result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoveAgentDbContextReferencesAsync_RemovesCodeMasterAgentEntities()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var dbContextDirectory = Path.Combine(root, "CodeMaster.Migrator", "Persistence", "EfCore");
        var dbContextPath = Path.Combine(dbContextDirectory, "CodeMasterDbContext.cs");

        try
        {
            Directory.CreateDirectory(dbContextDirectory);
            await File.WriteAllLinesAsync(dbContextPath,
            [
                "using Microsoft.EntityFrameworkCore;",
                "using CodeMaster.Domain.Entities.Ai;",
                "using CodeMaster.Domain.Entities.System;",
                "",
                "protected override void OnModelCreating(ModelBuilder modelBuilder)",
                "{",
                "    modelBuilder.Entity<AiMessage>()",
                "        .Property(x => x.RequestId)",
                "        .HasMaxLength(64);",
                "    modelBuilder.Entity<AiToolExecution>()",
                "        .HasIndex(x => x.RequestId)",
                "        .IsUnique();",
                "    modelBuilder.Entity<SysUser>();",
                "}"
            ]);

            var service = new TemplateExportService();
            await service.RemoveAgentDbContextReferencesAsync(root);

            var result = await File.ReadAllTextAsync(dbContextPath);
            Assert.DoesNotContain("Domain.Entities.Ai", result);
            Assert.DoesNotContain("Entity<AiMessage>", result);
            Assert.DoesNotContain("Entity<AiToolExecution>", result);
            Assert.Contains("Entity<SysUser>", result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoveCodeGenDbContextReferencesAsync_RemovesCodeMasterCodeGenEntities()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var dbContextDirectory = Path.Combine(root, "CodeMaster.Migrator", "Persistence", "EfCore");
        var dbContextPath = Path.Combine(dbContextDirectory, "CodeMasterDbContext.cs");

        try
        {
            Directory.CreateDirectory(dbContextDirectory);
            await File.WriteAllLinesAsync(dbContextPath,
            [
                "using Microsoft.EntityFrameworkCore;",
                "using CodeMaster.Domain.Entities.CodeGen;",
                "using CodeMaster.Domain.Entities.System;",
                "",
                "protected override void OnModelCreating(ModelBuilder modelBuilder)",
                "{",
                "    modelBuilder.Entity<ProjectModule>()",
                "        .HasIndex(x => new { x.ProjectId, x.ModuleName })",
                "        .IsUnique();",
                "    modelBuilder.Entity<ModuleEntity>()",
                "        .HasIndex(x => new { x.ProjectId, x.Name })",
                "        .IsUnique();",
                "    modelBuilder.Entity<SysUser>();",
                "}"
            ]);

            var service = new TemplateExportService();
            await service.RemoveCodeGenDbContextReferencesAsync(root);

            var result = await File.ReadAllTextAsync(dbContextPath);
            Assert.DoesNotContain("Domain.Entities.CodeGen", result);
            Assert.DoesNotContain("Entity<ProjectModule>", result);
            Assert.DoesNotContain("Entity<ModuleEntity>", result);
            Assert.Contains("Entity<SysUser>", result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemovePlatformWebApiProjectReferencesAsync_RemovesPlatformOnlyReferences()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var webApiDirectory = Path.Combine(root, "CodeMaster.WebApi");
        var projectPath = Path.Combine(webApiDirectory, "CodeMaster.WebApi.csproj");

        try
        {
            Directory.CreateDirectory(webApiDirectory);
            await File.WriteAllTextAsync(projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <ItemGroup>
                    <ProjectReference Include="..\CodeMaster.Agent\CodeMaster.Agent.csproj" />
                    <ProjectReference Include="..\CodeMaster.Application\CodeMaster.Application.csproj" />
                  </ItemGroup>
                  <ItemGroup>
                    <Content Include="..\Templates\CodeMaster_Template_*.zip" />
                    <Content Include="..\CodeMaster.CodeGenerator\Templates\**\*.*" />
                  </ItemGroup>
                </Project>
                """);

            var service = new TemplateExportService();
            await service.RemovePlatformWebApiProjectReferencesAsync(root);

            var result = await File.ReadAllTextAsync(projectPath);
            Assert.DoesNotContain("CodeMaster.Agent", result);
            Assert.DoesNotContain("CodeMaster_Template_", result);
            Assert.DoesNotContain("CodeMaster.CodeGenerator", result);
            Assert.Contains("CodeMaster.Application", result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoveFrontendAgentReferencesAsync_RemovesAgentUiAndState()
    {
        var root = Path.Combine(CodeMasterTestPaths.Root, $"codemaster-template-export-tests-{Guid.NewGuid():N}");
        var layoutDirectory = Path.Combine(root, "CodeMaster.Vue", "src", "layout");
        var layoutPath = Path.Combine(layoutDirectory, "index.vue");

        try
        {
            Directory.CreateDirectory(layoutDirectory);
            await File.WriteAllTextAsync(layoutPath,
                """
                <template>
                  <el-tooltip content="AI 助手" placement="bottom">
                    <el-button :icon="ChatDotRound" @click="agentVisible = true" />
                  </el-tooltip>
                  <theme-picker />
                  <agent-drawer v-model="agentVisible" />
                </template>
                <script setup>
                import { Fold, Expand, ChatDotRound } from '@element-plus/icons-vue'
                import AgentDrawer from '@/components/AgentAssistant/AgentDrawer.vue'
                const agentVisible = ref(false)
                </script>
                <style scoped>
                .agent-trigger {
                  color: red;
                  &:hover {
                    color: blue;
                  }
                }
                .header-item { display: flex; }
                </style>
                """);

            var service = new TemplateExportService();
            await service.RemoveFrontendAgentReferencesAsync(root);

            var result = await File.ReadAllTextAsync(layoutPath);
            Assert.DoesNotContain("AgentAssistant", result);
            Assert.DoesNotContain("agentVisible", result);
            Assert.DoesNotContain("ChatDotRound", result);
            Assert.DoesNotContain("agent-trigger", result);
            Assert.Contains("theme-picker", result);
            Assert.Contains("header-item", result);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void WriteRuntimeBundle(string bundle)
    {
        Directory.CreateDirectory(Path.Combine(bundle, "runtimes", "win-x64", "native"));
        Directory.CreateDirectory(Path.Combine(bundle, ".playwright", "package"));
        File.WriteAllText(Path.Combine(bundle, "CM.Printing.Runtime.dll"), "runtime");
        File.WriteAllText(Path.Combine(bundle, "CM.Printing.Runtime.deps.json"), "{}");
        File.WriteAllText(Path.Combine(bundle, "CM.Printing.Migrations.dll"), "migrations");
        File.WriteAllText(Path.Combine(bundle, "CM.Printing.Migrations.deps.json"), "{}");
        File.WriteAllText(Path.Combine(bundle, "NPOI.OOXML.dll"), "dependency");
        File.WriteAllText(Path.Combine(bundle, "runtimes", "win-x64", "native", "libSkiaSharp.dll"), "native");
        File.WriteAllText(Path.Combine(bundle, ".playwright", "package", "cli.js"), "playwright");
    }

    private static string ReadZipEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"ZIP entry not found: {entryName}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
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
