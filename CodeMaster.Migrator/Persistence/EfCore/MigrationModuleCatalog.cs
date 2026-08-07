using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Runtime.Loader;

namespace CodeMaster.Migrator.Persistence.EfCore;

public interface IMigrationModelContributor
{
    void Configure(ModelBuilder modelBuilder);
}

public sealed class MigrationModuleExecutionContext
{
    public required string DatabaseProvider { get; init; }

    public required string ConnectionString { get; init; }

    public required Action<DbContextOptionsBuilder, string, string, string> ConfigureDbContext { get; init; }
}

public interface IDatabaseMigrationModule
{
    string ModuleName { get; }

    Task MigrateAsync(
        MigrationModuleExecutionContext context,
        CancellationToken cancellationToken = default);
}

public static class MigrationModuleCatalog
{
    private static readonly Lazy<IReadOnlyList<Assembly>> LoadedAssemblies = new(LoadAssemblies);

    public static IReadOnlyList<Assembly> Assemblies => LoadedAssemblies.Value;

    public static IReadOnlyList<IMigrationModelContributor> CreateModelContributors()
    {
        return Assemblies
            .SelectMany(GetLoadableTypes)
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IMigrationModelContributor).IsAssignableFrom(type))
            .Select(type => (IMigrationModelContributor)Activator.CreateInstance(type)!)
            .ToList();
    }

    public static IReadOnlyList<IDatabaseMigrationModule> CreateDatabaseMigrationModules()
    {
        return Assemblies
            .SelectMany(GetLoadableTypes)
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IDatabaseMigrationModule).IsAssignableFrom(type))
            .Select(type => (IDatabaseMigrationModule)Activator.CreateInstance(type)!)
            .OrderBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null).Cast<Type>();
        }
    }

    private static IReadOnlyList<Assembly> LoadAssemblies()
    {
        var result = new List<Assembly> { Assembly.GetExecutingAssembly() };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "modules"))
        };
        foreach (var path in (Environment.GetEnvironmentVariable("CODEMASTER_MIGRATION_MODULE_PATHS") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            directories.Add(Path.GetFullPath(path));
        }

        var existingDirectories = directories.Where(Directory.Exists).ToArray();
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var directory in existingDirectories)
            {
                var dependencyPath = Path.Combine(directory, $"{name.Name}.dll");
                if (File.Exists(dependencyPath))
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(dependencyPath);
            }

            return null;
        };

        var knownNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in existingDirectories
                     .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var assemblyName = AssemblyName.GetAssemblyName(path);
            if (knownNames.Contains(assemblyName.Name)) continue;
            result.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)));
            knownNames.Add(assemblyName.Name);
        }

        return result;
    }
}
