using System.Security.Claims;
using CodeMaster.Core.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace CodeMaster.Infrastructure.Authorization;

/// <summary>
/// 权限授权处理器。
/// JWT 只携带身份信息，权限优先从令牌声明读取（兼容 MCP PAT 与未过期的旧令牌），
/// 读不到时按用户查询 <see cref="IUserPermissionProvider"/>（内部带缓存）。
/// </summary>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IUserPermissionProvider? _permissionProvider;

    public PermissionAuthorizationHandler(IUserPermissionProvider? permissionProvider = null)
    {
        _permissionProvider = permissionProvider;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (IsHostAdmin(context.User))
        {
            context.Succeed(requirement);
            return;
        }

        var permissionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in context.User.FindAll("Permission"))
        {
            if (!string.IsNullOrWhiteSpace(claim.Value))
            {
                permissionSet.Add(claim.Value);
            }
        }

        var packed = context.User.FindFirst("Permissions")?.Value;
        if (!string.IsNullOrWhiteSpace(packed))
        {
            foreach (var p in packed.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                permissionSet.Add(p.Trim());
            }
        }

        if (IsSatisfied(permissionSet, requirement))
        {
            context.Succeed(requirement);
            return;
        }

        // 令牌里没有权限声明（新签发的 JWT 就是这样），按用户查缓存。
        if (_permissionProvider == null)
        {
            return;
        }

        var userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
        if (!long.TryParse(userIdClaim?.Value, out var userId))
        {
            return;
        }

        long.TryParse(context.User.FindFirst("TenantId")?.Value, out var tenantId);

        try
        {
            var permissions = await _permissionProvider.GetPermissionsAsync(userId, tenantId);
            foreach (var permission in permissions)
            {
                permissionSet.Add(permission);
            }
        }
        catch
        {
            // 查权限失败按无权限处理，避免把异常抛成 500。
            return;
        }

        if (IsSatisfied(permissionSet, requirement))
        {
            context.Succeed(requirement);
        }
    }

    /// <summary>
    /// 支持多个权限（OR 逻辑）：任意一个权限满足即可。
    /// </summary>
    private static bool IsSatisfied(HashSet<string> permissionSet, PermissionRequirement requirement)
    {
        foreach (var requiredPermission in requirement.Permissions)
        {
            if (permissionSet.Contains(requiredPermission))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHostAdmin(ClaimsPrincipal user)
    {
        var isHostAdminClaim = user.FindFirst("IsHostAdmin")?.Value;
        if (bool.TryParse(isHostAdminClaim, out var isHostAdmin) && isHostAdmin)
        {
            return true;
        }

        var tenantIdClaim = user.FindFirst("TenantId")?.Value;
        var isHostTenant = long.TryParse(tenantIdClaim, out var tenantId) && tenantId == 0;
        var userName = user.Identity?.Name ?? user.FindFirst(ClaimTypes.Name)?.Value;
        return isHostTenant
            && !string.IsNullOrEmpty(userName)
            && userName.Equals("admin", StringComparison.OrdinalIgnoreCase);
    }
}
