namespace CodeMaster.CodeGenerator.Tests.Services;

public class TemplateSecurityTests
{
    [Theory]
    [InlineData("CodeMaster.Application/Services/Auth/AuthService.cs")]
    [InlineData("Templates/CodeMaster.Application/Services/Auth/AuthService.cs")]
    public void AuthServiceLogging_DoesNotUsePasswordLogProperty(string relativePath)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            ".."));
        var content = File.ReadAllText(Path.Combine(
            solutionRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.DoesNotContain("Password={Password}", content, StringComparison.Ordinal);
        Assert.Contains("PasswordProvided={PasswordProvided}", content, StringComparison.Ordinal);
    }
}
