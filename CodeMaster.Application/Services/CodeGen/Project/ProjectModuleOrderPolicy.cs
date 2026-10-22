namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Top-level generated-project menu order contract.
/// System and monitor own 1/2; reports owns the terminal 9999 slot.
/// </summary>
public static class ProjectModuleOrderPolicy
{
    public const int SystemDirectoryOrder = 1;
    public const int MonitorDirectoryOrder = 2;
    public const int FirstBusinessDirectoryOrder = 3;
    public const int LastBusinessDirectoryOrder = 9998;
    public const int ReportsDirectoryOrder = 9999;

    public static bool IsBusinessDirectoryOrder(int orderNum)
        => orderNum is >= FirstBusinessDirectoryOrder and <= LastBusinessDirectoryOrder;

    public static bool IsReportsModule(string? moduleName, string? moduleDescription, string? routePath = null)
    {
        var normalizedName = moduleName?.Trim();
        var normalizedRoute = routePath?.Trim().Trim('/');
        return string.Equals(normalizedName, "Reporting", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedName, "Reports", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedRoute, "reporting", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalizedRoute, "reports", StringComparison.OrdinalIgnoreCase)
               || string.Equals(moduleDescription?.Trim(), "报表管理", StringComparison.OrdinalIgnoreCase);
    }

    public static int NormalizeForMenu(
        int orderNum,
        string? moduleName = null,
        string? moduleDescription = null,
        string? routePath = null)
        => IsReportsModule(moduleName, moduleDescription, routePath)
            ? ReportsDirectoryOrder
            : IsBusinessDirectoryOrder(orderNum) ? orderNum : FirstBusinessDirectoryOrder;

    public static int ResolveModuleOrder(
        int requestedOrder,
        IEnumerable<int> occupiedOrders,
        string? moduleName,
        string? moduleDescription,
        string? routePath = null)
        => IsReportsModule(moduleName, moduleDescription, routePath)
            ? ReportsDirectoryOrder
            : ResolveAvailable(requestedOrder, occupiedOrders);

    public static int ResolveAvailable(int requestedOrder, IEnumerable<int> occupiedOrders)
    {
        var occupied = occupiedOrders
            .Where(IsBusinessDirectoryOrder)
            .ToHashSet();

        if (IsBusinessDirectoryOrder(requestedOrder) && !occupied.Contains(requestedOrder))
            return requestedOrder;

        for (var candidate = FirstBusinessDirectoryOrder; candidate <= LastBusinessDirectoryOrder; candidate++)
        {
            if (!occupied.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("No generated-project business menu order is available in the range 3..9998.");
    }
}
