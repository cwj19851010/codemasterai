using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Reflection;
using System.Runtime.InteropServices;
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

public sealed record ManagedViewDefinition(string Name, string Sql);

/// <summary>
/// Base implementation for generated view modules. It records the views owned by
/// CodeMaster in the target database so removed definitions can be dropped safely.
/// </summary>
public abstract class ManagedViewMigrationModule : IDatabaseMigrationModule
{
    protected abstract IReadOnlyCollection<ManagedViewDefinition> Views { get; }
    public abstract string ModuleName { get; }

    public async Task MigrateAsync(MigrationModuleExecutionContext context, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder();
        context.ConfigureDbContext(options, context.ConnectionString, GetType().Assembly.GetName().Name!, "__EFMigrationsHistory");
        await using var db = new DbContext(options.Options);
        var provider = context.DatabaseProvider.Trim().ToLowerInvariant();
        await using var transaction = provider is "postgresql" or "postgres" or "npgsql" or "sqlite"
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var current = Views.Where(view => !string.IsNullOrWhiteSpace(view.Name) && !string.IsNullOrWhiteSpace(view.Sql))
            .GroupBy(view => view.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagedViewDefinition(group.Key, group.Last().Sql.Trim().TrimEnd(';').Trim())).ToArray();

        await db.Database.ExecuteSqlRawAsync(CreateRegistrySql(provider), cancellationToken);
        var previous = await ReadManagedViewNamesAsync(db, provider, cancellationToken);
        var currentNames = current.Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (provider is "postgresql" or "postgres" or "npgsql" or "sqlite")
            foreach (var view in current.Reverse())
                await db.Database.ExecuteSqlRawAsync($"DROP VIEW IF EXISTS {Quote(view.Name, provider)}", cancellationToken);
        foreach (var removed in previous.Where(name => !currentNames.Contains(name)))
            await db.Database.ExecuteSqlRawAsync($"DROP VIEW IF EXISTS {Quote(removed, provider)}", cancellationToken);

        foreach (var view in current)
        {
            var name = Quote(view.Name, provider);
            var statement = provider switch
            {
                "sqlserver" or "mssql" => $"CREATE OR ALTER VIEW {name} AS {view.Sql}",
                "sqlite" => $"CREATE VIEW {name} AS {view.Sql}",
                "postgresql" or "postgres" or "npgsql" => $"CREATE VIEW {name} AS {view.Sql}",
                _ => $"CREATE OR REPLACE VIEW {name} AS {view.Sql}"
            };
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        await ExecuteRegistryCommandAsync(
            db,
            $"DELETE FROM {Quote(RegistryTable, provider)} WHERE {Quote("module_name", provider)} = @module",
            null,
            cancellationToken);
        foreach (var view in current)
        {
            await ExecuteRegistryCommandAsync(
                db,
                $"INSERT INTO {Quote(RegistryTable, provider)} ({Quote("module_name", provider)}, {Quote("view_name", provider)}) VALUES (@module, @name)",
                view.Name,
                cancellationToken);
        }
        if (transaction != null)
            await transaction.CommitAsync(cancellationToken);
    }

    private const string RegistryTable = "__codemaster_managed_views";

    private static string CreateRegistrySql(string provider)
    {
        var table = Quote(RegistryTable, provider);
        var moduleColumn = Quote("module_name", provider);
        var column = Quote("view_name", provider);
        return provider switch
        {
            "sqlserver" or "mssql" => $"IF OBJECT_ID(N'{RegistryTable}', N'U') IS NULL CREATE TABLE {table} ({moduleColumn} nvarchar(255) NOT NULL, {column} nvarchar(255) NOT NULL, PRIMARY KEY ({moduleColumn}, {column}))",
            "mysql" or "mariadb" => $"CREATE TABLE IF NOT EXISTS {table} ({moduleColumn} varchar(255) NOT NULL, {column} varchar(255) NOT NULL, PRIMARY KEY ({moduleColumn}, {column}))",
            _ => $"CREATE TABLE IF NOT EXISTS {table} ({moduleColumn} text NOT NULL, {column} text NOT NULL, PRIMARY KEY ({moduleColumn}, {column}))"
        };
    }

    private async Task<HashSet<string>> ReadManagedViewNamesAsync(DbContext db, string provider, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT {Quote("view_name", provider)} FROM {Quote(RegistryTable, provider)} WHERE {Quote("module_name", provider)} = @module";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@module";
        parameter.Value = ModuleName;
        command.Parameters.Add(parameter);
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    private async Task ExecuteRegistryCommandAsync(
        DbContext db,
        string commandText,
        string? viewName,
        CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = commandText;
        var moduleParameter = command.CreateParameter();
        moduleParameter.ParameterName = "@module";
        moduleParameter.Value = ModuleName;
        command.Parameters.Add(moduleParameter);
        if (viewName != null)
        {
            var viewParameter = command.CreateParameter();
            viewParameter.ParameterName = "@name";
            viewParameter.Value = viewName;
            command.Parameters.Add(viewParameter);
        }
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Quote(string value, string provider) => provider switch
    {
        "mysql" or "mariadb" => $"`{value.Replace("`", "``")}`",
        "sqlserver" or "mssql" => $"[{value.Replace("]", "]]")}]",
        _ => $"\"{value.Replace("\"", "\"\"")}\""
    };
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
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, libraryName) =>
        {
            foreach (var directory in existingDirectories)
            {
                foreach (var candidate in GetUnmanagedLibraryCandidates(directory, libraryName))
                {
                    if (File.Exists(candidate))
                        return NativeLibrary.Load(candidate);
                }
            }

            return IntPtr.Zero;
        };

        var knownNames = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in existingDirectories
                     .SelectMany(directory => Directory.EnumerateFiles(directory, "CodeMaster*.dll", SearchOption.TopDirectoryOnly))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var assemblyName = AssemblyName.GetAssemblyName(path);
            if (knownNames.Contains(assemblyName.Name)) continue;
            result.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)));
            knownNames.Add(assemblyName.Name);
        }

        return result;
    }

    private static IEnumerable<string> GetUnmanagedLibraryCandidates(string directory, string libraryName)
    {
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { libraryName };
        if (!Path.HasExtension(libraryName))
        {
            if (OperatingSystem.IsWindows()) fileNames.Add($"{libraryName}.dll");
            if (OperatingSystem.IsLinux()) fileNames.Add($"lib{libraryName}.so");
            if (OperatingSystem.IsMacOS()) fileNames.Add($"lib{libraryName}.dylib");
        }

        var runtimeNativeDirectory = Path.Combine(
            directory,
            "runtimes",
            RuntimeInformation.RuntimeIdentifier,
            "native");
        foreach (var fileName in fileNames)
        {
            yield return Path.Combine(directory, fileName);
            yield return Path.Combine(runtimeNativeDirectory, fileName);
        }
    }
}
