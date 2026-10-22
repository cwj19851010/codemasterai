using System.Security.Cryptography;
using System.Text;
using CodeMaster.Agent.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiProjectSourceRepairExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        FindWorkspaceRoot(),
        ".artifacts",
        "agent-test-temp",
        $"codemaster_source_repair_{Guid.NewGuid():N}");

    public AiProjectSourceRepairExecutorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    [Fact]
    public async Task Inspect_and_apply_require_allowlisted_root_and_matching_preimage()
    {
        var file = Path.Combine(_root, "src", "RepairTarget.cs");
        await File.WriteAllTextAsync(file, "public class RepairTarget\n{\n}\n");
        var executor = CreateExecutor(new PassingValidationRunner());
        var before = await File.ReadAllBytesAsync(file);
        var hash = Convert.ToHexString(SHA256.HashData(before));
        var patch = "--- a/src/RepairTarget.cs\n+++ b/src/RepairTarget.cs\n@@ -1,3 +1,4 @@\n public class RepairTarget\n {\n+    public int Value { get; set; }\n }\n";

        var inspected = await executor.InspectAsync(new AiProjectSourceInspectionRequest
        {
            WorkspaceRoot = _root,
            RelativePaths = ["src/RepairTarget.cs"]
        });
        var result = await executor.ApplyAsync(new AiProjectSourceRepairRequest
        {
            WorkspaceRoot = _root,
            ValidationProfile = "test",
            FailureSignature = "failure-1",
            Files =
            [
                new AiProjectSourceRepairFileInput
                {
                    RelativePath = "src/RepairTarget.cs",
                    PreimageSha256 = hash,
                    UnifiedPatch = patch
                }
            ]
        });

        Assert.True(inspected.Success);
        Assert.Equal(hash, inspected.Files[0].Sha256);
        Assert.True(result.Applied);
        Assert.True(result.ValidationPassed);
        Assert.Equal(1, result.Files[0].AddedLines);
        Assert.Contains("Value", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_refuses_stale_preimage_and_forbidden_targets()
    {
        var file = Path.Combine(_root, "src", "RepairTarget.cs");
        await File.WriteAllTextAsync(file, "class RepairTarget {}\n");
        var executor = CreateExecutor(new PassingValidationRunner());
        var patch = "--- a/src/RepairTarget.cs\n+++ b/src/RepairTarget.cs\n@@ -1,1 +1,1 @@\n-class RepairTarget {}\n+class RepairTarget { public int Value { get; set; } }\n";

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ApplyAsync(new AiProjectSourceRepairRequest
        {
            WorkspaceRoot = _root,
            ValidationProfile = "test",
            Files =
            [
                new AiProjectSourceRepairFileInput
                {
                    RelativePath = "src/RepairTarget.cs",
                    PreimageSha256 = "BAD",
                    UnifiedPatch = patch
                }
            ]
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.InspectAsync(new AiProjectSourceInspectionRequest
        {
            WorkspaceRoot = _root,
            RelativePaths = ["appsettings.json"]
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.InspectAsync(new AiProjectSourceInspectionRequest
        {
            WorkspaceRoot = _root,
            RelativePaths = ["../outside.cs"]
        }));
    }

    [Fact]
    public async Task Apply_is_disabled_outside_test_or_development_environment()
    {
        var options = new AiProjectRepairOptions
        {
            SourceRepairEnabled = true,
            AllowedWorkspaceRoots = [_root],
            ValidationProfiles = ["test"]
        };
        var executor = new AiProjectSourceRepairExecutor(
            Options.Create(options),
            new AllowAuthorization(),
            new PassingValidationRunner(),
            new TestHostEnvironment("Production"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.InspectAsync(new AiProjectSourceInspectionRequest
        {
            WorkspaceRoot = _root,
            RelativePaths = ["src/RepairTarget.cs"]
        }));
    }

    private AiProjectSourceRepairExecutor CreateExecutor(IAiProjectValidationRunner runner)
        => new(
            Options.Create(new AiProjectRepairOptions
            {
                SourceRepairEnabled = true,
                AllowedWorkspaceRoots = [_root],
                ValidationProfiles = ["test"]
            }),
            new AllowAuthorization(),
            runner,
            new TestHostEnvironment("Test"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeMaster.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private sealed class AllowAuthorization : IAiAuthorizationService
    {
        public Task<bool> HasAnyAsync(params string[] permissions) => Task.FromResult(true);
        public Task DemandAnyAsync(params string[] permissions) => Task.CompletedTask;
    }

    private sealed class PassingValidationRunner : IAiProjectValidationRunner
    {
        public Task<AiProjectValidationResult> RunAsync(string workspaceRoot, string profile, CancellationToken cancellationToken = default)
            => Task.FromResult(new AiProjectValidationResult { Passed = true, Output = "test profile passed" });
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "CodeMaster.Agent.Tests";
        public string ContentRootPath { get; set; } = CodeMasterTestPaths.Root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
