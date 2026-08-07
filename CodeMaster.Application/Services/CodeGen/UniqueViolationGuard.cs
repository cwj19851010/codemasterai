using System.Reflection;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 将数据库唯一约束冲突异常包装为友好业务异常（跨 SqlSugar/Npgsql/MySql/Sqlite/SqlServer）
/// </summary>
public static class UniqueViolationGuard
{
    public static Exception Wrap(Exception exception, string friendlyMessage)
    {
        var current = exception;
        while (current != null)
        {
            if (IsUniqueViolation(current))
            {
                return new InvalidOperationException(friendlyMessage, exception);
            }
            current = current.InnerException;
        }
        return exception;
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        var typeName = exception.GetType().Name;
        switch (typeName)
        {
            case "PostgresException":
                return string.Equals(
                    GetPropertyValue(exception, "SqlState") as string,
                    "23505",
                    StringComparison.Ordinal);
            case "MySqlException":
                return GetPropertyValue(exception, "Number") is int mySqlNumber && mySqlNumber == 1062;
            case "SqliteException":
                return GetPropertyValue(exception, "SqliteErrorCode") is int sqliteCode && sqliteCode == 19;
            case "SqlException":
                return GetPropertyValue(exception, "Number") is int sqlNumber && (sqlNumber == 2601 || sqlNumber == 2627);
            default:
                return false;
        }
    }

    private static object? GetPropertyValue(Exception exception, string propertyName)
    {
        return exception.GetType().GetProperty(propertyName)?.GetValue(exception);
    }
}
