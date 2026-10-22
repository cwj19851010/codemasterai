using System.Text.Json;
using CodeMaster.LocalAgent.Services;

namespace CodeMaster.LocalAgent.Tests.Services;

public class LocalAgentDiagnosticLogTests
{
    [Fact]
    public async Task WriteAsync_PersistsStructuredEventAndRedactsSecrets()
    {
        var root = Path.Combine(
            CodeMasterTestPaths.Root,
            "CodeMaster_LocalAgent_DiagnosticTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new LocalAgentDiagnosticLog(root);
            await diagnostics.WriteAsync(
                "error",
                "agent.test.failed",
                new Dictionary<string, object?>
                {
                    ["action"] = "agentChat",
                    ["accessToken"] = "token-value",
                    ["message"] = "Authorization=Bearer real-token password=secret-value"
                },
                new InvalidOperationException("connectionString=Server=db;Password=db-secret"));

            var line = await File.ReadAllTextAsync(diagnostics.CurrentLogPath);
            using var document = JsonDocument.Parse(line);
            var rootElement = document.RootElement;

            Assert.Equal("agent.test.failed", rootElement.GetProperty("event").GetString());
            Assert.Equal("error", rootElement.GetProperty("level").GetString());
            Assert.Equal("agentChat", rootElement.GetProperty("fields").GetProperty("action").GetString());
            Assert.Contains("[REDACTED]", line, StringComparison.Ordinal);
            Assert.DoesNotContain("token-value", line, StringComparison.Ordinal);
            Assert.DoesNotContain("real-token", line, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-value", line, StringComparison.Ordinal);
            Assert.DoesNotContain("db-secret", line, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Bearer abc.def.ghi", "Bearer [REDACTED]")]
    [InlineData("https://user:secret@example.com/path", "https://[REDACTED]@example.com/path")]
    [InlineData("apiKey=top-secret", "apiKey=[REDACTED]")]
    public void SanitizeText_RemovesCommonSecretFormats(string input, string expected)
    {
        Assert.Equal(expected, LocalAgentDiagnosticLog.SanitizeText(input));
    }
}
