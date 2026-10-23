using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.VueParser;
using CodeMaster.Infrastructure.VueParser.Model;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Detects and explicitly imports template-only Vue edits.  It never infers data
/// bindings from arbitrary script code; only stable genId values are matched.
/// </summary>
[DynamicApiPermission(requirePermission: true)]
public sealed class ComplexPageExternalSyncService : IComplexPageExternalSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ISqlSugarClient _db;
    private readonly IProjectPageService _pageService;

    public ComplexPageExternalSyncService(ISqlSugarClient db, IProjectPageService pageService)
    {
        _db = db;
        _pageService = pageService;
    }

    [Permission("codegen:projectpage:view")]
    public async Task<ComplexPageExternalChangeDto> InspectAsync(long pageId)
    {
        var page = await _pageService.GetByIdAsync(pageId)
            ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
        var path = await ResolveVuePathAsync(page);
        if (!File.Exists(path))
        {
            return new ComplexPageExternalChangeDto
            {
                PageId = pageId,
                VuePath = path,
                LastGeneratedVueHash = page.LastGeneratedVueHash,
                CanImport = false,
                Error = "Generated Vue page does not exist."
            };
        }

        var hash = await ComputeHashAsync(path);
        var changed = !string.IsNullOrWhiteSpace(page.LastGeneratedVueHash) &&
                      !string.Equals(hash, page.LastGeneratedVueHash, StringComparison.OrdinalIgnoreCase);
        var result = new ComplexPageExternalChangeDto
        {
            PageId = pageId,
            VuePath = path,
            CurrentVueHash = hash,
            LastGeneratedVueHash = page.LastGeneratedVueHash,
            Changed = changed,
            CanImport = true
        };
        if (changed)
        {
            try
            {
                await BuildDiffAsync(page, path, result);
            }
            catch (VueOfficialCompilerException exception)
            {
                result.CanImport = false;
                result.Error = $"Official Vue SFC parsing failed: {exception.Message}";
            }
        }
        return result;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ComplexPageExternalChangeDto> ImportTemplateAsync(long pageId, bool confirmed = false)
    {
        var page = await _pageService.GetByIdAsync(pageId)
            ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
        var inspection = await InspectAsync(pageId);
        if (!inspection.CanImport) return inspection;
        if (!inspection.Changed) return inspection;
        if (!confirmed)
        {
            inspection.Error = "Vue source changed externally. Confirm to import the template into the tree definition.";
            return inspection;
        }

        var outputDirectory = Path.GetDirectoryName(inspection.VuePath)!;
        var treePath = Path.Combine(outputDirectory, $"{page.PageKey}.tree.json");
        var pagePath = Path.Combine(outputDirectory, $"{page.PageKey}.page.json");
        var backupDirectory = Path.Combine(outputDirectory, ".codemaster-backup", "external", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(backupDirectory);
        await BackupIfExistsAsync(inspection.VuePath, backupDirectory);
        await BackupIfExistsAsync(treePath, backupDirectory);
        await BackupIfExistsAsync(pagePath, backupDirectory);

        var oldTree = File.Exists(treePath)
            ? JsonSerializer.Deserialize<List<Component>>(await File.ReadAllTextAsync(treePath), JsonOptions) ?? new List<Component>()
            : new List<Component>();
        var externalParse = new VueTemplateParser().ParseOfficial(
            await File.ReadAllTextAsync(inspection.VuePath),
            Path.GetFileName(inspection.VuePath));
        var externalTree = externalParse.Components;
        inspection.VueCompilerVersion = externalParse.CompilerVersion;
        inspection.ScriptSetupPreserved = externalParse.ScriptSetupContent != null;
        if (externalParse.ScriptSetupContent != null)
        {
            inspection.ScriptSetupBackupPath = Path.Combine(backupDirectory, $"{page.PageKey}.script-setup.txt");
            await File.WriteAllTextAsync(
                inspection.ScriptSetupBackupPath,
                externalParse.ScriptSetupContent,
                new UTF8Encoding(false));
        }
        var oldByGenId = Enumerate(oldTree)
            .Where(item => !string.IsNullOrWhiteSpace(item.GenId))
            .GroupBy(item => item.GenId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var counts = new ExternalImportCounts();
        AssignStableIds(externalTree, oldByGenId, counts, new Counter());
        var transformedTree = JsonSerializer.Serialize(externalTree, JsonOptions);
        await File.WriteAllTextAsync(treePath, transformedTree, new UTF8Encoding(false));

        await _db.Updateable<ProjectPage>()
            .SetColumns(item => new ProjectPage
            {
                SyncStatus = PageSyncStatus.NeedsSync,
                LastGeneratedVueHash = inspection.CurrentVueHash,
                LastError = null,
                UpdateTime = DateTime.UtcNow
            })
            .Where(item => item.Id == pageId)
            .ExecuteCommandAsync();

        inspection.Changed = false;
        inspection.ExternalNodeCount = counts.External;
        inspection.MatchedNodeCount = counts.Matched;
        inspection.BackupDirectory = backupDirectory;
        inspection.LastGeneratedVueHash = inspection.CurrentVueHash;
        return inspection;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ComplexPageExternalChangeDto> RejectImportAsync(long pageId)
    {
        var page = await _pageService.GetByIdAsync(pageId)
            ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
        var inspection = await InspectAsync(pageId);
        if (!inspection.Changed)
        {
            inspection.Error = "No external change detected; nothing to reject.";
            return inspection;
        }

        await _db.Updateable<ProjectPage>()
            .SetColumns(item => new ProjectPage
            {
                LastGeneratedVueHash = inspection.CurrentVueHash,
                LastError = null,
                UpdateTime = DateTime.UtcNow
            })
            .Where(item => item.Id == pageId)
            .ExecuteCommandAsync();

        inspection.Changed = false;
        inspection.LastGeneratedVueHash = inspection.CurrentVueHash;
        inspection.Error = "External change rejected. The next generation overwrites the Vue file.";
        return inspection;
    }

    [Permission("codegen:projectpage:update")]
    public async Task<ComplexPageExternalChangeDto> RestoreBackupAsync(long pageId)
    {
        var page = await _pageService.GetByIdAsync(pageId)
            ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
        var inspection = await InspectAsync(pageId);
        var outputDirectory = Path.GetDirectoryName(inspection.VuePath)!;
        var backupRoot = Path.Combine(outputDirectory, ".codemaster-backup", "external");
        if (!Directory.Exists(backupRoot))
        {
            inspection.Error = "No external-import backup exists for this page.";
            return inspection;
        }
        var latest = Directory.GetDirectories(backupRoot)
            .OrderByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (latest == null)
        {
            inspection.Error = "No external-import backup exists for this page.";
            return inspection;
        }

        var restored = 0;
        foreach (var file in Directory.GetFiles(latest))
        {
            var target = Path.Combine(outputDirectory, Path.GetFileName(file));
            await Task.Run(() => File.Copy(file, target, overwrite: true));
            restored++;
        }

        var vuePath = Path.Combine(outputDirectory, "index.vue");
        var restoredHash = File.Exists(vuePath) ? await ComputeHashAsync(vuePath) : string.Empty;
        await _db.Updateable<ProjectPage>()
            .SetColumns(item => new ProjectPage
            {
                SyncStatus = PageSyncStatus.NeedsSync,
                LastGeneratedVueHash = restoredHash,
                LastError = null,
                UpdateTime = DateTime.UtcNow
            })
            .Where(item => item.Id == pageId)
            .ExecuteCommandAsync();

        inspection.Changed = true;
        inspection.CurrentVueHash = restoredHash;
        inspection.LastGeneratedVueHash = restoredHash;
        inspection.BackupDirectory = latest;
        inspection.Error = $"Restored {restored} file(s) from backup. Re-run generation to recover the page.";
        return inspection;
    }

    private async Task BuildDiffAsync(ProjectPageDto page, string vuePath, ComplexPageExternalChangeDto result)
    {
        var outputDirectory = Path.GetDirectoryName(vuePath)!;
        var treePath = Path.Combine(outputDirectory, $"{page.PageKey}.tree.json");
        if (!File.Exists(treePath)) return;

        var oldTree = JsonSerializer.Deserialize<List<Component>>(await File.ReadAllTextAsync(treePath), JsonOptions) ?? new List<Component>();
        var externalParse = new VueTemplateParser().ParseOfficial(
            await File.ReadAllTextAsync(vuePath),
            Path.GetFileName(vuePath));
        result.VueCompilerVersion = externalParse.CompilerVersion;
        result.ScriptSetupPreserved = externalParse.ScriptSetupContent != null;
        var externalTree = externalParse.Components;
        var oldByGenId = Enumerate(oldTree)
            .Where(item => !string.IsNullOrWhiteSpace(item.GenId))
            .GroupBy(item => item.GenId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var externalGenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in Enumerate(externalTree))
        {
            if (!string.IsNullOrWhiteSpace(node.GenId))
            {
                externalGenIds.Add(node.GenId!);
                if (oldByGenId.TryGetValue(node.GenId!, out var old))
                    DiffNode(old, node, result);
                else
                    result.Diffs.Add(NewDiff("Added", node, $"外部新增节点 {node.Tag ?? "div"}"));
            }
            else if (string.IsNullOrWhiteSpace(node.Id) || !node.Id.StartsWith("gen_component_", StringComparison.OrdinalIgnoreCase))
            {
                result.Diffs.Add(NewDiff("Added", node, $"外部新增节点 {node.Tag ?? "div"}（无 genId）"));
            }
        }

        foreach (var pair in oldByGenId)
        {
            if (!externalGenIds.Contains(pair.Key))
            {
                var old = pair.Value;
                result.Diffs.Add(new ExternalTreeDiffItemDto
                {
                    ChangeType = "Removed",
                    ComponentId = old.Id ?? old.GenId ?? string.Empty,
                    Tag = old.Tag ?? string.Empty,
                    GenId = old.GenId,
                    Detail = $"生成节点 {old.Tag ?? "div"} 在外部 Vue 中已删除"
                });
            }
        }
    }

    private static void DiffNode(Component old, Component current, ComplexPageExternalChangeDto result)
    {
        var oldTag = old.Tag?.ToLowerInvariant() ?? string.Empty;
        var newTag = current.Tag?.ToLowerInvariant() ?? string.Empty;
        var propChanges = DiffProps(old, current);
        if (!string.Equals(oldTag, newTag, StringComparison.Ordinal))
        {
            result.Diffs.Add(NewDiff("Changed", current,
                $"标签变化：{old.Tag ?? "div"} → {current.Tag ?? "div"}"));
        }
        else if (propChanges.Length > 0)
        {
            result.Diffs.Add(NewDiff("Changed", current, $"属性变化：{propChanges}"));
        }
    }

    private static string DiffProps(Component old, Component current)
    {
        var oldProps = (old.Props ?? new List<ComponentProp>()).ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var newProps = (current.Props ?? new List<ComponentProp>()).ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        var changes = new List<string>();
        foreach (var pair in newProps)
        {
            if (!oldProps.TryGetValue(pair.Key, out var oldProp))
            {
                changes.Add($"+{pair.Key}");
                continue;
            }
            var oldValue = NormalizePropValue(oldProp);
            var newValue = NormalizePropValue(pair.Value);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                changes.Add($"{pair.Key}={newValue}");
        }
        foreach (var key in oldProps.Keys)
        {
            if (!newProps.ContainsKey(key)) changes.Add($"-{key}");
        }
        return string.Join(", ", changes);
    }

    private static string NormalizePropValue(ComponentProp prop)
        => prop.IsBind ? $":{prop.Value}" : (prop.IsSingle ? "true" : (prop.Value ?? string.Empty));

    private static ExternalTreeDiffItemDto NewDiff(string changeType, Component node, string detail)
        => new()
        {
            ChangeType = changeType,
            ComponentId = node.Id ?? node.GenId ?? string.Empty,
            Tag = node.Tag ?? string.Empty,
            GenId = node.GenId,
            Detail = detail
        };

    private async Task<string> ResolveVuePathAsync(ProjectPageDto page)
    {
        if (!string.IsNullOrWhiteSpace(page.TargetDirectory))
            return Path.Combine(page.TargetDirectory, "index.vue");
        var project = await _db.Queryable<Project>().Where(item => item.Id == page.ProjectId && !item.IsDeleted).FirstAsync()
            ?? throw new InvalidOperationException($"Project {page.ProjectId} was not found.");
        var module = await _db.Queryable<ProjectModule>().Where(item => item.Id == page.ModuleId && !item.IsDeleted).FirstAsync()
            ?? throw new InvalidOperationException($"Project module {page.ModuleId} was not found.");
        var root = project.ProjectPath;
        if (!root.EndsWith(project.ProjectName, StringComparison.OrdinalIgnoreCase))
        {
            var nested = Path.Combine(root, project.ProjectName);
            if (Directory.Exists(nested)) root = nested;
        }
        var result = Path.GetFullPath(Path.Combine(root, $"{project.ProjectName}.Vue", "src", "views", CodeGenNaming.ToPathSegment(module.ModuleName), CodeGenNaming.ToPathSegment(page.PageKey), "index.vue"));
        var vueRoot = Path.GetFullPath(Path.Combine(root, $"{project.ProjectName}.Vue"));
        var prefix = vueRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Vue path escapes the target project.");
        return result;
    }

    private static async Task<string> ComputeHashAsync(string path)
        => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();

    private static async Task BackupIfExistsAsync(string path, string backupDirectory)
    {
        if (File.Exists(path))
            await Task.Run(() => File.Copy(path, Path.Combine(backupDirectory, Path.GetFileName(path)), overwrite: true));
    }

    private static void AssignStableIds(
        IEnumerable<Component> nodes,
        IReadOnlyDictionary<string, Component> oldByGenId,
        ExternalImportCounts counts,
        Counter counter)
    {
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.GenId) && oldByGenId.TryGetValue(node.GenId, out var old))
            {
                node.Id = old.Id ?? node.GenId;
                node.DataSourceId = old.DataSourceId;
                node.BindingId = old.BindingId;
                node.FieldId = old.FieldId;
                node.Origin = old.Origin;
                counts.Matched++;
            }
            else
            {
                node.Id = string.IsNullOrWhiteSpace(node.Id) ? $"external_{++counter.Value}" : node.Id;
                node.Origin = ComponentOriginKind.External;
                counts.External++;
            }
            if (node.Children != null) AssignStableIds(node.Children, oldByGenId, counts, counter);
            if (node.UseSlots != null)
                foreach (var slot in node.UseSlots)
                    if (slot.Components != null) AssignStableIds(slot.Components, oldByGenId, counts, counter);
        }
    }

    private static IEnumerable<Component> Enumerate(IEnumerable<Component> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.Children != null)
                foreach (var child in Enumerate(node.Children)) yield return child;
            if (node.UseSlots != null)
                foreach (var slot in node.UseSlots)
                    if (slot.Components != null)
                        foreach (var child in Enumerate(slot.Components)) yield return child;
        }
    }

    private sealed class Counter { public int Value; }
    private sealed class ExternalImportCounts { public int External; public int Matched; }
}
