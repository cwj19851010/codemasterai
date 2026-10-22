using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace CodeMaster.Migrator.Persistence.EfCore;

/// <summary>
/// Loads independent migration modules by a stable reflection contract:
/// public static Task MigrateAsync(string provider, string connectionString, CancellationToken token).
/// </summary>
public static class RuntimeMigrationModuleLoader
{
    private static readonly Lazy<IReadOnlyList<RuntimeMigrationModule>> Modules = new(LoadModules);

    public static bool HasModule(string assemblyName)
        => Modules.Value.Any(module => string.Equals(module.Name, assemblyName, StringComparison.OrdinalIgnoreCase));

    public static async Task ApplyAsync(
        string databaseProvider,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        foreach (var module in Modules.Value)
        {
            Console.WriteLine($"  - Applying independent runtime migration: {module.Name}");
            await module.MigrateAsync(databaseProvider, connectionString, cancellationToken);
        }
    }

    private static IReadOnlyList<RuntimeMigrationModule> LoadModules()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "modules"))
        };
        foreach (var path in (Environment.GetEnvironmentVariable("APP_RUNTIME_MIGRATION_PATHS") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            directories.Add(Path.GetFullPath(path));

        var existing = directories.Where(Directory.Exists).ToArray();
        var result = new List<RuntimeMigrationModule>();
        foreach (var path in existing
                     .SelectMany(directory => Directory.EnumerateFiles(directory, "CM.*.Migrations.dll", SearchOption.TopDirectoryOnly))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var loadContext = new RuntimeMigrationLoadContext(Path.GetDirectoryName(path)!);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));
            foreach (var type in GetLoadableTypes(assembly))
            {
                var method = type.GetMethod(
                    "MigrateAsync",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: [typeof(string), typeof(string), typeof(CancellationToken)],
                    modifiers: null);
                if (method != null && typeof(Task).IsAssignableFrom(method.ReturnType))
                    result.Add(new RuntimeMigrationModule(assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(path), method, loadContext));
            }
        }
        return result;
    }

    private static IEnumerable<string> GetNativeCandidates(string directory, string libraryName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { libraryName };
        if (!Path.HasExtension(libraryName))
        {
            if (OperatingSystem.IsWindows()) names.Add($"{libraryName}.dll");
            if (OperatingSystem.IsLinux()) names.Add($"lib{libraryName}.so");
            if (OperatingSystem.IsMacOS()) names.Add($"lib{libraryName}.dylib");
        }
        foreach (var name in names)
        {
            yield return Path.Combine(directory, name);
            yield return Path.Combine(directory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", name);
        }
    }

    private sealed class RuntimeMigrationLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;

        public RuntimeMigrationLoadContext(string directory)
            : base($"migration:{Path.GetFileName(directory)}:{Guid.NewGuid():N}", isCollectible: false)
        {
            _directory = directory;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (ShouldShare(assemblyName.Name))
            {
                var shared = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                    assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
                if (shared != null)
                    return shared;
            }

            var path = Path.Combine(_directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            foreach (var path in GetNativeCandidates(_directory, unmanagedDllName))
                if (File.Exists(path))
                    return LoadUnmanagedDllFromPath(path);
            return IntPtr.Zero;
        }

        private static bool ShouldShare(string? name)
            => name is "mscorlib" or "netstandard"
               || name?.StartsWith("System.", StringComparison.Ordinal) == true
               || name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null).Cast<Type>(); }
    }
}

public sealed class RuntimeMigrationModule
{
    private readonly MethodInfo _method;
    private readonly AssemblyLoadContext _loadContext;

    internal RuntimeMigrationModule(string name, MethodInfo method, AssemblyLoadContext loadContext)
    {
        Name = name;
        _method = method;
        _loadContext = loadContext;
    }

    public string Name { get; }

    public async Task MigrateAsync(string databaseProvider, string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            var task = (Task?)_method.Invoke(null, [databaseProvider, connectionString, cancellationToken])
                       ?? throw new InvalidOperationException($"{Name} migration returned no task.");
            await task;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw new InvalidOperationException($"Runtime migration {Name} failed: {exception.InnerException.Message}", exception.InnerException);
        }
    }
}
