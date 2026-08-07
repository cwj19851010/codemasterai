namespace CodeMaster.Core.DynamicApi;

/// <summary>
/// Read-only metadata for application services that were registered through
/// the Dynamic API service-registration pipeline.
/// </summary>
public interface IApplicationServiceCapabilityCatalog
{
    IReadOnlyList<ApplicationServiceCapability> Services { get; }

    ApplicationServiceCapability? FindService(string serviceName);

    ApplicationServiceMethodCapability? FindMethod(string serviceName, string methodName);
}

public sealed record ApplicationServiceCapability(
    string ServiceName,
    string ImplementationType,
    IReadOnlyList<ApplicationServiceMethodCapability> Methods);

public sealed record ApplicationServiceMethodCapability(
    string MethodName,
    string ResultType,
    string ItemType,
    string ResultShape,
    string HttpMethod,
    string RoutePath,
    IReadOnlyList<ApplicationServiceParameterCapability> Parameters);

public sealed record ApplicationServiceParameterCapability(
    string Name,
    string TypeName,
    bool IsOptional,
    bool IsRouteParameter);
