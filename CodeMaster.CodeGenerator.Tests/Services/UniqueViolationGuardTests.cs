using CodeMaster.Application.Services.CodeGen;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class UniqueViolationGuardTests
{
    [Fact]
    public void Wrap_PostgresUniqueViolation_ReturnsFriendlyMessage()
    {
        var inner = new PostgresException("23505");

        var wrapped = UniqueViolationGuard.Wrap(inner, "项目下已存在同名实体 'Order'");

        var exception = Assert.IsType<InvalidOperationException>(wrapped);
        Assert.Equal("项目下已存在同名实体 'Order'", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void Wrap_MySqlDuplicateKey_ReturnsFriendlyMessage()
    {
        var inner = new MySqlException(1062);

        var wrapped = UniqueViolationGuard.Wrap(inner, "项目下已存在同名模块 'sales'");

        var exception = Assert.IsType<InvalidOperationException>(wrapped);
        Assert.Equal("项目下已存在同名模块 'sales'", exception.Message);
    }

    [Fact]
    public void Wrap_SqliteConstraintViolation_ReturnsFriendlyMessage()
    {
        var inner = new SqliteException(19);

        var wrapped = UniqueViolationGuard.Wrap(inner, "项目下已存在同名实体 'Order'");

        Assert.IsType<InvalidOperationException>(wrapped);
    }

    [Theory]
    [InlineData("23514")] // check constraint violation
    [InlineData("42601")] // syntax error
    public void Wrap_NonUniquePostgresError_ReturnsOriginalException(string sqlState)
    {
        var inner = new PostgresException(sqlState);

        var wrapped = UniqueViolationGuard.Wrap(inner, "friendly");

        Assert.Same(inner, wrapped);
    }

    [Fact]
    public void Wrap_MySqlNonDuplicateError_ReturnsOriginalException()
    {
        var inner = new MySqlException(1146);

        var wrapped = UniqueViolationGuard.Wrap(inner, "friendly");

        Assert.Same(inner, wrapped);
    }

    [Fact]
    public void Wrap_UnknownException_ReturnsOriginalException()
    {
        var inner = new InvalidOperationException("boom");

        var wrapped = UniqueViolationGuard.Wrap(inner, "friendly");

        Assert.Same(inner, wrapped);
    }

    [Fact]
    public void Wrap_UniqueViolationNestedInsideWrapper_StillDetected()
    {
        var inner = new InvalidOperationException(
            "wrapper",
            new PostgresException("23505"));

        var wrapped = UniqueViolationGuard.Wrap(inner, "项目下已存在同名实体 'Order'");

        Assert.IsType<InvalidOperationException>(wrapped);
        Assert.Equal("项目下已存在同名实体 'Order'", wrapped.Message);
    }

    private sealed class PostgresException(string sqlState) : Exception
    {
        public string SqlState { get; } = sqlState;
    }

    private sealed class MySqlException(int number) : Exception
    {
        public int Number { get; } = number;
    }

    private sealed class SqliteException(int errorCode) : Exception
    {
        public int SqliteErrorCode { get; } = errorCode;
    }
}
