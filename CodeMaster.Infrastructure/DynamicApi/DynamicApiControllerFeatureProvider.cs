using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CodeMaster.Infrastructure.DynamicApi;

public class DynamicApiControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
{
    private readonly IReadOnlyCollection<Assembly> _applicationAssemblies;

    public DynamicApiControllerFeatureProvider(Assembly applicationAssembly)
        : this(new[] { applicationAssembly })
    {
    }

    public DynamicApiControllerFeatureProvider(IEnumerable<Assembly> applicationAssemblies)
    {
        _applicationAssemblies = applicationAssemblies.Distinct().ToArray();
    }

    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        var serviceTypes = _applicationAssemblies
            .SelectMany(GetLoadableTypes)
            .Where(type => type.IsClass && !type.IsAbstract && IsApplicationService(type))
            .ToList();

        foreach (var serviceType in serviceTypes)
        {
            if (!feature.Controllers.Contains(serviceType.GetTypeInfo()))
                feature.Controllers.Add(serviceType.GetTypeInfo());
        }
    }

    private static bool IsApplicationService(Type type) =>
        typeof(Core.Services.IApplicationService).IsAssignableFrom(type);

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
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
}
