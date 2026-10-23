using System.Text;
using System.Text.Json;
using CodeMaster.UiComposition.Entities;
using CodeMaster.Domain.Entities.CodeGen;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Exports CodeMaster-managed view definitions into the generated project's
/// optional migrator module. EF Core intentionally excludes view entities from
/// table migrations, so the SQL must be applied after the tables exist.
/// </summary>
internal static class GeneratedViewMigrationExporter
{
    private const string RelativePath = "Persistence/EfCore/GeneratedViewMigrationModule.cs";

    /// <summary>
    /// 按项目重新导出视图迁移模块。全量/增量生成结束后调用，保证
    /// <c>GeneratedViewMigrationModule.cs</c> 与元数据同步。
    /// </summary>
    public static async Task<bool> ExportForProjectAsync(
        ISqlSugarClient db,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Queryable<Project>()
            .Where(item => item.Id == projectId && !item.IsDeleted)
            .FirstAsync(cancellationToken);
        if (project is null)
            return false;

        var definitions = await db.Queryable<ProjectViewDefinition>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .ToListAsync(cancellationToken);
        var definitionIds = definitions.Select(item => item.Id).ToList();
        var dependencies = definitionIds.Count == 0
            ? new List<ProjectViewDependency>()
            : await db.Queryable<ProjectViewDependency>()
                .Where(item => definitionIds.Contains(item.DefinitionId) && !item.IsDeleted)
                .ToListAsync(cancellationToken);
        var entities = await db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == projectId && !item.IsDeleted)
            .ToListAsync(cancellationToken);
        var entityIds = entities.Select(item => item.Id).ToList();
        var fields = entityIds.Count == 0
            ? new List<EntityField>()
            : await db.Queryable<EntityField>()
                .Where(item => entityIds.Contains(item.ModuleEntityId) && !item.IsDeleted && !item.IsIgnore)
                .ToListAsync(cancellationToken);
        return await ExportAsync(project, definitions, dependencies, entities, fields, cancellationToken);
    }

    public static async Task<bool> ExportAsync(
        Project project,
        IReadOnlyList<ProjectViewDefinition> definitions,
        IReadOnlyList<ProjectViewDependency> dependencies,
        IReadOnlyList<ModuleEntity> entities,
        IReadOnlyList<EntityField> fields,
        CancellationToken cancellationToken = default)
    {
        var projectRoot = ResolveProjectRoot(project);
        var migratorPath = Path.Combine(projectRoot, $"{project.ProjectName}.Migrator");
        if (!Directory.Exists(migratorPath))
            return false;

        var ordered = OrderDefinitions(
            definitions
                .Where(item => !item.IsDeleted && item.Ownership == ProjectViewOwnership.Managed &&
                               item.DeploymentState is (ProjectViewDeploymentState.DefinitionOnly or ProjectViewDeploymentState.Applied or ProjectViewDeploymentState.Drifted) &&
                               !string.IsNullOrWhiteSpace(item.Sql))
                .ToList(),
            dependencies);
        var filePath = Path.Combine(migratorPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var content = Render(project.ProjectName, ordered, entities, fields);
        if (File.Exists(filePath) && string.Equals(await File.ReadAllTextAsync(filePath, cancellationToken), content, StringComparison.Ordinal))
            return true;

        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false), cancellationToken);
        return true;
    }

    private static string ResolveProjectRoot(Project project)
    {
        var root = project.ProjectPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            return string.Empty;

        root = Path.GetFullPath(root);
        return Path.GetFileName(root).Equals(project.ProjectName, StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.Combine(root, project.ProjectName);
    }

    private static IReadOnlyList<ProjectViewDefinition> OrderDefinitions(
        IReadOnlyList<ProjectViewDefinition> definitions,
        IReadOnlyList<ProjectViewDependency> dependencies)
    {
        var byId = definitions.ToDictionary(item => item.Id);
        var outgoing = definitions.ToDictionary(item => item.Id, _ => new List<long>());
        var incoming = definitions.ToDictionary(item => item.Id, _ => 0);
        foreach (var dependency in dependencies.Where(item => byId.ContainsKey(item.DefinitionId) &&
                                                              item.SourceViewDefinitionId.HasValue &&
                                                              byId.ContainsKey(item.SourceViewDefinitionId.Value)))
        {
            var source = dependency.SourceViewDefinitionId!.Value;
            var target = dependency.DefinitionId;
            if (outgoing[source].Contains(target))
                continue;
            outgoing[source].Add(target);
            incoming[target]++;
        }

        var ready = new SortedSet<long>(definitions.Where(item => incoming[item.Id] == 0).Select(item => item.Id));
        var result = new List<ProjectViewDefinition>(definitions.Count);
        while (ready.Count > 0)
        {
            var id = ready.Min;
            ready.Remove(id);
            result.Add(byId[id]);
            foreach (var target in outgoing[id])
            {
                if (--incoming[target] == 0)
                    ready.Add(target);
            }
        }

        // A cycle cannot be deployed safely in dependency order. Keep the
        // remaining definitions deterministic so the generated build remains
        // reproducible; deployment validation reports the cycle separately.
        result.AddRange(definitions.Where(item => !result.Any(existing => existing.Id == item.Id))
            .OrderBy(item => item.Id));
        return result;
    }

    private static string Render(
        string projectName,
        IReadOnlyList<ProjectViewDefinition> definitions,
        IReadOnlyList<ModuleEntity> entities,
        IReadOnlyList<EntityField> fields)
    {
        var namespaceName = $"{projectName}.Migrator.Persistence.EfCore";
        var entries = definitions.Count == 0
            ? "        // No deployed CodeMaster-managed views."
            : string.Join(Environment.NewLine, definitions.Select(item =>
                $"        new(\"{Escape(item.ViewName)}\", {JsonSerializer.Serialize(ViewSqlTableResolver.Resolve(NormalizeSql(item.Sql!), entities, fields))}),"));

        var builder = new StringBuilder();
        builder.AppendLine($"namespace {namespaceName};");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Applies managed CodeMaster view definitions after EF table migrations.</summary>");
        builder.AppendLine("public sealed class GeneratedViewMigrationModule : ManagedViewMigrationModule");
        builder.AppendLine("{");
        builder.AppendLine("    protected override IReadOnlyCollection<ManagedViewDefinition> Views { get; } =");
        builder.AppendLine("    [");
        builder.AppendLine(entries);
        builder.AppendLine("    ];");
        builder.AppendLine();
        builder.AppendLine("    public override string ModuleName => \"GeneratedViews\";");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string NormalizeSql(string sql) => sql.Trim().TrimEnd(';').Trim();

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
