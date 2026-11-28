namespace CodeMaster.Core.Authorization;

/// <summary>
/// 按用户读取权限集合的提供者。
/// JWT 里不再携带权限声明，授权处理器在需要时通过它查（实现方负责缓存）。
/// </summary>
public interface IUserPermissionProvider
{
    /// <summary>
    /// 获取指定用户的权限码集合（已去重，大小写不敏感比较由调用方处理）。
    /// </summary>
    Task<IReadOnlyCollection<string>> GetPermissionsAsync(long userId, long tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 让某个用户的权限缓存失效。
    /// </summary>
    void Invalidate(long userId);

    /// <summary>
    /// 让所有用户的权限缓存失效（角色、菜单权限变更后调用）。
    /// </summary>
    void InvalidateAll();
}
