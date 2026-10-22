using CodeMaster.LocalAgent.Services;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalCodegenExecutionServiceMigrationTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ShouldCreateMigration_UsesEfPendingModelChangesExitCode(
        int exitCode,
        bool expected)
    {
        Assert.Equal(expected, LocalCodegenExecutionService.ShouldCreateMigration(exitCode));
    }

    [Fact]
    public void ShouldCreateMigration_RejectsUnexpectedExitCode()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LocalCodegenExecutionService.ShouldCreateMigration(2));
    }
}
