using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// DISABLED 2026-10-20 (user decision): the Agent never reads or patches CodeMaster source code.
// The whole file is dormant -- nothing registers IAiProjectSourceRepairExecutor and no tool
// exposes it. It is kept so the capability can be restored deliberately if that decision is
// ever revisited; its unit tests still exercise the executor in isolation.
namespace CodeMaster.Agent.Services;

public sealed class AiProjectSourceInspectionRequest
{
    public string? WorkspaceRoot { get; set; }
    public List<string> RelativePaths { get; set; } = [];
}

public sealed class AiProjectSourceInspectionFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public bool Truncated { get; set; }
    public string Content { get; set; } = string.Empty;
}

public sealed class AiProjectSourceInspectionResult
{
    public bool Success { get; set; }
    public string WorkspaceRoot { get; set; } = string.Empty;
    public List<AiProjectSourceInspectionFile> Files { get; set; } = [];
}

public sealed class AiProjectSourceRepairFileInput
{
    public string RelativePath { get; set; } = string.Empty;
    public string PreimageSha256 { get; set; } = string.Empty;
    public string UnifiedPatch { get; set; } = string.Empty;
}

public sealed class AiProjectSourceRepairRequest
{
    public long ProjectId { get; set; }
    public long PlanId { get; set; }
    public long TaskId { get; set; }
    public string FailureSignature { get; set; } = string.Empty;
    public string? WorkspaceRoot { get; set; }
    public string ValidationProfile { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public List<string> AffectedCapabilities { get; set; } = [];
    public List<AiProjectSourceRepairFileInput> Files { get; set; } = [];
}

public sealed class AiProjectSourceRepairFileResult
{
    public string RelativePath { get; set; } = string.Empty;
    public string BeforeSha256 { get; set; } = string.Empty;
    public string AfterSha256 { get; set; } = string.Empty;
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
}

public sealed class AiProjectSourceRepairResult
{
    public bool Applied { get; set; }
    public string FailureSignature { get; set; } = string.Empty;
    public bool ValidationPassed { get; set; }
    public string RepairId { get; set; } = string.Empty;
    public string WorkspaceRoot { get; set; } = string.Empty;
    public string ValidationProfile { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public string? ValidationOutput { get; set; }
    public string? Failure { get; set; }
    public string? DiffSummary { get; set; }
    public List<AiProjectSourceRepairFileResult> Files { get; set; } = [];
    public List<string> AffectedCapabilities { get; set; } = [];
}

public sealed class AiProjectValidationResult
{
    public bool Passed { get; init; }
    public string Output { get; init; } = string.Empty;
}

public interface IAiProjectValidationRunner
{
    Task<AiProjectValidationResult> RunAsync(string workspaceRoot, string profile, CancellationToken cancellationToken = default);
}

public interface IAiProjectSourceRepairExecutor
{
    Task<AiProjectSourceInspectionResult> InspectAsync(
        AiProjectSourceInspectionRequest input,
        CancellationToken cancellationToken = default);

