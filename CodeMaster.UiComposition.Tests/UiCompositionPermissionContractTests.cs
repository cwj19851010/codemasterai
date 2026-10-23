using System.Reflection;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Authorization;
using Xunit;

namespace CodeMaster.UiComposition.Tests;

public sealed class UiCompositionPermissionContractTests
{
    public static TheoryData<Type> ProtectedServices => new()
    {
        typeof(ProjectPageService),
        typeof(ProjectDataSourceService),
        typeof(PageDisplayTemplateService),
        typeof(ProjectViewBuilderService),
        typeof(ComplexPageAgentPlanningService)
    };

    [Theory]
    [MemberData(nameof(ProtectedServices))]
    public void Dynamic_services_require_permissions_on_every_public_endpoint(Type serviceType)
    {
        var dynamicPermission = serviceType.GetCustomAttribute<DynamicApiPermissionAttribute>();
        Assert.NotNull(dynamicPermission);
        Assert.True(dynamicPermission!.RequirePermission);

        var endpointMethods = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .ToList();

        Assert.NotEmpty(endpointMethods);
        Assert.All(endpointMethods, method =>
        {
            var permission = method.GetCustomAttribute<PermissionAttribute>();
            Assert.True(permission is not null && permission.PermissionCodes.Length > 0,
                $"{serviceType.Name}.{method.Name} must declare a PermissionAttribute.");
        });
    }
}
