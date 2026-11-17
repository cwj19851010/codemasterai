using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Read-only access to the text documents that live inside a generated project folder.
///
/// Deliberately narrow: only markdown and plain text, only below the project root, and only
/// up to a fixed size. The Agent is a metadata tool, so letting it open arbitrary files in the
/// generated solution would turn a modelling mistake into a source-code edit.
/// </summary>
public static class AiProjectFileReader
{
    /// <summary>2 MB per file. Beyond that it is not a requirement document.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    public const int MaxListedFiles = 200;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode", "dist", "packages", "TestResults"
    };

    /// <summary>
    /// Resolves a project-relative path to a physical path that is guaranteed to stay inside
    /// the project root. Absolute paths, drive-qualified paths and <c>..</c> segments are all
    /// rejected before the file system is touched.
    /// </summary>
    public static bool TryResolve(string? projectRoot, string? relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            error = "这个项目还没有生成目录（ProjectPath 为空），无法读取项目文件。请先完成项目初始化。";
            return false;
        }

        var root = Path.GetFullPath(projectRoot.Trim());
        if (!Directory.Exists(root))
        {
            error = $"项目目录不存在：{root}";
            return false;
        }

        var relative = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (relative.Length == 0)
        {
            error = "必须提供项目内的相对路径，例如 README.md 或 docs/需求.md。";
            return false;
        }

        if (Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            error = "只接受项目内的相对路径，不接受绝对路径。";
            return false;
        }

        if (relative.Split('/').Any(segment => segment == ".."))
        {
            error = "路径不能包含 .. ，只能读取项目目录内的文件。";
            return false;
        }

        var combined = Path.GetFullPath(Path.Combine(root, relative));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "目标路径超出了项目目录。";
            return false;
        }

        if (!IsAllowedPath(combined))
        {
            error = $"只支持读取 {string.Join(" / ", AiAttachmentLimits.AllowedExtensions)} 文件，收到的是「{Path.GetFileName(combined)}」。";
            return false;
        }

        if (!File.Exists(combined))
        {
            error = $"项目里没有这个文件：{relative}。可以先调用 list_project_files 查看可用文档。";
            return false;
        }

        fullPath = combined;
        error = string.Empty;
        return true;
    }

    public static bool IsAllowedPath(string path)
        => AiAttachmentLimits.IsAllowedFileName(Path.GetFileName(path));

    /// <summary>
    /// Lists markdown and text documents below the project root, skipping build output and
    /// dependency folders. Sorted by path so the model sees a stable order across calls.
    /// </summary>
    public static IReadOnlyList<(string RelativePath, long Size)> EnumerateDocuments(
        string? projectRoot,
        string? subDirectory = null,
        int maxResults = MaxListedFiles)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return Array.Empty<(string, long)>();

        var root = Path.GetFullPath(projectRoot.Trim());
        if (!Directory.Exists(root))
            return Array.Empty<(string, long)>();

        var start = root;
        if (!string.IsNullOrWhiteSpace(subDirectory))
        {
            var relative = subDirectory.Trim().Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment == "..") || Path.IsPathRooted(relative))
                return Array.Empty<(string, long)>();

            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate))
                return Array.Empty<(string, long)>();

            start = candidate;
        }

        var results = new List<(string, long)>();
        var pending = new Stack<string>();
        pending.Push(start);

        while (pending.Count > 0 && results.Count < maxResults)
        {
            var current = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                // Materialise inside the try: Directory.Enumerate* is lazy, so a permission
                // failure would otherwise surface during the foreach below, outside the guard.
                files = Directory.GetFiles(current);
                directories = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (results.Count >= maxResults)
                    break;
                if (!IsAllowedPath(file))
                    continue;

                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                results.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), size));
            }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (IgnoredDirectories.Contains(name))
                    continue;
                pending.Push(directory);
            }
        }

        return results
            .OrderBy(item => item.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
