using System.Text;
using System.Text.RegularExpressions;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>存活实体标识（模块名 + 实体名），用于判断磁盘上的生成文件是否还有对应元数据。</summary>
public sealed record LiveEntityKey(string ModuleName, string EntityName);

/// <summary>被保留的孤儿切片——用户在里面写过代码，宁可留着也不能删。</summary>
public sealed record KeptOrphanSlice(string ModuleName, string EntityName, string Reason);

/// <summary>一次孤儿清理的结果。删除项与保留项分开报，便于测试与排查。</summary>
public sealed record OrphanSweepResult(
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<string> DeletedDirectories,
    IReadOnlyList<KeptOrphanSlice> KeptSlices)
{
    public static OrphanSweepResult Empty { get; } = new([], [], []);

    public bool AnythingDeleted => DeletedFiles.Count > 0 || DeletedDirectories.Count > 0;
}

/// <summary>
/// 清理"实体已从元数据消失、但生成的代码还留在磁盘上"的死代码。
/// <para>
/// 为什么需要它：生成只写不删，实体/模块被移除后旧文件一直留着。最典型的后果是同名实体出现在
/// 两个命名空间里，SourceGenerator 扫全目录 → <c>CS0104</c> 引用不明确，整个工程编译不过。
/// 实测 <c>EngineeringManagement/Material.cs</c> 与 <c>MaterialProcurement/Material.cs</c> 曾长期并存。
/// </para>
/// <para>
/// <b>删除单位是"实体整条纵向切片"，不是"文件类型"。</b> 曾经只删 <c>{Entity}.auto.cs</c>，
/// 结果工程直接编译失败（<c>CS0311</c>）：基接口 <c>IEntity&lt;long&gt;</c> 声明在 <c>.auto.cs</c> 里，
/// 删掉后留下的空用户 partial 类变成裸类，而 Dtos / Services 还在把它当 <c>TEntity</c> 用。
/// 半清理比不清理更糟，所以一个实体要么整条切片一起处理，要么整条跳过。
/// </para>
/// <para>
/// 用户文件 <c>{Entity}.cs</c> 只在"仍是模板样板"时才跟着删；一旦发现用户写过属性/方法，
/// 整条切片原样保留并记录到 <see cref="OrphanSweepResult.KeptSlices"/>。
/// </para>
/// </summary>
public static class GeneratedCodeOrphanSweeper
{
    private const string AutoSuffix = ".auto.cs";

