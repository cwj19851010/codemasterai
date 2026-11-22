using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 模板配置
/// </summary>
public class TemplateConfig
{
    public string TemplateVersion { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceRoot { get; set; } = ".";
    public IncludeConfig Include { get; set; } = new();
    public List<string> Overlays { get; set; } = new();
    public ExcludeConfig Exclude { get; set; } = new();
    public PrintingConfig Printing { get; set; } = new();
}

/// <summary>
/// 打印能力开关。开源发行版不提供报表，保持 false；
/// 完整版通过 template-config.private.json 打开，导出时会把独立打印运行时
/// 注入生成的项目模板。
/// </summary>
public class PrintingConfig
{
    public bool Enabled { get; set; }
}

public class IncludeConfig
{
    public List<string> Core { get; set; } = new();
    public List<string> Domain { get; set; } = new();
    public List<string> Application { get; set; } = new();
    public List<string> Infrastructure { get; set; } = new();
    public List<string> WebApi { get; set; } = new();
    public List<string> Migrator { get; set; } = new();
    public List<string> Frontend { get; set; } = new();
    public List<string> Root { get; set; } = new();
    public List<string> SourceGenerator { get; set; } = new();
    public List<string> Tests { get; set; } = new();
}

public class ExcludeConfig
{
    public List<string> Migrations { get; set; } = new();
}

/// <summary>
/// 模板导出服务
/// </summary>
public class TemplateExportService
{
    private const string PrintingRuntimePathEnvironmentVariable = "CODEMASTER_PRINTING_RUNTIME_PATH";

    /// <summary>
    /// 记录"当前生效模板包"的文件名。WebApi 构建只拷贝这里指名的那个包，
    /// 避免历史导出的包全部进入 bin/ 与 publish/。
    /// </summary>
    public const string CurrentTemplatePointerFileName = "latest-template.txt";
    private readonly string _solutionRoot;
    private readonly IReadOnlyList<string>? _printingRuntimeSourceRoots;

    public TemplateExportService()
        : this(
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../..")),
            null)
    {
    }

    internal TemplateExportService(
        string solutionRoot,
        IEnumerable<string>? printingRuntimeSourceRoots)
    {
        _solutionRoot = Path.GetFullPath(solutionRoot);
        _printingRuntimeSourceRoots = printingRuntimeSourceRoots?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 加载模板配置
    /// </summary>
    private TemplateConfig LoadTemplateConfig()
    {
        // 完整版把报表等私有能力写在 template-config.private.json（不入库）；
        // 开源发行版只有 template-config.json，天然不含任何私有覆盖项。
        var privateConfigPath = Path.Combine(_solutionRoot, "template-config.private.json");
        var configPath = File.Exists(privateConfigPath)
            ? privateConfigPath
            : Path.Combine(_solutionRoot, "template-config.json");

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"模板配置文件不存在: {configPath}");
        }

        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<TemplateConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return config ?? throw new InvalidOperationException("无法解析模板配置文件");
    }

