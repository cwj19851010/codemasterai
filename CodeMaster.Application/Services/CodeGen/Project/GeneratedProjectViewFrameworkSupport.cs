using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Brings projects created from older templates up to the minimum framework
/// contract required by generated read-only database-view entities.
/// </summary>
internal static class GeneratedProjectViewFrameworkSupport
{
    public static bool IsManagedViewEntity(ModuleEntity entity) =>
        entity.GenerationMode == EntityGenerationMode.FullStack &&
        entity.IsReadOnly &&
        !entity.HasAudit &&
        !entity.HasSoftDelete &&
        !string.IsNullOrWhiteSpace(entity.TableName);

    public static async Task EnsureAsync(string projectRoot, string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Generated project root and name are required for view framework support.");

        projectRoot = Path.GetFullPath(projectRoot);
        var coreDirectory = Path.Combine(projectRoot, $"{projectName}.Core", "Entities");
        var sourceGeneratorPath = Path.Combine(projectRoot, $"{projectName}.SourceGenerator", "DbContextGenerator.cs");
        var sqlSugarSetupPath = Path.Combine(
            projectRoot,
            $"{projectName}.Infrastructure",
            "Persistence",
            "SqlSugar",
            "SqlSugarSetup.cs");
        var migratorDirectory = Path.Combine(projectRoot, $"{projectName}.Migrator", "Persistence", "EfCore");
        var migratorDbContextPath = Path.Combine(migratorDirectory, $"{projectName}DbContext.cs");
        var migratorProgramPath = Path.Combine(projectRoot, $"{projectName}.Migrator", "Program.cs");
        if (!Directory.Exists(coreDirectory) || !File.Exists(sourceGeneratorPath) ||
            !File.Exists(migratorDbContextPath) || !File.Exists(migratorProgramPath))
            throw new InvalidOperationException(
                $"Generated project '{projectName}' is missing Core, SourceGenerator, or Migrator framework files.");

        Directory.CreateDirectory(migratorDirectory);
        await WriteFrameworkFileAsync(
            Path.Combine(coreDirectory, "IViewEntity.cs"),
            LoadEmbeddedTemplate("IViewEntity.cs.template").Replace("CodeMaster", projectName, StringComparison.Ordinal));
        await WriteFrameworkFileAsync(
            Path.Combine(migratorDirectory, "MigrationModuleCatalog.cs"),
            LoadEmbeddedTemplate("MigrationModuleCatalog.cs.template").Replace("CodeMaster", projectName, StringComparison.Ordinal));
        await UpgradeSourceGeneratorAsync(sourceGeneratorPath);
        if (File.Exists(sqlSugarSetupPath))
            await UpgradeSqlSugarSetupAsync(sqlSugarSetupPath);
        await UpgradeMigratorDbContextAsync(migratorDbContextPath);
        await UpgradeMigratorProgramAsync(migratorProgramPath, projectName);
    }

    private static async Task WriteFrameworkFileAsync(string path, string content)
    {
        var normalized = NormalizeLineEndings(content);
        if (File.Exists(path) && string.Equals(
                NormalizeLineEndings(await File.ReadAllTextAsync(path)),
                normalized,
                StringComparison.Ordinal))
            return;
        await File.WriteAllTextAsync(path, normalized, new UTF8Encoding(false));
    }

