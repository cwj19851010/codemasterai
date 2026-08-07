using CodeMaster.Application.Services.CodeGen;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class ProjectServicePathTests
{
    [Fact]
    public void NormalizeProcessWorkingDirectory_ReturnsCanonicalExistingPath()
    {
        var source = Environment.GetEnvironmentVariable("TEMP") ?? Directory.GetCurrentDirectory();

        var normalized = ProjectService.NormalizeProcessWorkingDirectory(source);

        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.True(Directory.Exists(normalized));
        if (OperatingSystem.IsWindows() && source.Contains('~'))
            Assert.DoesNotContain("~", normalized);
    }
}