    Task<AiProjectSourceRepairResult> ApplyAsync(
        AiProjectSourceRepairRequest input,
        CancellationToken cancellationToken = default);
}

public sealed class AiProjectSourceRepairExecutor : IAiProjectSourceRepairExecutor
{
    private const int MaximumInspectionFiles = 10;
    private const int MaximumRepairFiles = 5;
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumPatchBytes = 512 * 1024;
    private static readonly SemaphoreSlim RepairGate = new(1, 1);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex HunkHeader = new(
        "^@@ -(\\d+)(?:,(\\d+))? \\+(\\d+)(?:,(\\d+))? @@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly AiProjectRepairOptions _options;
    private readonly IAiAuthorizationService _authorizationService;
    private readonly IHostEnvironment? _hostEnvironment;
    private readonly IAiProjectValidationRunner _validationRunner;

    public AiProjectSourceRepairExecutor(
        IOptions<AiProjectRepairOptions> options,
        IAiAuthorizationService authorizationService,
        IAiProjectValidationRunner? validationRunner = null,
        IHostEnvironment? hostEnvironment = null)
    {
        _options = options.Value;
        _authorizationService = authorizationService;
        _validationRunner = validationRunner ?? new AiProjectValidationRunner();
        _hostEnvironment = hostEnvironment;
    }

    public async Task<AiProjectSourceInspectionResult> InspectAsync(
        AiProjectSourceInspectionRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await DemandEnabledAsync();
        var root = ResolveWorkspaceRoot(input.WorkspaceRoot);
        var paths = NormalizeDistinctPaths(input.RelativePaths, MaximumInspectionFiles);
        var result = new AiProjectSourceInspectionResult { Success = true, WorkspaceRoot = root };

        foreach (var relativePath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveSourcePath(root, relativePath);
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length > MaximumFileBytes)
                throw new InvalidOperationException($"Source inspection refuses files larger than {MaximumFileBytes} bytes: {relativePath}.");

            var content = DecodeSource(bytes, relativePath);
            result.Files.Add(new AiProjectSourceInspectionFile
            {
                RelativePath = relativePath,
                Sha256 = Hash(bytes),
                ByteLength = bytes.LongLength,
                Truncated = content.Length > 64_000,
                Content = RedactForDiagnostics(content[..Math.Min(content.Length, 64_000)]) ?? string.Empty
            });
        }

        return result;
    }

