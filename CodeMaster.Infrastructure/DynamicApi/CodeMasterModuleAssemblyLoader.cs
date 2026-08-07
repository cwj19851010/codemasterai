using System.Reflection;
using System.Runtime.Loader;

namespace CodeMaster.Infrastructure.DynamicApi;

public static class CodeMasterModuleAssemblyLoader
{
    public static IReadOnlyList<Assembly> LoadOptionalModules(string baseDirectory, string? configuredPaths = null)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(baseDirectory, "modules"))
        };
        foreach (var path in (configuredPaths ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            directories.Add(Path.GetFullPath(path));
        }

        var existingDirectories = directories.Where(Directory.Exists).ToArray();
        if (existingDirectories.Length == 0) return Array.Empty<Assembly>();

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

        var loaded = new List<Assembly>();
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
            loaded.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)));
            knownNames.Add(assemblyName.Name);
        }

        return loaded;
    }
}
