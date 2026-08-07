using System.Reflection;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Core.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeMaster.Infrastructure.DynamicApi;

public static class DynamicApiExtensions
{
    public static IMvcBuilder AddDynamicApi(
        this IMvcBuilder mvcBuilder,
        Assembly applicationAssembly,
        Action<DynamicApiOptions>? configure = null)
        => AddDynamicApi(mvcBuilder, new[] { applicationAssembly }, configure);

    public static IMvcBuilder AddDynamicApi(
        this IMvcBuilder mvcBuilder,
        IEnumerable<Assembly> applicationAssemblies,
        Action<DynamicApiOptions>? configure = null)
    {
        var assemblies = applicationAssemblies.Distinct().ToArray();
        var services = mvcBuilder.Services;
        if (configure != null)
            services.Configure(configure);
        else
            services.Configure<DynamicApiOptions>(_ => { });

        mvcBuilder.ConfigureApplicationPartManager(manager =>
        {
            manager.FeatureProviders.Add(new DynamicApiControllerFeatureProvider(assemblies));
        });

        mvcBuilder.AddMvcOptions(options =>
        {
            using var serviceProvider = services.BuildServiceProvider();
            var convention = new DynamicApiControllerConvention(
                serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamicApiOptions>>());
            options.Conventions.Add(convention);
        });

        return mvcBuilder;
    }

    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        Assembly applicationAssembly)
        => AddApplicationServices(services, new[] { applicationAssembly });

    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IEnumerable<Assembly> applicationAssemblies)
    {
        var allTypes = applicationAssemblies
            .Distinct()
            .SelectMany(GetLoadableTypes)
            .ToList();
        var serviceTypes = allTypes
            .Where(type => type.IsClass && !type.IsAbstract &&
                           typeof(Core.Services.IApplicationService).IsAssignableFrom(type))
            .ToList();

        Console.WriteLine($"[AddApplicationServices] Found {serviceTypes.Count} application services");
        foreach (var serviceType in serviceTypes)
        {
            var interfaceType = serviceType.GetInterfaces()
                .FirstOrDefault(candidate =>
                    candidate.Name == $"I{serviceType.Name}" &&
                    candidate.GetInterfaces().Any(parent => parent.Name == "IApplicationService"));

            if (interfaceType != null)
            {
                services.AddScoped(interfaceType, serviceType);
                Console.WriteLine($"  - Register service: {interfaceType.Name} -> {serviceType.Name}");
            }

            services.AddScoped(serviceType);
        }

        services.TryAddSingleton<IApplicationServiceCapabilityCatalog>(
            new ApplicationServiceCapabilityCatalog(serviceTypes));

        services.AddCodeMasterExtensions(allTypes);

        return services;
    }

    public static IServiceCollection AddCodeMasterExtensions(
        this IServiceCollection services,
        IEnumerable<Assembly> extensionAssemblies)
        => services.AddCodeMasterExtensions(extensionAssemblies.Distinct().SelectMany(GetLoadableTypes));

    private static IServiceCollection AddCodeMasterExtensions(
        this IServiceCollection services,
        IEnumerable<Type> candidateTypes)
    {
        var extensionMarker = typeof(ICodeMasterExtension);
        var extensionTypes = candidateTypes
            .Where(type => type.IsClass && !type.IsAbstract && extensionMarker.IsAssignableFrom(type))
            .Distinct()
            .ToList();
        foreach (var extensionType in extensionTypes)
        {
            foreach (var contract in extensionType.GetInterfaces().Where(extensionMarker.IsAssignableFrom))
            {
                services.AddScoped(contract, extensionType);
                Console.Error.WriteLine($"  - Register extension: {contract.Name} -> {extensionType.Name}");
            }
        }

        return services;
    }

    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped(typeof(Core.Repositories.IReadOnlyRepository<>), typeof(Persistence.Repositories.ReadOnlyRepository<>));
        services.AddScoped(typeof(Core.Repositories.IRepository<>), typeof(Persistence.Repositories.Repository<>));
        return services;
    }

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