    /// <summary>
    /// 模板生成的用户 partial 去掉注释与空白后，只剩下这三段。任何手写成员都会让匹配失败。
    /// </summary>
    private static readonly Regex BoilerplatePattern = new(
        @"^namespace[A-Za-z0-9_.]+;publicpartialclass[A-Za-z0-9_]+{[^}]*}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 扫描并清理孤儿切片。
    /// </summary>
    /// <param name="projectPath">生成项目的根目录。</param>
    /// <param name="projectName">项目名，用于拼 <c>{Project}.Domain</c> / <c>{Project}.Application</c> / <c>{Project}.Vue</c>。</param>
    /// <param name="liveEntities">当前元数据里存活的实体。为空时**不做任何删除**，避免一次查询异常把整个项目清空。</param>
    public static OrphanSweepResult Sweep(
        string projectPath,
        string projectName,
        IEnumerable<LiveEntityKey> liveEntities)
    {
        if (string.IsNullOrWhiteSpace(projectPath) ||
            string.IsNullOrWhiteSpace(projectName) ||
            !Directory.Exists(projectPath))
        {
            return OrphanSweepResult.Empty;
        }

        // 空集合当作"调用方没拿到元数据"处理：宁可这次不清理，也不能把整个项目的代码删光。
        var live = liveEntities
            .Where(item => !string.IsNullOrWhiteSpace(item.ModuleName) && !string.IsNullOrWhiteSpace(item.EntityName))
            .Select(item => (Module: item.ModuleName.Trim(), Entity: item.EntityName.Trim()))
            .ToHashSet();
        if (live.Count == 0)
            return OrphanSweepResult.Empty;

        var domainEntitiesRoot = Path.Combine(projectPath, $"{projectName}.Domain", "Entities");
        if (!Directory.Exists(domainEntitiesRoot))
            return OrphanSweepResult.Empty;

        var deletedFiles = new List<string>();
        var deletedDirectories = new List<string>();
        var keptSlices = new List<KeptOrphanSlice>();

        foreach (var moduleDirectory in Directory.GetDirectories(domainEntitiesRoot))
        {
            var moduleName = Path.GetFileName(moduleDirectory);

            // System / Monitor 这类框架目录没有 .auto.cs，天然不会命中，无需额外白名单。
            var autoFiles = Directory.GetFiles(moduleDirectory, "*" + AutoSuffix, SearchOption.TopDirectoryOnly);
            if (autoFiles.Length == 0)
                continue;

            foreach (var autoFile in autoFiles)
            {
                var fileName = Path.GetFileName(autoFile);
                var entityName = fileName[..^AutoSuffix.Length];
                if (entityName.Length == 0)
                    continue;
                if (live.Contains((moduleName, entityName)))
                    continue;

                var userPartialPath = Path.Combine(moduleDirectory, $"{entityName}.cs");
                if (File.Exists(userPartialPath) && !IsBoilerplate(userPartialPath))
                {
                    // 用户在实体里写过东西：整条切片原样保留。
                    keptSlices.Add(new KeptOrphanSlice(
                        moduleName,
                        entityName,
                        "user partial contains hand-written members"));
                    continue;
                }

                DeleteSlice(projectPath, projectName, moduleName, entityName, deletedFiles, deletedDirectories);
            }

            DeleteDirectoryIfEmpty(moduleDirectory, deletedDirectories);
        }

        return new OrphanSweepResult(deletedFiles, deletedDirectories, keptSlices);
    }

    /// <summary>删除一个实体的整条纵向切片：Domain + Application(Dtos/Services) + Vue(api/views)。</summary>
    private static void DeleteSlice(
        string projectPath,
        string projectName,
        string moduleName,
        string entityName,
        List<string> deletedFiles,
        List<string> deletedDirectories)
    {
        var domainDirectory = Path.Combine(projectPath, $"{projectName}.Domain", "Entities", moduleName);
        var dtoDirectory = Path.Combine(projectPath, $"{projectName}.Application", "Dtos", moduleName);
        var serviceDirectory = Path.Combine(projectPath, $"{projectName}.Application", "Services", moduleName);

        foreach (var path in new[]
                 {
                     Path.Combine(domainDirectory, $"{entityName}{AutoSuffix}"),
                     Path.Combine(domainDirectory, $"{entityName}.cs"),
                     Path.Combine(dtoDirectory, $"{entityName}Dto.cs"),
                     Path.Combine(serviceDirectory, $"I{entityName}Service.cs"),
                     Path.Combine(serviceDirectory, $"{entityName}Service.cs")
                 })
        {
            TryDeleteFile(path, deletedFiles);
        }

        var moduleSegment = CodeGenNaming.ToPathSegment(moduleName);
        var entitySegment = CodeGenNaming.ToPathSegment(entityName);
        var vueSourceRoot = Path.Combine(projectPath, $"{projectName}.Vue", "src");

        TryDeleteFile(
            Path.Combine(vueSourceRoot, "api", moduleSegment, $"{entitySegment}.js"),
            deletedFiles);

        var viewDirectory = Path.Combine(vueSourceRoot, "views", moduleSegment, entitySegment);
        if (Directory.Exists(viewDirectory))
        {
            try
            {
                Directory.Delete(viewDirectory, recursive: true);
                deletedDirectories.Add(viewDirectory);
            }
            catch (IOException)
            {
                // 目录被占用就留着，下次生成再试——不能因为删不掉而让整个生成失败。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        DeleteDirectoryIfEmpty(Path.Combine(vueSourceRoot, "views", moduleSegment), deletedDirectories);
        DeleteDirectoryIfEmpty(Path.Combine(vueSourceRoot, "api", moduleSegment), deletedDirectories);
    }

    private static void TryDeleteFile(string path, List<string> deletedFiles)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
            deletedFiles.Add(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryIfEmpty(string directory, List<string> deletedDirectories)
    {
        if (!Directory.Exists(directory))
            return;
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            return;

        try
        {
            Directory.Delete(directory, recursive: false);
            deletedDirectories.Add(directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 判断用户 partial 是否仍是模板原样（只有注释，没有任何成员）。
    /// </summary>
    /// <remarks>
    /// 判不准时一律返回 false（= 保留）。删文件不可逆，宁可漏删也不能误删手写代码。
    /// </remarks>
    internal static bool IsBoilerplate(string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var stripped = StripComments(content.TrimStart('\uFEFF'));
        var compact = new string(stripped.Where(character => !char.IsWhiteSpace(character)).ToArray());

        // 空文件里没有任何内容会丢失。
        if (compact.Length == 0)
            return true;

        return BoilerplatePattern.IsMatch(compact);
    }

    /// <summary>去掉 <c>//</c> 行注释与 <c>/* */</c> 块注释。字符串字面量按保守处理，残留的字符会让判定失败。</summary>
    private static string StripComments(string content)
    {
        var builder = new StringBuilder(content.Length);
        var index = 0;
        while (index < content.Length)
        {
            if (content[index] == '/' && index + 1 < content.Length && content[index + 1] == '/')
            {
                while (index < content.Length && content[index] != '\n')
                    index++;
                continue;
            }

            if (content[index] == '/' && index + 1 < content.Length && content[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < content.Length && !(content[index] == '*' && content[index + 1] == '/'))
                    index++;
                index = Math.Min(index + 2, content.Length);
                continue;
            }

            builder.Append(content[index]);
            index++;
        }

        return builder.ToString();
    }
}
