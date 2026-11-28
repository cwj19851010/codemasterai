using CodeMaster.Application.Services.Auth;
using CodeMaster.Core.Authorization;
using Microsoft.Extensions.Caching.Memory;

namespace CodeMaster.Application.Services.System;

/// <summary>
/// 用户权限的内存缓存实现。
/// JWT 里不再写权限声明，授权处理器按需调用这里；缓存 5 分钟，
/// 角色/菜单权限变更时调用 <see cref="InvalidateAll"/> 立即失效。
/// </summary>
public sealed class UserPermissionCache : IUserPermissionProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IAuthService _authService;
    private readonly IMemoryCache _cache;

    // 版本号用于整体失效：变一次号，之前所有缓存键自然查不到。
    private long _version;

    public UserPermissionCache(IAuthService authService, IMemoryCache cache)
    {
        _authService = authService;
        _cache = cache;
    }

    public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(
        long userId,
        long tenantId,
        CancellationToken cancellationToken = default)
    {
        var key = $"user-perms:{Interlocked.Read(ref _version)}:{tenantId}:{userId}";
        if (_cache.TryGetValue(key, out IReadOnlyCollection<string>? cached) && cached != null)
        {
            return cached;
        }

        var userInfo = await _authService.GetUserInfoAsync(userId);
        var permissions = userInfo?.Permissions?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();

        _cache.Set(key, (IReadOnlyCollection<string>)permissions, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl
        });
        return permissions;
    }

    public void Invalidate(long userId)
    {
        // 键里带 userId 但不知道 tenantId，直接整体失效最稳妥，代价只是多查几次库。
        InvalidateAll();
    }

    public void InvalidateAll()
    {
        Interlocked.Increment(ref _version);
    }
}
