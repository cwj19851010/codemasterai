using System.Collections.Generic;
using System.Reflection;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodeMaster.Infrastructure.DynamicApi;

/// <summary>
/// Reflection metadata shared by dynamic API consumers such as data-source
/// pickers. It is built from the exact service type set registered by
/// <see cref="DynamicApiExtensions.AddApplicationServices"/>.
/// </summary>
public sealed class ApplicationServiceCapabilityCatalog : IApplicationServiceCapabilityCatalog
{
    private readonly IReadOnlyList<ApplicationServiceCapability> _services;

    public ApplicationServiceCapabilityCatalog(IEnumerable<Type> serviceTypes)
    {
        _services = serviceTypes
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IApplicationService).IsAssignableFrom(type))
            .Select(CreateServiceCapability)
            .OrderBy(item => item.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ApplicationServiceCapability> Services => _services;

    public ApplicationServiceCapability? FindService(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName)) return null;
        var key = serviceName.Trim();
        return _services.FirstOrDefault(item => string.Equals(item.ServiceName, key, StringComparison.OrdinalIgnoreCase));
    }

    public ApplicationServiceMethodCapability? FindMethod(string serviceName, string methodName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(methodName)) return null;
        return FindService(serviceName)?.Methods.FirstOrDefault(item =>
            string.Equals(item.MethodName, methodName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static ApplicationServiceCapability CreateServiceCapability(Type serviceType)
    {
        var methods = serviceType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(IsExposedMethod)
            .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(method => method.GetParameters().Length)
                .ThenBy(method => method.ToString(), StringComparer.Ordinal)
                .First())
            .Select(method => CreateMethodCapability(serviceType, method))
            .OrderBy(item => item.MethodName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ApplicationServiceCapability(serviceType.Name, serviceType.FullName ?? serviceType.Name, methods);
    }

    private static bool IsExposedMethod(MethodInfo method)
    {
        if (method.IsSpecialName || method.IsStatic || method.IsGenericMethodDefinition)
            return false;
        if (method.DeclaringType == typeof(object))
            return false;
        if (method.GetCustomAttribute<NonActionAttribute>() != null)
            return false;
        var dynamicApi = method.GetCustomAttribute<DynamicApiAttribute>();
        return dynamicApi?.IsEnabled != false;
    }

    private static ApplicationServiceMethodCapability CreateMethodCapability(Type serviceType, MethodInfo method)
    {
        var resultType = UnwrapTask(method.ReturnType);
        var (shape, itemType) = ResolveResultShape(method.Name, resultType);
        var dynamicApi = method.GetCustomAttribute<DynamicApiAttribute>();
        var parameters = method.GetParameters()
            .Select(parameter => new ApplicationServiceParameterCapability(
                parameter.Name ?? string.Empty,
                GetFriendlyTypeName(parameter.ParameterType),
                parameter.IsOptional || parameter.HasDefaultValue,
                IsRouteParameter(parameter)))
            .ToArray();
        var moduleName = GetModuleName(serviceType.Namespace);
        var controllerName = GetControllerName(serviceType.Name);
        var actionRoute = string.IsNullOrWhiteSpace(dynamicApi?.Route)
            ? GetActionRoute(method)
            : dynamicApi!.Route!.Trim('/');

        return new ApplicationServiceMethodCapability(
            method.Name,
            GetFriendlyTypeName(resultType),
            GetFriendlyTypeName(itemType),
            shape,
            (dynamicApi?.HttpMethod ?? GetHttpVerb(method.Name)).ToUpperInvariant(),
            $"/api/{moduleName.ToLowerInvariant()}/{controllerName.ToLowerInvariant()}/{actionRoute}",
            parameters);
    }

    private static string GetModuleName(string? namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName)) return "app";
        var parts = namespaceName.Split('.');
        return parts.Length >= 4 ? parts[3] : "app";
    }

    private static string GetControllerName(string typeName)
    {
        var name = typeName;
        if (name.EndsWith("Service", StringComparison.Ordinal)) name = name[..^"Service".Length];
        if (name.EndsWith("AppService", StringComparison.Ordinal)) name = name[..^"AppService".Length];
        if (name.StartsWith('I') && name.Length > 1 && char.IsUpper(name[1])) name = name[1..];
        if (name.StartsWith("Sys", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3])) name = name[3..];
        return name;
    }

    private static string GetActionRoute(MethodInfo method)
    {
        var methodName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^"Async".Length]
            : method.Name;
        var route = methodName.ToLowerInvariant();
        foreach (var parameter in method.GetParameters().Where(IsRouteParameter))
            route += $"/{{{parameter.Name}}}";
        return route;
    }

    private static string GetHttpVerb(string methodName)
    {
        if (methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase)) return "GET";
        if (methodName.StartsWith("Create", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Add", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Insert", StringComparison.OrdinalIgnoreCase)) return "POST";
        if (methodName.StartsWith("Update", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Modify", StringComparison.OrdinalIgnoreCase)) return "PUT";
        if (methodName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)) return "DELETE";
        return "POST";
    }

    private static bool IsRouteParameter(ParameterInfo parameter)
    {
        if (parameter.GetCustomAttribute<FromRouteAttribute>() != null) return true;
        if (parameter.GetCustomAttribute<FromQueryAttribute>() != null ||
            parameter.GetCustomAttribute<FromBodyAttribute>() != null ||
            parameter.GetCustomAttribute<FromFormAttribute>() != null ||
            parameter.GetCustomAttribute<FromHeaderAttribute>() != null) return false;
        return !parameter.HasDefaultValue &&
               parameter.ParameterType != typeof(string) &&
               !IsComplexType(parameter.ParameterType);
    }

    private static bool IsComplexType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(TimeSpan))
            return false;
        return type != typeof(string);
    }

    private static (string Shape, Type ItemType) ResolveResultShape(string methodName, Type resultType)
    {
        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(PagedResultDto<>))
            return ("Paged", resultType.GetGenericArguments()[0]);

        if (resultType != typeof(string) && resultType != typeof(byte[]) &&
            resultType.GetInterfaces().Any(item => item == typeof(System.Collections.IEnumerable)))
        {
            var itemType = resultType.IsArray
                ? resultType.GetElementType()!
                : resultType.GetGenericArguments().FirstOrDefault() ?? typeof(object);
            return (methodName.Contains("Option", StringComparison.OrdinalIgnoreCase) ? "Options" : "Collection", itemType);
        }

        return ("Single", resultType);
    }

    private static Type UnwrapTask(Type type)
    {
        if (type == typeof(Task) || type == typeof(ValueTask)) return typeof(void);
        if (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) ||
                                   type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            return type.GetGenericArguments()[0];
        return type;
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            return $"{name}<{string.Join(",", type.GetGenericArguments().Select(GetFriendlyTypeName))}>";
        }

        return type.FullName ?? type.Name;
    }
}
