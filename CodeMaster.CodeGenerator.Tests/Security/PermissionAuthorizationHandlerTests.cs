using System.Security.Claims;
using CodeMaster.Core.Authorization;
using CodeMaster.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace CodeMaster.CodeGenerator.Tests.Security;

/// <summary>
/// 权限授权处理器：令牌不再携带权限声明后，必须能按用户从缓存提供者查到权限。
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    private sealed class FakePermissionProvider : IUserPermissionProvider
    {
        private readonly string[] _permissions;

        public FakePermissionProvider(params string[] permissions) => _permissions = permissions;

        public int Calls { get; private set; }

        public Task<IReadOnlyCollection<string>> GetPermissionsAsync(
            long userId,
            long tenantId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyCollection<string>>(_permissions);
        }

        public void Invalidate(long userId)
        {
        }

        public void InvalidateAll()
        {
        }
    }

    private static ClaimsPrincipal CreateUser(long userId = 100, long tenantId = 1)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "tester"),
            new Claim("TenantId", tenantId.ToString())
        }, "test");
        return new ClaimsPrincipal(identity);
    }

    private static async Task<(bool Success, FakePermissionProvider? Provider)> EvaluateAsync(
        FakePermissionProvider? provider,
        params string[] requiredPermissions)
    {
        var handler = new PermissionAuthorizationHandler(provider);
        var requirement = new PermissionRequirement(requiredPermissions);
        var context = new AuthorizationHandlerContext(new[] { requirement }, CreateUser(), resource: null);

        await handler.HandleAsync(context);
        return (context.HasSucceeded, provider);
    }

    [Fact]
    public async Task Permission_is_resolved_from_provider_when_token_has_no_permission_claim()
    {
        var (success, provider) = await EvaluateAsync(
            new FakePermissionProvider("system:user:list"),
            "system:user:list");

        Assert.True(success);
        Assert.Equal(1, provider!.Calls);
    }

    [Fact]
    public async Task Missing_permission_is_rejected()
    {
        var (success, _) = await EvaluateAsync(new FakePermissionProvider("other:perm"), "system:user:list");
        Assert.False(success);
    }

    [Fact]
    public async Task Permission_claim_still_works_without_provider()
    {
        var handler = new PermissionAuthorizationHandler(null);
        var requirement = new PermissionRequirement("system:user:list");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "100"),
            new Claim(ClaimTypes.Name, "tester"),
            new Claim("TenantId", "1"),
            new Claim("Permission", "system:user:list")
        }, "test");
        var context = new AuthorizationHandlerContext(new[] { requirement }, new ClaimsPrincipal(identity), null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Host_admin_shortcut_does_not_hit_provider()
    {
        var provider = new FakePermissionProvider();
        var handler = new PermissionAuthorizationHandler(provider);
        var requirement = new PermissionRequirement("system:user:list");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Name, "admin"),
            new Claim("TenantId", "0"),
            new Claim("IsHostAdmin", "true")
        }, "test");
        var context = new AuthorizationHandlerContext(new[] { requirement }, new ClaimsPrincipal(identity), null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(0, provider.Calls);
    }
}
