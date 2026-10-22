using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeMaster.Infrastructure.DynamicApi;

/// <summary>
/// Loads project-independent runtime modules. The CM prefix is intentionally
/// stable and is not affected by generated-project name replacement.
/// </summary>
public static class RuntimeWebModuleLoader
{
    public static IReadOnlyList<RuntimeWebModule> Load(string baseDirectory, string? configuredPaths = null)
    {
        var directories = BuildDirectories(baseDirectory, configuredPaths);
        var result = new List<RuntimeWebModule>();

        foreach (var path in directories
                     .SelectMany(directory => Directory.EnumerateFiles(directory, "CM.*.Runtime.dll", SearchOption.TopDirectoryOnly))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var loadContext = new RuntimeModuleLoadContext(Path.GetDirectoryName(path)!);
            var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path));
            foreach (var type in GetLoadableTypes(assembly))
            {
                var configure = type.GetMethod(
                    "ConfigureServices",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: [typeof(IServiceCollection), typeof(IConfiguration)],
                    modifiers: null);
                var map = type.GetMethod(
                    "MapEndpoints",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: [typeof(IEndpointRouteBuilder)],
                    modifiers: null);
                if (configure != null && map != null)
                    result.Add(new RuntimeWebModule(assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(path), configure, map, loadContext));
            }
        }

        return result;
    }

    private static string[] BuildDirectories(string baseDirectory, string? configuredPaths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(baseDirectory, "modules"))
        };
        foreach (var path in (configuredPaths ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            directories.Add(Path.GetFullPath(path));
        return directories.Where(Directory.Exists).ToArray();
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

    private sealed class RuntimeModuleLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;

        public RuntimeModuleLoadContext(string directory)
            : base($"runtime:{Path.GetFileName(directory)}:{Guid.NewGuid():N}", isCollectible: false)
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
               || name?.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) == true
               || name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null).Cast<Type>(); }
    }
}

public sealed class RuntimeWebModule
{
    private readonly MethodInfo _configure;
    private readonly MethodInfo _map;
    private readonly AssemblyLoadContext _loadContext;

    internal RuntimeWebModule(string name, MethodInfo configure, MethodInfo map, AssemblyLoadContext loadContext)
    {
        Name = name;
        _configure = configure;
        _map = map;
        _loadContext = loadContext;
    }

    public string Name { get; }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => Invoke(_configure, services, configuration);

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
        => Invoke(_map, endpoints);

    private static void Invoke(MethodInfo method, params object[] arguments)
    {
        try { method.Invoke(null, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw new InvalidOperationException(
                $"Runtime module method {method.DeclaringType?.FullName}.{method.Name} failed: {exception.InnerException.Message}",
                exception.InnerException);
        }
    }
}