    private static async Task UpgradeSourceGeneratorAsync(string path)
    {
        var content = await File.ReadAllTextAsync(path);
        if (!content.Contains("IsView = typeSymbol.AllInterfaces.Any", StringComparison.Ordinal))
        {
            content = ReplaceRequired(
                content,
                "InheritsEntityBase = InheritsFromEntityBase(typeSymbol)",
                "InheritsEntityBase = InheritsFromEntityBase(typeSymbol),\n                IsView = typeSymbol.AllInterfaces.Any(i => i.Name == \"IViewEntity\")",
                "analyzing view entities");
        }

        if (!content.Contains("builder.ToView", StringComparison.Ordinal))
        {
            var tableMapping = new Regex(
                """(?<indent>[ \t]*)// 配置表名\s*\n\k<indent>sb\.AppendLine\(\$"\s*// 配置表名"\);\s*\n\k<indent>sb\.AppendLine\(\$"\s*builder\.ToTable\(\\"\{entity\.TableName\}\\"\);"\);""",
                RegexOptions.CultureInvariant);
            var upgraded = tableMapping.Replace(
                NormalizeLineEndings(content),
                match =>
                {
                    var indent = match.Groups["indent"].Value;
                    return string.Join('\n',
                        indent + "// Database views must not become CREATE TABLE operations in EF migrations.",
                        indent + "sb.AppendLine($\"            // 配置数据源\");",
                        indent + "if (entity.IsView)",
                        indent + "    sb.AppendLine($\"            builder.ToView(\\\"{entity.TableName}\\\");\");",
                        indent + "else",
                        indent + "    sb.AppendLine($\"            builder.ToTable(\\\"{entity.TableName}\\\");\");");
                },
                1);
            if (string.Equals(upgraded, NormalizeLineEndings(content), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Unable to upgrade the generated project while mapping view entities with ToView; the framework file shape is unsupported.");
            content = upgraded;
        }

        if (!Regex.IsMatch(content, @"public\s+bool\s+IsView\s*\{\s*get;\s*set;\s*\}"))
        {
            content = ReplaceRequired(
                content,
                "public bool InheritsEntityBase { get; set; }",
                "public bool InheritsEntityBase { get; set; }\n            public bool IsView { get; set; }",
                "persisting the view marker in source-generator metadata");
        }

        await File.WriteAllTextAsync(path, NormalizeLineEndings(content), new UTF8Encoding(false));
    }

    private static async Task UpgradeMigratorProgramAsync(string path, string projectName)
    {
        var content = await File.ReadAllTextAsync(path);
        if (content.Contains("ApplyModuleMigrationsAsync", StringComparison.Ordinal))
            return;

        var addDbContext = Regex.Match(
            content,
            @"services\.AddDbContext<(?<context>[A-Za-z_][A-Za-z0-9_]*)>\(opts\s*=>\s*\{[\s\S]*?^\}\);",
            RegexOptions.Multiline);
        if (!addDbContext.Success)
            throw new InvalidOperationException("Unable to upgrade the generated Migrator DbContext registration for managed views.");
        var contextName = addDbContext.Groups["context"].Value;
        var replacement = $$"""
            var baseMigrationsAssembly = typeof({{contextName}}).Assembly.GetName().Name
                ?? throw new InvalidOperationException("Unable to resolve the base migrations assembly name.");
            services.AddDbContext<{{contextName}}>(opts => ConfigureDbContextOptions(
                opts,
                connectionString,
                baseMigrationsAssembly,
                "__EFMigrationsHistory"));
            """;
        content = content[..addDbContext.Index] + replacement + content[(addDbContext.Index + addDbContext.Length)..];

        const string migrateMarker = "#endif\n    Console.WriteLine(\"✓ 数据库迁移完成\\n\");";
        content = ReplaceRequired(
            NormalizeLineEndings(content),
            migrateMarker,
            "#endif\n    await ApplyModuleMigrationsAsync(dbProvider, connectionString);\n    Console.WriteLine(\"✓ 数据库迁移完成\\n\");",
            "executing managed view migrations");

        var helpers = $$"""
            static void ConfigureDbContextOptions(
                DbContextOptionsBuilder optionsBuilder,
                string connectionString,
                string migrationsAssembly,
                string migrationsHistoryTable)
            {
            #if DB_MYSQL
                optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString),
                    provider => provider.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(migrationsHistoryTable));
            #elif DB_PGSQL
                optionsBuilder.UseNpgsql(connectionString,
                    provider => provider.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(migrationsHistoryTable));
            #elif DB_SQLITE
                optionsBuilder.UseSqlite(connectionString,
                    provider => provider.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(migrationsHistoryTable));
            #elif DB_ORACLE
                optionsBuilder.UseOracle(connectionString,
                    provider => provider.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(migrationsHistoryTable));
            #else
                optionsBuilder.UseSqlServer(connectionString,
                    provider => provider.MigrationsAssembly(migrationsAssembly).MigrationsHistoryTable(migrationsHistoryTable));
            #endif
            }

            static async Task ApplyModuleMigrationsAsync(string databaseProvider, string connectionString)
            {
                var modules = MigrationModuleCatalog.CreateDatabaseMigrationModules();
                var context = new MigrationModuleExecutionContext
                {
                    DatabaseProvider = databaseProvider,
                    ConnectionString = connectionString,
                    ConfigureDbContext = ConfigureDbContextOptions
                };
                foreach (var module in modules)
                {
                    Console.WriteLine($"  - Applying optional migration module: {module.ModuleName}");
                    await module.MigrateAsync(context);
                }
            }

            """;
        content = ReplaceRequired(
            content,
            "static string MaskConnectionString(string connectionString)",
            helpers + "static string MaskConnectionString(string connectionString)",
            $"adding managed view migration helpers to {projectName}.Migrator");
        await File.WriteAllTextAsync(path, NormalizeLineEndings(content), new UTF8Encoding(false));
    }

    private static async Task UpgradeSqlSugarSetupAsync(string path)
    {
        var content = NormalizeLineEndings(await File.ReadAllTextAsync(path));
        if (content.Contains("sugarColumn?.ColumnName", StringComparison.Ordinal))
            return;

        var assignment = new Regex(
            @"(?<indent>[ \t]*)column\.DbColumnName\s*=\s*ToSnakeCase\(property\.Name\);\s*\n(?:\k<indent>\s*\n)?(?:\k<indent>//[^\n]*\n)?\k<indent>column\.DataType\s*=\s*GetDatabaseType\(property\.PropertyType,\s*dbType\);",
            RegexOptions.CultureInvariant);
        var replacementCount = 0;
        content = assignment.Replace(content, match =>
        {
            replacementCount++;
            var indent = match.Groups["indent"].Value;
            return string.Join('\n',
                indent + "var sugarColumn = property.GetCustomAttributes(typeof(SugarColumn), false)",
                indent + "    .FirstOrDefault() as SugarColumn;",
                indent + "column.DbColumnName = !string.IsNullOrWhiteSpace(sugarColumn?.ColumnName)",
                indent + "    ? sugarColumn.ColumnName",
                indent + "    : ToSnakeCase(property.Name);",
                "",
                indent + "// Explicit SugarColumn database types must survive the global convention too.",
                indent + "column.DataType = !string.IsNullOrWhiteSpace(sugarColumn?.ColumnDataType)",
                indent + "    ? sugarColumn.ColumnDataType",
                indent + "    : GetDatabaseType(property.PropertyType, dbType);");
        });
        if (replacementCount == 0)
            throw new InvalidOperationException(
                "Unable to upgrade the generated SqlSugar column convention while preserving SugarColumn mappings; the framework file shape is unsupported.");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
    }

    private static async Task UpgradeMigratorDbContextAsync(string path)
    {
        var content = NormalizeLineEndings(await File.ReadAllTextAsync(path));
        if (content.Contains("View entities already have a ToView mapping", StringComparison.Ordinal))
            return;

        var loop = Regex.Match(
            content,
            @"(?m)^(?<indent>[ \t]*)foreach\s*\(\s*var\s+entity\s+in\s+modelBuilder\.Model\.GetEntityTypes\(\)\s*\)\s*\n\k<indent>\{");
        if (!loop.Success)
            throw new InvalidOperationException(
                "Unable to upgrade the generated project while preserving ToView mappings in the Migrator DbContext; the framework file shape is unsupported.");
        var indent = loop.Groups["indent"].Value;
        var childIndent = indent + "    ";
        var replacement = string.Join('\n',
            indent + "foreach (var entity in modelBuilder.Model.GetEntityTypes())",
            indent + "{",
            childIndent + "// View entities already have a ToView mapping from the source generator.",
            childIndent + "// Rewriting their table name would make EF migrations emit a shadow table.",
            childIndent + "if (entity.ClrType.GetInterfaces().Any(item => item.Name == \"IViewEntity\"))",
            childIndent + "    continue;");
        content = content[..loop.Index] + replacement + content[(loop.Index + loop.Length)..];
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
    }

    private static string LoadEmbeddedTemplate(string suffix)
    {
        var assembly = typeof(GeneratedProjectViewFrameworkSupport).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Embedded generated-project framework template '{suffix}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReplaceRequired(string content, string oldValue, string newValue, string operation)
    {
        if (!content.Contains(oldValue, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unable to upgrade the generated project while {operation}; the framework file shape is unsupported.");
        return content.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string NormalizeLineEndings(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