    /// <summary>
    /// 导出纯净版模板（不包含项目管理功能）
    /// </summary>
    public async Task<string> ExportCleanTemplateAsync(string? outputPath)
    {
        // Keep export scratch data inside the CodeMaster workspace. Some
        // managed Windows environments deny the user TEMP directory, while
        // the workspace is already the intended ownership boundary for
        // CodeMaster-generated artifacts.
        var tempRoot = Path.Combine(_solutionRoot, ".artifacts", "template-export");
        Directory.CreateDirectory(tempRoot);
        var tempDir = Path.Combine(tempRoot, $"CodeMaster_Template_{Guid.NewGuid():N}");

        try
        {
            // 1. 加载配置
            var config = LoadTemplateConfig();
            var sourceDir = ResolveSourceDirectory(config);

            // 2. 创建临时目录
            Directory.CreateDirectory(tempDir);

            // 3. 复制配置中列出的文件
            await CopyIncludedFilesAsync(sourceDir, tempDir, config);
            await ApplyConfiguredOverlaysAsync(tempDir, config);
            if (config.Printing.Enabled)
                await CopyGeneratedProjectRuntimeModulesAsync(tempDir);

            // 4. 在 Program.cs 中删除额外种子数据调用
            await RemoveAdditionalSeedCallAsync(tempDir);

            // 4.5 在 WebApi Program.cs 中删除 CodeGen 相关 using
            await RemoveCodeGenLinesAsync(tempDir);
            await RemovePlatformWebApiProjectReferencesAsync(tempDir);
            await RemoveFrontendMcpReferencesAsync(tempDir);
            await RewriteFrontendViewModuleGlobAsync(tempDir);
            await RemoveFrontendAgentReferencesAsync(tempDir);
            await RemoveTauriBridgeLinesAsync(tempDir);
            await RewriteRequestWithoutClientBridgeAsync(tempDir);
            await RewriteSignalRWithoutClientBridgeAsync(tempDir);
            await RemoveLoginClientBridgeAsync(tempDir);
            await RemoveAgentDbContextReferencesAsync(tempDir);
            await RemoveCodeGenDbContextReferencesAsync(tempDir);

            // 5. 复制 template.sln 并重命名
            await CopySolutionTemplateAsync(sourceDir, tempDir);
            ValidateCleanTemplateDirectory(sourceDir, tempDir, config);

            // 6. 确定输出目录（如果未指定，使用默认的 Templates 目录）
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = Path.Combine(_solutionRoot, "Templates");
            }

            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            // 7. 压缩为zip文件
            var zipPath = Path.Combine(outputPath, $"CodeMaster_Template_{DateTime.UtcNow:yyyyMMddHHmmss}.zip");
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(tempDir, zipPath, CompressionLevel.Optimal, false);

            // Record which package is the current one. The WebApi build only ships
            // the package named here, so accumulated historical exports no longer
            // land in bin/ and publish/ (they used to cost several GB per build).
            await WriteCurrentTemplatePointerAsync(outputPath, Path.GetFileName(zipPath));

            return zipPath;
        }
        finally
        {
            // 清理临时目录
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// 写入"当前生效模板包"指针文件（内容是包文件名）。
    /// </summary>
    private static async Task WriteCurrentTemplatePointerAsync(string outputPath, string fileName)
    {
        var pointerPath = Path.Combine(outputPath, CurrentTemplatePointerFileName);
        await File.WriteAllTextAsync(pointerPath, fileName, new UTF8Encoding(false));
    }

    /// <summary>
    /// 复制配置中列出的文件
    /// </summary>
    private async Task CopyIncludedFilesAsync(string sourceDir, string destDir, TemplateConfig config)    {
        var allFiles = new List<string>();
        allFiles.AddRange(config.Include.Core);
        allFiles.AddRange(config.Include.Domain);
        allFiles.AddRange(config.Include.Application);
        allFiles.AddRange(config.Include.Infrastructure);
        allFiles.AddRange(config.Include.WebApi);
        allFiles.AddRange(config.Include.Migrator);
        allFiles.AddRange(config.Include.Frontend);
        allFiles.AddRange(config.Include.Root);
        allFiles.AddRange(config.Include.SourceGenerator);
        allFiles.AddRange(config.Include.Tests);

        foreach (var relativePath in allFiles)
        {
            if (relativePath.Contains("*"))
            {
                // 处理通配符（支持 **/*.cs 和 *.cs）
                var files = ExpandWildcard(sourceDir, relativePath);
                if (files.Count == 0)
                {
                    throw new FileNotFoundException(
                        $"模板 include 通配符没有匹配到文件: {relativePath} (sourceRoot: {sourceDir})");
                }
                foreach (var file in files)
                {
                    var relPath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');

                    // 检查是否在排除列表中
                    bool shouldExclude = ShouldExcludeTemplateFile(relPath, config);

                    if (!shouldExclude)
                    {
                        CopyFileWithDirectory(file, sourceDir, destDir);
                    }
                }
            }
            else
            {
                // 精确路径
                var sourceFile = Path.Combine(sourceDir, relativePath);
                if (!File.Exists(sourceFile))
                {
                    throw new FileNotFoundException(
                        $"模板 include 文件不存在: {relativePath} (sourceRoot: {sourceDir})",
                        sourceFile);
                }

                if (!ShouldExcludeTemplateFile(relativePath, config))
                {
                    CopyFileWithDirectory(sourceFile, sourceDir, destDir);
                }
            }
        }

        await Task.CompletedTask;
    }

    private string ResolveSourceDirectory(TemplateConfig config)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(config.SourceRoot)
            ? "."
            : config.SourceRoot.Trim();
        var sourceDir = Path.GetFullPath(Path.IsPathRooted(configuredRoot)
            ? configuredRoot
            : Path.Combine(_solutionRoot, configuredRoot));
        var solutionRoot = _solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var solutionPrefix = solutionRoot + Path.DirectorySeparatorChar;
        if (!sourceDir.Equals(solutionRoot, StringComparison.OrdinalIgnoreCase) &&
            !sourceDir.StartsWith(solutionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"模板 sourceRoot 必须位于解决方案目录内: {sourceDir}");
        }

        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"模板 sourceRoot 不存在: {sourceDir}");