    public async Task<AiProjectSourceRepairResult> ApplyAsync(
        AiProjectSourceRepairRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await DemandEnabledAsync();
        var root = ResolveWorkspaceRoot(input.WorkspaceRoot);
        if (input.Files is not { Count: > 0 and <= MaximumRepairFiles })
            throw new InvalidOperationException($"A source repair must contain between 1 and {MaximumRepairFiles} files.");

        var profile = ResolveValidationProfile(input.ValidationProfile);
        var repairId = CreateRepairId(input);
        var changes = new List<PendingChange>();
        foreach (var file in input.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizePath(file.RelativePath);
            var path = ResolveSourcePath(root, relativePath);
            var patch = ValidatePatch(relativePath, file.UnifiedPatch);
            var beforeBytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var beforeHash = Hash(beforeBytes);
            if (!string.Equals(beforeHash, file.PreimageSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Source repair refused because the preimage hash changed: {relativePath}.");

            var before = DecodeSource(beforeBytes, relativePath);
            var after = ApplyUnifiedPatch(before, patch, relativePath);
            var afterBytes = EncodeSource(after, beforeBytes);
            changes.Add(new PendingChange(
                relativePath,
                path,
                beforeBytes,
                afterBytes,
                beforeHash,
                Hash(afterBytes),
                CountPatchLines(patch, '+'),
                CountPatchLines(patch, '-')));
        }

        await RepairGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var change in changes)
            {
                var currentHash = Hash(await File.ReadAllBytesAsync(change.FullPath, cancellationToken));
                if (!string.Equals(currentHash, change.BeforeHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Source repair refused because the preimage hash changed during apply: {change.RelativePath}.");
            }

            var written = new List<PendingChange>();
            try
            {
                foreach (var change in changes)
                {
                    await File.WriteAllBytesAsync(change.FullPath, change.AfterBytes, cancellationToken);
                    written.Add(change);
                }
            }
            catch
            {
                foreach (var change in written)
                {
                    var current = await File.ReadAllBytesAsync(change.FullPath, CancellationToken.None);
                    if (string.Equals(Hash(current), change.AfterHash, StringComparison.OrdinalIgnoreCase))
                        await File.WriteAllBytesAsync(change.FullPath, change.BeforeBytes, CancellationToken.None);
                }
                throw;
            }
        }
        finally
        {
            RepairGate.Release();
        }

        var validation = await _validationRunner.RunAsync(root, profile, cancellationToken);
        return new AiProjectSourceRepairResult
        {
            Applied = true,
            FailureSignature = input.FailureSignature,
            ValidationPassed = validation.Passed,
            RepairId = repairId,
            WorkspaceRoot = root,
            ValidationProfile = profile,
            Rationale = TrimText(RedactForDiagnostics(input.Rationale), 2000),
            ValidationOutput = RedactForDiagnostics(TrimOutput(validation.Output)),
            DiffSummary = string.Join(", ", changes.Select(item =>
                $"{item.RelativePath} (+{item.AddedLines}/-{item.RemovedLines}, {item.BeforeHash[..12]}->{item.AfterHash[..12]})")),
            Files = changes.Select(item => new AiProjectSourceRepairFileResult
            {
                RelativePath = item.RelativePath,
                BeforeSha256 = item.BeforeHash,
                AfterSha256 = item.AfterHash,
                AddedLines = item.AddedLines,
                RemovedLines = item.RemovedLines
            }).ToList(),
            AffectedCapabilities = input.AffectedCapabilities.Take(20)
                .Select(item => TrimText(RedactForDiagnostics(item), 100))
                .Where(item => item.Length > 0)
                .ToList()
        };
    }

    private async Task DemandEnabledAsync()
    {
        if (!_options.SourceRepairEnabled || !IsRepairEnvironment())
            throw new InvalidOperationException("CodeMaster source repair is disabled outside a Development/Test environment.");
        if (_options.AllowedWorkspaceRoots.Length == 0)
            throw new InvalidOperationException("CodeMaster source repair has no allowed workspace root configured.");
        await _authorizationService.DemandAnyAsync("codegen:project:repairSource");
    }

    private string ResolveWorkspaceRoot(string? requestedRoot)
    {
        var candidates = _options.AllowedWorkspaceRoots
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var root = string.IsNullOrWhiteSpace(requestedRoot)
            ? candidates.Length == 1 ? candidates[0] : throw new InvalidOperationException("Specify one of the configured CodeMaster workspace roots.")
            : Path.GetFullPath(requestedRoot);
        if (!candidates.Any(item => PathsEqual(item, root)))
            throw new InvalidOperationException("The requested workspace root is not allowlisted for CodeMaster source repair.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The allowlisted workspace root does not exist: {root}");
        return root;
    }

    private static string ResolveSourcePath(string root, string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(item => item is "." or ".."))
            throw new InvalidOperationException("Path traversal is not allowed in a source repair.");
        var path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The source repair path is outside the configured workspace root.");
        if (IsForbiddenPath(relativePath))
            throw new InvalidOperationException($"The source repair path is generated, secret, binary, or configuration output: {relativePath}");
        if (!File.Exists(path))
            throw new FileNotFoundException("The source repair target does not exist.", path);
        var info = new FileInfo(path);
        if (info.LinkTarget != null)
            throw new InvalidOperationException($"Symlink targets are not allowed in source repairs: {relativePath}");
        return path;
    }

    private static bool IsForbiddenPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var extension = Path.GetExtension(fileName);
        if (segments.Any(item => item.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                 item.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                 item.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                                 item.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
                                 item.Equals("generated", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("launchsettings.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".user", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.EndsWith(".auto.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".tree.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".fields.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".script.json", StringComparison.OrdinalIgnoreCase))
            return true;
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".db", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveValidationProfile(string requested)
    {
        var profile = string.IsNullOrWhiteSpace(requested)
            ? _options.ValidationProfiles.FirstOrDefault()
            : requested.Trim();
        if (string.IsNullOrWhiteSpace(profile) || !_options.ValidationProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested validation profile is not enabled by the server.");
        return profile;
    }

    private static List<string> NormalizeDistinctPaths(IEnumerable<string>? paths, int maximum)
    {
        var normalized = (paths ?? []).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0 || normalized.Count > maximum)
            throw new InvalidOperationException($"Provide between 1 and {maximum} source paths.");
        return normalized;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Source path is required.");
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidOperationException("Source repair paths must be repository-relative.");
        return normalized;
    }

    private static string ValidatePatch(string relativePath, string patch)
    {
        if (string.IsNullOrWhiteSpace(patch) || StrictUtf8.GetByteCount(patch) > MaximumPatchBytes)
            throw new InvalidOperationException("The source repair patch is empty or too large.");
        if (patch.Contains('\0') || patch.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase) ||
            patch.Contains("rename from", StringComparison.OrdinalIgnoreCase) ||
            patch.Contains("rename to", StringComparison.OrdinalIgnoreCase) ||
            patch.Contains("new file mode", StringComparison.OrdinalIgnoreCase) ||
            patch.Contains("deleted file mode", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Binary, rename, create, and delete patches are not allowed.");

        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var oldHeader = lines.FirstOrDefault(item => item.StartsWith("--- ", StringComparison.Ordinal));
        var newHeader = lines.FirstOrDefault(item => item.StartsWith("+++ ", StringComparison.Ordinal));
        var expectedOld = $"--- a/{relativePath}";
        var expectedNew = $"+++ b/{relativePath}";
        if (oldHeader == null || newHeader == null ||
            lines.Count(item => item.StartsWith("--- ", StringComparison.Ordinal)) != 1 ||
            lines.Count(item => item.StartsWith("+++ ", StringComparison.Ordinal)) != 1 ||
            !oldHeader.Split('\t')[0].Equals(expectedOld, StringComparison.Ordinal) ||
            !newHeader.Split('\t')[0].Equals(expectedNew, StringComparison.Ordinal))
            throw new InvalidOperationException($"The unified patch must target exactly {relativePath}.");
        return patch.Replace("\r\n", "\n");
    }

    private static string ApplyUnifiedPatch(string source, string patch, string relativePath)
    {
        var hadFinalNewline = source.EndsWith('\n');
        var sourceLines = source.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        if (source.Length == 0)
            sourceLines = [];
        var patchLines = patch.Split('\n');
        var output = new List<string>();
        var sourceIndex = 0;
        var hunkFound = false;
        for (var i = 0; i < patchLines.Length; i++)
        {
            var match = HunkHeader.Match(patchLines[i]);
            if (!match.Success)
                continue;
            hunkFound = true;
            var oldStart = int.Parse(match.Groups[1].Value) - 1;
            if (oldStart < 0 || oldStart > sourceLines.Count)
                throw new InvalidOperationException($"Unified patch line range is invalid for {relativePath}.");
            while (sourceIndex < oldStart)
            {
                if (sourceIndex >= sourceLines.Count)
                    throw new InvalidOperationException($"Unified patch line range is invalid for {relativePath}.");
                output.Add(sourceLines[sourceIndex++]);
            }
            var oldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
            var consumed = 0;
            for (i++; i < patchLines.Length && !HunkHeader.IsMatch(patchLines[i]); i++)
            {
                var line = patchLines[i];
                if (line.StartsWith("\\ No newline", StringComparison.Ordinal))
                    continue;
                if (line.Length == 0 && i == patchLines.Length - 1)
                    continue;
                if (line.Length == 0)
                    throw new InvalidOperationException($"Malformed unified patch for {relativePath}.");
                switch (line[0])
                {
                    case ' ':
                        EnsureContext(sourceLines, sourceIndex, line[1..], relativePath);
                        output.Add(sourceLines[sourceIndex++]);
                        consumed++;
                        break;
                    case '-':
                        EnsureContext(sourceLines, sourceIndex, line[1..], relativePath);
                        sourceIndex++;
                        consumed++;
                        break;
                    case '+':
                        output.Add(line[1..]);
                        break;
                    default:
                        throw new InvalidOperationException($"Malformed unified patch for {relativePath}.");
                }
            }
            if (consumed != oldCount)
                throw new InvalidOperationException($"Unified patch context count does not match {relativePath}.");
            i--;
        }
        if (!hunkFound)
            throw new InvalidOperationException($"The unified patch contains no hunks for {relativePath}.");
        while (sourceIndex < sourceLines.Count)
            output.Add(sourceLines[sourceIndex++]);
        var result = string.Join('\n', output);
        return hadFinalNewline || patch.EndsWith('\n') ? result + '\n' : result;
    }

    private static void EnsureContext(IReadOnlyList<string> sourceLines, int index, string expected, string path)
    {
        if (index >= sourceLines.Count || !string.Equals(sourceLines[index], expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unified patch context no longer matches {path}.");
    }

    private static string DecodeSource(byte[] bytes, string path)
    {
        try
        {
            return StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"Binary or non-UTF8 source files are not supported: {path}.");
        }
    }

    private static byte[] EncodeSource(string content, byte[] original)
    {
        var encoded = StrictUtf8.GetBytes(content);
        return original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
            ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(encoded).ToArray()
            : encoded;
    }

    private static int CountPatchLines(string patch, char marker)
        => patch.Replace("\r\n", "\n").Split('\n')
            .Count(line => line.Length > 0 && line[0] == marker &&
                           !line.StartsWith("+++ ", StringComparison.Ordinal) &&
                           !line.StartsWith("--- ", StringComparison.Ordinal));

    private static string CreateRepairId(AiProjectSourceRepairRequest input)
    {
        var material = string.Join('\n', input.Files.Select(item =>
            $"{item.RelativePath}|{item.PreimageSha256}|{item.UnifiedPatch}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string? RedactForDiagnostics(string? value)
        => AiProjectFailureEvidenceBuilder.RedactForTest(value);

    private static string TrimOutput(string output)
        => string.IsNullOrWhiteSpace(output) ? string.Empty : output.Length <= 8000 ? output : output[..8000];

    private static string TrimText(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= maximumLength ? value.Trim() : value[..maximumLength].Trim();

    private bool IsRepairEnvironment()
        => _hostEnvironment?.IsDevelopment() == true || _hostEnvironment?.IsEnvironment("Test") == true;

    private static bool PathsEqual(string left, string right)
        => string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private sealed record PendingChange(
        string RelativePath,
        string FullPath,
        byte[] BeforeBytes,
        byte[] AfterBytes,
        string BeforeHash,
        string AfterHash,
        int AddedLines,
        int RemovedLines);
}

internal sealed class AiProjectValidationRunner : IAiProjectValidationRunner
{
    private static readonly IReadOnlyDictionary<string, (string FileName, string[] Arguments, string? RelativeWorkingDirectory)> Profiles =
        new Dictionary<string, (string, string[], string?)>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent-regression"] = ("dotnet", ["test", "CodeMaster.Agent.Tests/CodeMaster.Agent.Tests.csproj", "-c", "Release", "--no-restore"], null),
            ["codegen-regression"] = ("dotnet", ["test", "CodeMaster.CodeGenerator.Tests/CodeMaster.CodeGenerator.Tests.csproj", "-c", "Release", "--no-restore"], null),
            ["webapi-build"] = ("dotnet", ["build", "CodeMaster.WebApi/CodeMaster.WebApi.csproj", "-c", "Release", "--no-restore"], null),
            ["vue-build"] = (OperatingSystem.IsWindows() ? "npm.cmd" : "npm", ["run", "build"], "CodeMaster.Vue")
        };

    public AiProjectValidationRunner()
    {
    }

    public async Task<AiProjectValidationResult> RunAsync(string workspaceRoot, string profile, CancellationToken cancellationToken = default)
    {
        if (!Profiles.TryGetValue(profile, out var command))
            throw new InvalidOperationException($"Validation profile '{profile}' is not defined by CodeMaster.");
        var workingDirectory = string.IsNullOrWhiteSpace(command.RelativeWorkingDirectory)
            ? workspaceRoot
            : Path.Combine(workspaceRoot, command.RelativeWorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            return new AiProjectValidationResult { Passed = false, Output = $"Validation directory does not exist: {workingDirectory}" };

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start validation profile '{profile}'.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var stdout = ReadLimitedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadLimitedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new AiProjectValidationResult { Passed = false, Output = "Validation profile timed out after 10 minutes." };
        }
        var output = (await stdout) + Environment.NewLine + (await stderr);
        return new AiProjectValidationResult { Passed = process.ExitCode == 0, Output = output };
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumOutput = 16_000;
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;
            if (output.Length < maximumOutput)
                output.Append(buffer, 0, Math.Min(read, maximumOutput - output.Length));
        }
        return output.ToString();
    }
}