        return sourceDir;
    }

    private async Task ApplyConfiguredOverlaysAsync(string tempDir, TemplateConfig config)
    {
        var overlayRoot = Path.Combine(_solutionRoot, "Templates");
        foreach (var relativePath in config.Overlays.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            if (normalized.Contains("../", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
                throw new InvalidOperationException($"模板 overlay 路径无效: {relativePath}");

            var sourcePath = Path.GetFullPath(Path.Combine(
                overlayRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            var overlayPrefix = Path.GetFullPath(overlayRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!sourcePath.StartsWith(overlayPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"模板 overlay 路径无效: {relativePath}");

            // overlay 从 Templates 基线目录读取。报表等私有能力在开源发行版里
            // 不随基线提供，此时跳过该文件而不是中断整个模板导出。
            if (!File.Exists(sourcePath))
            {
                Console.WriteLine($"  - 跳过缺失的模板 overlay: {relativePath}");
                continue;
            }

            var targetPath = Path.Combine(tempDir, normalized.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        await Task.CompletedTask;
    }

    private void ValidateCleanTemplateDirectory(string sourceDir, string tempDir, TemplateConfig config)
    {
        var allowedFiles = GetConfiguredIncludeFiles(sourceDir, config)
            .Select(path => Path.GetRelativePath(sourceDir, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        allowedFiles.UnionWith(config.Overlays.Select(path => path.Replace('\\', '/').TrimStart('/')));
        allowedFiles.Add("CodeMaster.sln");

        var unexpectedFiles = Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("modules/", StringComparison.OrdinalIgnoreCase))
            .Where(path => !allowedFiles.Contains(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unexpectedFiles.Count > 0)
        {
            throw new InvalidOperationException(
                $"纯净模板包含不在 template-config.json include 白名单中的文件: {string.Join(", ", unexpectedFiles)}");
        }

        var forbiddenDirectories = new[]
        {
            "CodeMaster.Agent",
            "CodeMaster.CodeGenerator",
            "CodeMaster.LocalAgent",
            "CodeMaster.McpServer",
            "CodeMaster.Private",
            "CodeMaster.Vue/src-tauri"
        };
        var forbiddenPath = allowedFiles.FirstOrDefault(path => forbiddenDirectories.Any(directory =>
            path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)));
        if (forbiddenPath != null)
            throw new InvalidOperationException($"纯净模板 include 包含平台专用文件: {forbiddenPath}");

        // 报表能力属于高级版：只有打开 printing.enabled 的完整版才要求这些文件
        if (config.Printing.Enabled)
        {
            RequireFile(tempDir, "modules/CM.Printing.Runtime.dll");
            RequireFile(tempDir, "modules/CM.Printing.Migrations.dll");
            RequireFile(tempDir, "CodeMaster.Migrator/SeedData/CodeGen/PrintTemplateModule.cs");
            RequireFile(tempDir, "CodeMaster.Vue/src/views/codegen/print/preview.vue");
            RequireFile(tempDir, "CodeMaster.Vue/src/views/codegen/printTemplate/index.vue");
        }

        var packageJson = ReadTemplateText(tempDir, "CodeMaster.Vue/package.json");
        var dashboard = ReadTemplateText(tempDir, "CodeMaster.Vue/src/views/dashboard/index.vue");
        var migratorProgram = ReadTemplateText(tempDir, "CodeMaster.Migrator/Program.cs");
        var readme = ReadTemplateText(tempDir, "README.md");
        var settings = ReadTemplateText(tempDir, "CodeMaster.WebApi/appsettings.json");
        var developmentSettings = ReadTemplateText(tempDir, "CodeMaster.WebApi/appsettings.Development.json");

        if (packageJson.Contains("@tauri-apps", StringComparison.OrdinalIgnoreCase) ||
            packageJson.Contains("sidecar:publish", StringComparison.OrdinalIgnoreCase) ||
            packageJson.Contains("build:full", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("纯净模板前端 package.json 包含平台客户端依赖或脚本。");
        }

        if (dashboard.Contains("/codegen/project", StringComparison.OrdinalIgnoreCase) ||
            migratorProgram.Contains("Name = \"代码管理\"", StringComparison.Ordinal) ||
            migratorProgram.Contains("new ProjectModule()", StringComparison.Ordinal) ||
            migratorProgram.Contains("new ModuleEntityModule()", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("纯净模板包含 CodeMaster 平台代码管理入口。");
        }

        if (readme.Contains("MCP", StringComparison.OrdinalIgnoreCase) ||
            readme.Contains("Tauri", StringComparison.OrdinalIgnoreCase) ||
            readme.Contains("LocalAgent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("纯净模板 README 包含 CodeMaster 平台客户端说明。");
        }

        var unsafeSettings = settings + Environment.NewLine + developmentSettings;
        if (unsafeSettings.Contains("tauri.localhost", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                unsafeSettings,
                @"(?i)(Password|Pwd)\s*=\s*(?!your_mysql_password|CHANGE_ME|\*{3})[^;\""\r\n]+"))
        {
            throw new InvalidOperationException("纯净模板配置包含平台客户端地址或非占位数据库密码。");
        }
    }

    private IReadOnlyList<string> GetConfiguredIncludeFiles(string sourceDir, TemplateConfig config)
    {
        var configuredPaths = new List<string>();
        configuredPaths.AddRange(config.Include.Core);
        configuredPaths.AddRange(config.Include.Domain);
        configuredPaths.AddRange(config.Include.Application);
        configuredPaths.AddRange(config.Include.Infrastructure);
        configuredPaths.AddRange(config.Include.WebApi);
        configuredPaths.AddRange(config.Include.Migrator);
        configuredPaths.AddRange(config.Include.Frontend);
        configuredPaths.AddRange(config.Include.Root);
        configuredPaths.AddRange(config.Include.SourceGenerator);
        configuredPaths.AddRange(config.Include.Tests);

        return configuredPaths
            .SelectMany(path => path.Contains('*')
                ? ExpandWildcard(sourceDir, path)
                : [Path.Combine(sourceDir, path)])
            .Where(File.Exists)
            .Where(path => !ShouldExcludeTemplateFile(
                Path.GetRelativePath(sourceDir, path).Replace('\\', '/'),
                config))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void RequireFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException($"纯净模板缺少必需文件: {relativePath}");
    }

    private static string ReadTemplateText(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>
    /// Copies the independent printing runtime into the generated-project template.
    /// Platform host assemblies are deliberately excluded because generated projects
    /// rename their host projects and must remain independent from CodeMaster.
    /// </summary>
    internal async Task<int> CopyGeneratedProjectRuntimeModulesAsync(string tempDir)
    {
        var sourceRoots = ResolvePrintingRuntimeSourceRoots();
        var runtimeFiles = CollectPrintingRuntimeFiles(sourceRoots);
        if (!runtimeFiles.Any(file => string.Equals(file.RelativePath, "CM.Printing.Runtime.dll", StringComparison.OrdinalIgnoreCase)) ||
            !runtimeFiles.Any(file => string.Equals(file.RelativePath, "CM.Printing.Migrations.dll", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "生成模板缺少完整打印运行时。请先构建 CodeMaster.Private/Printing.LocalAgent，" +
                $"或设置 {PrintingRuntimePathEnvironmentVariable} 指向包含 CM.Printing.Runtime.dll 与 CM.Printing.Migrations.dll 的目录。");
        }

        var modulesDirectory = Path.Combine(tempDir, "modules");
        Directory.CreateDirectory(modulesDirectory);
        foreach (var runtimeFile in runtimeFiles)
        {
            var targetPath = Path.Combine(
                modulesDirectory,
                runtimeFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var source = File.OpenRead(runtimeFile.SourcePath);
            await using var target = File.Create(targetPath);
            await source.CopyToAsync(target);
        }

        return runtimeFiles.Count;
    }

    private IReadOnlyList<string> ResolvePrintingRuntimeSourceRoots()
    {
        if (_printingRuntimeSourceRoots is { Count: > 0 })
            return _printingRuntimeSourceRoots;

        var configuredPath = Environment.GetEnvironmentVariable(PrintingRuntimePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return [Path.GetFullPath(configuredPath)];

        var hostedBundle = Path.Combine(
            AppContext.BaseDirectory,
            "modules",
            "CodeMaster.PrintingRuntime");
        if (HasCompletePrintingRuntime([hostedBundle]))
            return [hostedBundle];

        var configurations = AppContext.BaseDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
            ? new[] { "Release", "Debug" }
            : new[] { "Debug", "Release" };
        foreach (var configuration in configurations)
        {
            var roots = new[]
            {
                Path.Combine(_solutionRoot, "CodeMaster.Private", "Printing.Runtime", "bin", configuration, "net10.0"),
                Path.Combine(_solutionRoot, "CodeMaster.Private", "Printing.Migrations", "bin", configuration, "net10.0")
            };
            if (HasCompletePrintingRuntime(roots))
                return roots;
        }

        return Array.Empty<string>();
    }

    private static bool HasCompletePrintingRuntime(IEnumerable<string> sourceRoots)
    {
        var existingRoots = sourceRoots.Where(Directory.Exists).ToArray();
        return existingRoots.Any(root => File.Exists(Path.Combine(root, "CM.Printing.Runtime.dll"))) &&
               existingRoots.Any(root => File.Exists(Path.Combine(root, "CM.Printing.Migrations.dll")));
    }

    private static IReadOnlyList<RuntimeTemplateFile> CollectPrintingRuntimeFiles(IEnumerable<string> sourceRoots)
    {
        var files = new Dictionary<string, RuntimeTemplateFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceRoot in sourceRoots.Where(Directory.Exists))
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!IsPrintingRuntimeFile(relativePath))
                    continue;

                files[relativePath] = new RuntimeTemplateFile(sourcePath, relativePath);
            }
        }

        return files.Values
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPrintingRuntimeFile(string relativePath)
    {
        if (relativePath.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(".playwright/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = Path.GetFileName(relativePath);
        if (fileName.Equals("CM.Printing.Runtime.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("CM.Printing.Runtime.deps.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("CM.Printing.Migrations.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("CM.Printing.Migrations.deps.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("playwright.ps1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(fileName).Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
               !fileName.StartsWith("CodeMaster.", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RuntimeTemplateFile(string SourcePath, string RelativePath);

    /// <summary>
    /// 展开通配符模式，支持 **/*.cs 和 *.cs
    /// </summary>
    private static bool ShouldExcludeTemplateFile(string relativePath, TemplateConfig config)
    {
        var relPath = relativePath.Replace('\\', '/');

        // Generated projects create a fresh migration during initialization based on the
        // selected database provider. Historical EF migrations should not be shipped in templates.
        if (relPath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) &&
            relPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return config.Exclude.Migrations.Any(pattern => PathMatchesPattern(relPath, pattern));
    }

    private static bool PathMatchesPattern(string relativePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var normalizedPattern = pattern.Replace('\\', '/');
        if (!normalizedPattern.Contains('*'))
            return string.Equals(relativePath, normalizedPattern, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", @"[^/]*") + "$";

        return Regex.IsMatch(relativePath, regex, RegexOptions.IgnoreCase);
    }

    private List<string> ExpandWildcard(string baseDir, string pattern)
    {
        var result = new List<string>();

        // 分离目录和文件模式
        var parts = pattern.Split('/');
        var currentDir = baseDir;
        var searchPattern = "*";
        var searchOption = SearchOption.TopDirectoryOnly;

        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "**")
            {
                // 递归搜索
                searchOption = SearchOption.AllDirectories;
            }
            else if (parts[i].Contains("*"))
            {
                // 文件模式
                searchPattern = parts[i];
            }
            else
            {
                // 普通目录
                currentDir = Path.Combine(currentDir, parts[i]);
            }
        }

        if (Directory.Exists(currentDir))
        {
            var files = Directory.GetFiles(currentDir, searchPattern, searchOption);
            result.AddRange(files);
        }

        return result;
    }

    /// <summary>
    /// 复制文件并保持目录结构（移除 BOM）
    /// </summary>
    private void CopyFileWithDirectory(string sourceFile, string sourceRoot, string destRoot)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
        var destFile = Path.Combine(destRoot, relativePath);
        var destDir = Path.GetDirectoryName(destFile);

        if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        // 对于文本文件，移除 BOM；对于二进制文件，直接复制
        var extension = Path.GetExtension(sourceFile).ToLower();
        var textExtensions = new[] { ".cs", ".json", ".js", ".vue", ".ts", ".tsx", ".html", ".css", ".scss", ".xml", ".config", ".txt", ".md", ".sln", ".csproj" };

        if (textExtensions.Contains(extension))
        {
            try
            {
                // 读取文件内容（自动处理 BOM）
                var content = File.ReadAllText(sourceFile);
                // 写入文件（使用 UTF-8 without BOM）
                File.WriteAllText(destFile, content, new UTF8Encoding(false));
            }
            catch
            {
                // 如果读取失败（可能是二进制文件），直接复制
                File.Copy(sourceFile, destFile, true);
            }
        }
        else
        {
            // 二进制文件直接复制
            File.Copy(sourceFile, destFile, true);
        }
    }

    /// <summary>
    /// 在 WebApi Program.cs 中删除 CodeGen 相关行
    /// </summary>
    internal async Task RemoveCodeGenLinesAsync(string tempDir)
    {
        var programPath = Path.Combine(tempDir, "CodeMaster.WebApi", "Program.cs");
        if (File.Exists(programPath))
        {
            var codeGenTypes = new[]
            {
                "CodeGen",
                "OneToManyRelation",
                "GenTable",
                "GenTableColumn",
                "IEmailSender",
                "EmailSender",
                "IPublicAccountService",
                "PublicAccountService",
                "Application.Services.Community",
                "McpToken",
                "CodeMaster.Agent.Extensions",
                "AddCodeMasterAgent"
            };
            var utf8WithoutBom = new UTF8Encoding(false);
            var lines = await File.ReadAllLinesAsync(programPath, utf8WithoutBom);
            var result = lines.Where(line => !codeGenTypes.Any(t => line.Contains(t))).ToList();
            await File.WriteAllLinesAsync(programPath, result, utf8WithoutBom);
        }

        // Migrator 的 Program.cs 同样引用了 SeedData.CodeGen（项目/模块/报表种子），
        // 而生成的模板并不包含该目录（include 只列 Core/Monitor/System），
        // 不清理会导致生成的项目编译失败：CS0234 找不到 SeedData.CodeGen。
        var migratorProgramPath = Path.Combine(tempDir, "CodeMaster.Migrator", "Program.cs");
        if (File.Exists(migratorProgramPath))
        {
            var codeGenSeedMarkers = new[]
            {
                "SeedData.CodeGen",
                "PrintTemplateModule",
                "生成项目打印运行时菜单"
            };
            var utf8NoBom = new UTF8Encoding(false);
            var migratorLines = await File.ReadAllLinesAsync(migratorProgramPath, utf8NoBom);
            var migratorResult = migratorLines
                .Where(line => !codeGenSeedMarkers.Any(t => line.Contains(t)))
                .ToList();
            await File.WriteAllLinesAsync(migratorProgramPath, migratorResult, utf8NoBom);
        }
    }

    internal async Task RemovePlatformWebApiProjectReferencesAsync(string tempDir)
    {
        var projectPath = Path.Combine(tempDir, "CodeMaster.WebApi", "CodeMaster.WebApi.csproj");
        if (!File.Exists(projectPath))
        {
            return;
        }

        await using var input = File.OpenRead(projectPath);
        var document = await XDocument.LoadAsync(input, LoadOptions.PreserveWhitespace, CancellationToken.None);
        input.Close();

        var elementsToRemove = document
            .Descendants()
            .Where(element =>
            {
                var include = element.Attribute("Include")?.Value.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(include))
                {
                    return false;
                }

                return element.Name.LocalName switch
                {
                    "ProjectReference" => include.Contains("/CodeMaster.Agent/", StringComparison.OrdinalIgnoreCase),
                    "Content" => include.Contains("/Templates/CodeMaster_Template_", StringComparison.OrdinalIgnoreCase) ||
                                 include.Contains("/CodeMaster.CodeGenerator/Templates/", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
            })
            .ToList();

        foreach (var element in elementsToRemove)
        {
            element.Remove();
        }

        foreach (var itemGroup in document
                     .Descendants()
                     .Where(element => element.Name.LocalName == "ItemGroup" && !element.Elements().Any())
                     .ToList())
        {
            itemGroup.Remove();
        }

        await using var output = File.Create(projectPath);
        await document.SaveAsync(output, SaveOptions.DisableFormatting, CancellationToken.None);
    }

    internal async Task RemoveFrontendMcpReferencesAsync(string tempDir)
    {
        var utf8WithoutBom = new UTF8Encoding(false);
        var routerPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "router", "index.js");
        if (File.Exists(routerPath))
        {
            var lines = (await File.ReadAllLinesAsync(routerPath, utf8WithoutBom)).ToList();
            while (true)
            {
                var markerIndex = lines.FindIndex(ContainsFrontendMcpReference);
                if (markerIndex < 0)
                    break;

                var startIndex = markerIndex;
                while (startIndex >= 0 && lines[startIndex].Trim() != "{")
                    startIndex--;

                if (startIndex < 0)
                    throw new InvalidOperationException("Unable to locate the MCP route object start in CodeMaster.Vue/src/router/index.js");

                var depth = 0;
                var endIndex = -1;
                for (var i = startIndex; i < lines.Count; i++)
                {
                    depth += lines[i].Count(ch => ch == '{');
                    depth -= lines[i].Count(ch => ch == '}');
                    if (depth == 0)
                    {
                        endIndex = i;
                        break;
                    }
                }

                if (endIndex < startIndex)
                    throw new InvalidOperationException("Unable to locate the MCP route object end in CodeMaster.Vue/src/router/index.js");

                lines.RemoveRange(startIndex, endIndex - startIndex + 1);
            }

            await File.WriteAllLinesAsync(routerPath, lines, utf8WithoutBom);
        }

        var layoutPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "layout", "index.vue");
        if (File.Exists(layoutPath))
        {
            var lines = await File.ReadAllLinesAsync(layoutPath, utf8WithoutBom);
            var result = lines
                .Where(line => !ContainsFrontendMcpReference(line))
                .Select(line => line.Replace(
                    "<el-dropdown-item divided @click=\"handleLogout\">",
                    "<el-dropdown-item @click=\"handleLogout\">",
                    StringComparison.Ordinal))
                .ToList();

            await File.WriteAllLinesAsync(layoutPath, result, utf8WithoutBom);
        }
    }

    internal async Task RemoveFrontendAgentReferencesAsync(string tempDir)
    {
        var layoutPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "layout", "index.vue");
        if (!File.Exists(layoutPath))
        {
            return;
        }

        var utf8WithoutBom = new UTF8Encoding(false);
        var lines = (await File.ReadAllLinesAsync(layoutPath, utf8WithoutBom)).ToList();
        var result = new List<string>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("<el-tooltip content=\"AI 助手\"", StringComparison.Ordinal))
            {
                while (i < lines.Count && !lines[i].Contains("</el-tooltip>", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            if (line.Contains("<agent-drawer", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("@/components/AgentAssistant/", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("const agentVisible", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.TrimStart().StartsWith(".agent-trigger {", StringComparison.Ordinal))
            {
                var depth = 0;
                do
                {
                    depth += lines[i].Count(ch => ch == '{');
                    depth -= lines[i].Count(ch => ch == '}');
                    i++;
                }
                while (i < lines.Count && depth > 0);

                i--;
                continue;
            }

            result.Add(line.Replace(", ChatDotRound", string.Empty, StringComparison.Ordinal));
        }

        await File.WriteAllLinesAsync(layoutPath, result, utf8WithoutBom);
    }

    internal async Task RewriteFrontendViewModuleGlobAsync(string tempDir)
    {
        var routerPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "router", "index.js");
        if (!File.Exists(routerPath))
        {
            return;
        }

        var utf8WithoutBom = new UTF8Encoding(false);
        var content = await File.ReadAllTextAsync(routerPath, utf8WithoutBom);

        content = Regex.Replace(
            content,
            @"^\s*import\s+\{\s*modules\s*\}\s+from\s+['""]@view-modules['""]\s*;?\s*$",
            "const modules = import.meta.glob('../views/**/*.vue')",
            RegexOptions.Multiline);

        content = Regex.Replace(
            content,
            @"import\.meta\.glob\(\s*['""](?:@/views|/src/views)/\*\*/\*\.vue['""]\s*\)",
            "import.meta.glob('../views/**/*.vue')");

        content = Regex.Replace(
            content,
            @"let componentPath\s*=\s*`@/views/\$\{view\}\.vue`",
            "let componentPath = `../views/${view}.vue`");
        content = Regex.Replace(
            content,
            @"if\s*\(!modules\[`/src/views/\$\{view\}\.vue`\]\)",
            "if (!modules[componentPath])");
        content = Regex.Replace(
            content,
            @"componentPath\s*=\s*`@/views/\$\{view\}/index\.vue`",
            "componentPath = `../views/${view}/index.vue`");
        content = Regex.Replace(
            content,
            @"modules\[componentPath\.replace\('@',\s*'/src'\)\]",
            "modules[componentPath]");

        if (content.Contains("@view-modules", StringComparison.Ordinal) ||
            Regex.IsMatch(content, @"import\.meta\.glob\(\s*['""]@/views/", RegexOptions.IgnoreCase) ||
            content.Contains("modules[`/src/views/${view}.vue`]", StringComparison.Ordinal) ||
            content.Contains("componentPath.replace('@', '/src')", StringComparison.Ordinal) ||
            !Regex.IsMatch(
                content,
                @"const\s+modules\s*=\s*import\.meta\.glob\(\s*['""]\.\./views/\*\*/\*\.vue['""]\s*\)",
                RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException(
                "Unable to rewrite CodeMaster.Vue/src/router/index.js to a portable Vite view-module glob.");
        }

        await File.WriteAllTextAsync(routerPath, content, utf8WithoutBom);
    }

    private static bool ContainsFrontendMcpReference(string value)
    {
        return value.Contains("mcp-token", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("mcpToken", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 Program.cs 中删除额外种子数据调用
    /// </summary>
    internal async Task RemoveTauriBridgeLinesAsync(string tempDir)
    {
        var mainPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "main.js");
        var layoutPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "layout", "index.vue");
        if (!File.Exists(mainPath) && !File.Exists(layoutPath))
        {
            return;
        }

        var utf8WithoutBom = new UTF8Encoding(false);
        if (File.Exists(mainPath))
        {
            var lines = await File.ReadAllLinesAsync(mainPath, utf8WithoutBom);
            var result = lines
                .Where(line =>
                    !line.Contains("tauriBridge", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("installTauriBridge", StringComparison.OrdinalIgnoreCase))
                .ToList();

            await File.WriteAllLinesAsync(mainPath, result, utf8WithoutBom);
        }

        if (File.Exists(layoutPath))
        {
            var lines = await File.ReadAllLinesAsync(layoutPath, utf8WithoutBom);
            var result = lines
                .Where(line =>
                    !line.Contains("@/utils/tauriBridge", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("@/components/UpdateChecker", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("<update-checker", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("const isClient = isTauriRuntime()", StringComparison.Ordinal))
                .ToList();

            await File.WriteAllLinesAsync(layoutPath, result, utf8WithoutBom);
        }
    }

    private async Task RewriteRequestWithoutClientBridgeAsync(string tempDir)
    {
        var requestPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "utils", "request.js");
        if (!File.Exists(requestPath))
        {
            return;
        }

        await File.WriteAllTextAsync(requestPath, GetCleanRequestJs(), new UTF8Encoding(false));
    }

    private async Task RewriteSignalRWithoutClientBridgeAsync(string tempDir)
    {
        var signalRPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "utils", "signalr.js");
        if (!File.Exists(signalRPath))
        {
            return;
        }

        await File.WriteAllTextAsync(signalRPath, GetCleanSignalRJs(), new UTF8Encoding(false));
    }

    private async Task RemoveLoginClientBridgeAsync(string tempDir)
    {
        var loginPath = Path.Combine(tempDir, "CodeMaster.Vue", "src", "views", "login", "index.vue");
        if (!File.Exists(loginPath))
        {
            return;
        }

        var utf8WithoutBom = new UTF8Encoding(false);
        var content = await File.ReadAllTextAsync(loginPath, utf8WithoutBom);
        content = GeneratedTemplateCleanup.RemoveLoginClientBridge(content);
        await File.WriteAllTextAsync(loginPath, content, utf8WithoutBom);
    }

    internal async Task RemoveAgentDbContextReferencesAsync(string tempDir)
    {
        var dbContextPath = Path.Combine(
            tempDir,
            "CodeMaster.Migrator",
            "Persistence",
            "EfCore",
            "CodeMasterDbContext.cs");
        if (!File.Exists(dbContextPath))
        {
            return;
        }

        var utf8WithoutBom = new UTF8Encoding(false);
        var lines = (await File.ReadAllLinesAsync(dbContextPath, utf8WithoutBom)).ToList();
        var result = new List<string>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains(".Domain.Entities.Ai;", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("modelBuilder.Entity<Ai", StringComparison.Ordinal))
            {
                while (i < lines.Count && !lines[i].TrimEnd().EndsWith(';'))
                {
                    i++;
                }

                continue;
            }

            result.Add(line);
        }

        await File.WriteAllLinesAsync(dbContextPath, result, utf8WithoutBom);
    }

    internal async Task RemoveCodeGenDbContextReferencesAsync(string tempDir)
    {
        var dbContextPath = Path.Combine(
            tempDir,
            "CodeMaster.Migrator",
            "Persistence",
            "EfCore",
            "CodeMasterDbContext.cs");
        if (!File.Exists(dbContextPath))
        {
            return;
        }

        var utf8WithoutBom = new UTF8Encoding(false);
        var lines = (await File.ReadAllLinesAsync(dbContextPath, utf8WithoutBom)).ToList();
        var result = new List<string>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains(".Domain.Entities.CodeGen;", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("modelBuilder.Entity<ProjectModule>()", StringComparison.Ordinal) ||
                line.Contains("modelBuilder.Entity<ModuleEntity>()", StringComparison.Ordinal))
            {
                while (i < lines.Count && !lines[i].TrimEnd().EndsWith(';'))
                {
                    i++;
                }

                continue;
            }

            result.Add(line);
        }

        await File.WriteAllLinesAsync(dbContextPath, result, utf8WithoutBom);
    }

    private static string GetCleanRequestJs()
    {
        return """
import axios from 'axios'
import { ElMessage } from 'element-plus'
import router from '@/router'
import i18n from '@/i18n'

const t = (key, params) => i18n.global.t(key, params)

const service = axios.create({
  baseURL: '/api',
  timeout: 10000
})

service.interceptors.request.use(
  config => {
    const userStore = localStorage.getItem('user')
    if (userStore) {
      try {
        const user = JSON.parse(userStore)
        if (user.token) {
          config.headers['Authorization'] = `Bearer ${user.token}`
        }
      } catch (e) {
        console.error('Failed to parse user store:', e)
      }
    }

    const settingsStore = localStorage.getItem('settings')
    if (settingsStore) {
      try {
        const settings = JSON.parse(settingsStore)
        if (settings.language) {
          config.headers['Accept-Language'] = settings.language
        }
      } catch (e) {
        console.error('Failed to parse settings store:', e)
      }
    }

    return config
  },
  error => {
    console.error('Request error:', error)
    return Promise.reject(error)
  }
)

service.interceptors.response.use(
  response => {
    const res = response.data

    if (res && typeof res === 'object' && !Array.isArray(res) && typeof res.code === 'number') {
      if (res.code === 200) {
        return res.data
      }

      ElMessage.error(res.message || t('request_failed'))
      return Promise.reject(new Error(res.message || t('request_failed')))
    }

    return res
  },
  error => {
    console.error('Response error:', error)

    if (error.response && error.response.status === 401) {
      ElMessage.error(t('login_expired'))
      localStorage.removeItem('user')
      localStorage.removeItem('permission')
      router.push('/login')
    } else {
      const errorMsg = error.response?.data?.message || error.message
      if (errorMsg) {
        ElMessage.error(errorMsg)
      }
    }

    return Promise.reject(error)
  }
)

export default service
""";
    }

    private static string GetCleanSignalRJs()
    {
        return """
import * as signalR from '@microsoft/signalr'
import { ElMessage } from 'element-plus'
import i18n from '@/i18n'

const t = (key, params) => i18n.global.t(key, params)

function getAccessToken() {
  const userStore = localStorage.getItem('user')
  if (userStore) {
    try {
      const user = JSON.parse(userStore)
      if (user.token) {
        return user.token
      }
    } catch (error) {
      console.error('Failed to parse user store:', error)
    }
  }

  return localStorage.getItem('token') || ''
}

function getHubUrl(path) {
  return path
}

class SignalRService {
  constructor() {
    this.connection = null
    this.projectInitConnection = null
  }

  async connect() {
    const token = getAccessToken()
    if (!token) {
      console.warn(t('signalr_no_token'))
      return
    }

    if (this.connection && this.connection.state !== signalR.HubConnectionState.Disconnected) {
      return
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(getHubUrl('/hubs/notification'), {
        accessTokenFactory: () => token
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build()

    this.connection.on('ForceOffline', (data) => {
      ElMessage.warning(data?.message || data?.Message || t('force_offline'))
      localStorage.removeItem('token')
      localStorage.removeItem('userInfo')
      localStorage.removeItem('user')
      localStorage.removeItem('permission')
      window.location.href = '/login'
    })

    this.connection.on('UserOnline', (data) => {
      console.log('User online:', data)
    })

    this.connection.on('UserOffline', (data) => {
      console.log('User offline:', data)
    })

    try {
      await this.connection.start()
      console.log('SignalR connected')
    } catch (error) {
      console.error('SignalR connection failed:', error)
    }
  }

  async connectProjectInit(onProgress) {
    const token = getAccessToken()
    if (!token) {
      console.warn(t('signalr_no_token'))
      return
    }

    if (this.projectInitConnection && this.projectInitConnection.state !== signalR.HubConnectionState.Disconnected) {
      return
    }

    this.projectInitConnection = new signalR.HubConnectionBuilder()
      .withUrl(getHubUrl('/hubs/project-initialization'), {
        accessTokenFactory: () => token
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build()

    this.projectInitConnection.on('ReceiveProgress', (projectId, step, message, progress) => {
      if (onProgress) {
        onProgress(projectId, step, message, progress)
      }
    })

    try {
      await this.projectInitConnection.start()
      console.log('Project initialization SignalR connected')
    } catch (error) {
      console.error('Project initialization SignalR connection failed:', error)
    }
  }

  async disconnectProjectInit() {
    if (this.projectInitConnection) {
      await this.projectInitConnection.stop()
      this.projectInitConnection = null
    }
  }

  async disconnect() {
    if (this.connection) {
      await this.connection.stop()
      this.connection = null
    }
    await this.disconnectProjectInit()
  }
}

export const signalRService = new SignalRService()
""";
    }

    private async Task RemoveAdditionalSeedCallAsync(string tempDir)
    {
        var programPath = Path.Combine(tempDir, "CodeMaster.Migrator", "Program.cs");
        if (File.Exists(programPath))
        {
            var utf8WithoutBom = new UTF8Encoding(false);
            var lines = await File.ReadAllLinesAsync(programPath, utf8WithoutBom);
            var result = new List<string>();

            foreach (var line in lines)
            {
                // 跳过包含 SeedAdditionalDataAsync 的行
                if (!line.Contains("SeedAdditionalDataAsync"))
                {
                    result.Add(line);
                }
            }

            await File.WriteAllLinesAsync(programPath, result, utf8WithoutBom);
        }
    }

    /// <summary>
    /// 复制 CodeMaster.sln 文件
    /// </summary>
    private async Task CopySolutionTemplateAsync(string sourceDir, string tempDir)
    {
        var sourceSlnPath = Path.Combine(sourceDir, "CodeMaster.sln");
        var destSlnPath = Path.Combine(tempDir, "CodeMaster.sln");

        if (File.Exists(sourceSlnPath))
        {
            var utf8WithoutBom = new UTF8Encoding(false);

            // 读取 .sln 文件内容
            var content = await File.ReadAllTextAsync(sourceSlnPath, utf8WithoutBom);

            // 移除 CodeGenerator 项目引用（整行删除）
            var projectsToRemove = new[]
            {
                "CodeMaster.CodeGenerator",
                "CodeMaster.CodeGenerator.Tests",
                "CodeMaster.OpenSpec.Tests",
                "CodeMaster.E2eTests",
                "CodeMaster.McpServer",
                "CodeMaster.LocalAgent",
                "CodeMaster.LocalAgent.Tests",
                "CodeMaster.Agent",
                "CodeMaster.Agent.Tests"
            };

            content = RemoveSolutionProjects(content, projectsToRemove);
            content = RemoveMissingSolutionProjects(content, tempDir);

            // 写入目标文件（使用 UTF-8 without BOM）
            await File.WriteAllTextAsync(destSlnPath, content, utf8WithoutBom);
        }
    }

    private static string RemoveSolutionProjects(string content, IReadOnlyCollection<string> projectNames)
    {
        var projectNameSet = new HashSet<string>(projectNames, StringComparer.OrdinalIgnoreCase);
        var projectGuidsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var keptLines = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = Regex.Match(line,
                "^Project\\(\"\\{[^}]+\\}\"\\) = \"(?<name>[^\"]+)\", \"[^\"]+\", \"(?<guid>\\{[^}]+\\})\"");

            if (match.Success && projectNameSet.Contains(match.Groups["name"].Value))
            {
                projectGuidsToRemove.Add(match.Groups["guid"].Value);
                while (i + 1 < lines.Length &&
                       !lines[i + 1].Trim().Equals("EndProject", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                }

                if (i + 1 < lines.Length)
                {
                    i++;
                }

                continue;
            }

            keptLines.Add(line);
        }

        if (projectGuidsToRemove.Count == 0)
        {
            return string.Join(Environment.NewLine, keptLines);
        }

        return string.Join(Environment.NewLine,
            keptLines.Where(line => !projectGuidsToRemove.Any(guid =>
                line.Contains(guid, StringComparison.OrdinalIgnoreCase))));
    }

    internal static string RemoveMissingSolutionProjects(string content, string templateRoot)
    {
        var missingProjectNames = Regex.Matches(
                content,
                "^Project\\(\\\"\\{[^}]+\\}\\\"\\) = \\\"(?<name>[^\\\"]+)\\\", \\\"(?<path>[^\\\"]+\\.csproj)\\\", \\\"\\{[^}]+\\}\\\"",
                RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(match =>
            {
                var projectPath = match.Groups["path"].Value
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                return !File.Exists(Path.Combine(templateRoot, projectPath));
            })
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return missingProjectNames.Count == 0
            ? content
            : RemoveSolutionProjects(content, missingProjectNames);
    }
}
