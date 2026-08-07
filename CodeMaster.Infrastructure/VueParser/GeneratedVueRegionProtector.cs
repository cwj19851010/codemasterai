using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Infrastructure.VueParser.Model;

namespace CodeMaster.Infrastructure.VueParser;

/// <summary>
/// Protects generated Vue template regions identified by stable data-gen-id anchors.
/// The sidecar records the normalized hash that CodeMaster expects for every region;
/// formatting-only edits therefore do not create conflicts.
/// </summary>
public sealed class GeneratedVueRegionProtector
{
    private const int SidecarVersion = 1;
    private const string WholeFileRegionId = "$file";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly VueTemplateParser _parser = new();
    private readonly VueTemplateSerializer _serializer = new();

    public GeneratedVueRegionAnalysis Analyze(
        string generatedVue,
        string? currentVue = null,
        string? sidecarJson = null,
        string? trustedCurrentWholeFileHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedVue);

        if (string.IsNullOrWhiteSpace(currentVue))
            return CreateInitialAnalysis(generatedVue);

        var generatedWholeHash = ComputeHash(generatedVue);
        var currentWholeHash = ComputeHash(currentVue);
        var sidecar = DeserializeSidecar(sidecarJson);
        if (string.Equals(currentWholeHash, trustedCurrentWholeFileHash, StringComparison.OrdinalIgnoreCase))
            return CreateInitialAnalysis(generatedVue);
        if (string.Equals(currentWholeHash, sidecar?.WholeFileHash, StringComparison.OrdinalIgnoreCase))
            return CreateInitialAnalysis(generatedVue);

        List<Component> generatedTree;
        List<Component> currentTree;
        Dictionary<string, RegionSnapshot> generatedRegions;
        Dictionary<string, RegionSnapshot> currentRegions;
        try
        {
            generatedTree = _parser.Parse(generatedVue);
            currentTree = _parser.Parse(currentVue);
            generatedRegions = ExtractRegions(generatedTree);
            currentRegions = ExtractRegions(currentTree);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return CreateWholeFileConflict(generatedVue, currentVue, generatedWholeHash, ex.Message);
        }

        if (generatedRegions.Count == 0 ||
            currentRegions.Count == 0 && (sidecar == null || sidecar.Regions.Count == 0))
            return AnalyzeWholeFileFallback(generatedVue, currentVue, generatedWholeHash, currentWholeHash, sidecar);

        var decisions = BuildDecisions(generatedRegions, currentRegions, sidecar);
        var conflicts = decisions.Values
            .Where(item => item.Conflict != null)
            .Select(item => item.Conflict!)
            .OrderBy(item => item.GenId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var consumedCurrentRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedTree = MergeLists(
            generatedTree,
            currentTree,
            decisions,
            currentRegions,
            consumedCurrentRegions);
        var mergedTemplate = _serializer.SerializeWithWrapper(mergedTree);
        var mergedVue = ReplaceTemplate(generatedVue, mergedTemplate);

        var expectedRegions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var generatedRegion in generatedRegions)
            expectedRegions[generatedRegion.Key] = generatedRegion.Value.Hash;
        foreach (var decision in decisions.Values.Where(item => item.Kind == RegionDecisionKind.KeepCurrent && item.NewRegion == null))
            expectedRegions[decision.GenId] = null;

        var nextSidecar = new GeneratedVueRegionSidecar
        {
            Version = SidecarVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            WholeFileHash = generatedWholeHash,
            Regions = expectedRegions,
            Conflicts = conflicts
        };
        return new GeneratedVueRegionAnalysis
        {
            Content = mergedVue,
            SidecarJson = JsonSerializer.Serialize(nextSidecar, JsonOptions),
            Conflicts = conflicts
        };
    }

    public async Task<GeneratedVueRegionAnalysis> WriteAsync(
        string vuePath,
        string generatedVue,
        string? trustedCurrentWholeFileHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vuePath);
        var sidecarPath = GetSidecarPath(vuePath);
        var currentVue = File.Exists(vuePath)
            ? await File.ReadAllTextAsync(vuePath, cancellationToken)
            : null;
        var sidecarJson = File.Exists(sidecarPath)
            ? await File.ReadAllTextAsync(sidecarPath, cancellationToken)
            : null;
        var analysis = Analyze(generatedVue, currentVue, sidecarJson, trustedCurrentWholeFileHash);

        var directory = Path.GetDirectoryName(Path.GetFullPath(vuePath))
            ?? throw new InvalidOperationException("Unable to resolve the Vue output directory.");
        Directory.CreateDirectory(directory);
        var temporaryVuePath = Path.Combine(directory, $".{Path.GetFileName(vuePath)}.{Guid.NewGuid():N}.tmp");
        var temporarySidecarPath = Path.Combine(directory, $".{Path.GetFileName(sidecarPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryVuePath, analysis.Content, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(temporarySidecarPath, analysis.SidecarJson, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryVuePath, vuePath, overwrite: true);
            File.Move(temporarySidecarPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryVuePath)) File.Delete(temporaryVuePath);
            if (File.Exists(temporarySidecarPath)) File.Delete(temporarySidecarPath);
        }

        return analysis;
    }

    public static string GetSidecarPath(string vuePath) => vuePath + ".genhash.json";

    public IReadOnlyDictionary<string, string> GetRegionHashes(string vueContent)
    {
        var regions = ExtractRegions(_parser.Parse(vueContent));
        return regions.ToDictionary(item => item.Key, item => item.Value.Hash, StringComparer.OrdinalIgnoreCase);
    }

    private GeneratedVueRegionAnalysis CreateInitialAnalysis(string generatedVue)
    {
        var generatedRegions = ExtractRegions(_parser.Parse(generatedVue));
        var sidecar = new GeneratedVueRegionSidecar
        {
            Version = SidecarVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            WholeFileHash = ComputeHash(generatedVue),
            Regions = generatedRegions.ToDictionary(
                item => item.Key,
                item => (string?)item.Value.Hash,
                StringComparer.OrdinalIgnoreCase)
        };
        return new GeneratedVueRegionAnalysis
        {
            Content = generatedVue,
            SidecarJson = JsonSerializer.Serialize(sidecar, JsonOptions)
        };
    }

    private GeneratedVueRegionAnalysis AnalyzeWholeFileFallback(
        string generatedVue,
        string currentVue,
        string generatedWholeHash,
        string currentWholeHash,
        GeneratedVueRegionSidecar? sidecar)
    {
        if (string.Equals(currentWholeHash, generatedWholeHash, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentWholeHash, sidecar?.WholeFileHash, StringComparison.OrdinalIgnoreCase))
            return CreateInitialAnalysis(generatedVue);

        return CreateWholeFileConflict(
            generatedVue,
            currentVue,
            generatedWholeHash,
            "No stable data-gen-id region could be matched, so CodeMaster preserved the existing file.");
    }

    private static GeneratedVueRegionAnalysis CreateWholeFileConflict(
        string generatedVue,
        string currentVue,
        string generatedWholeHash,
        string reason)
    {
        var conflict = new GeneratedVueRegionConflict
        {
            GenId = WholeFileRegionId,
            Reason = reason
        };
        var sidecar = new GeneratedVueRegionSidecar
        {
            Version = SidecarVersion,
            GeneratedAtUtc = DateTime.UtcNow,
            WholeFileHash = generatedWholeHash,
            Regions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
            Conflicts = new List<GeneratedVueRegionConflict> { conflict }
        };
        return new GeneratedVueRegionAnalysis
        {
            Content = currentVue,
            SidecarJson = JsonSerializer.Serialize(sidecar, JsonOptions),
            Conflicts = new List<GeneratedVueRegionConflict> { conflict }
        };
    }

    private Dictionary<string, RegionSnapshot> ExtractRegions(List<Component> roots)
    {
        var result = new Dictionary<string, RegionSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            CollectRegions(root, result);
        return result;
    }

    private void CollectRegions(Component component, Dictionary<string, RegionSnapshot> result)
    {
        if (!string.IsNullOrWhiteSpace(component.GenId))
        {
            if (!result.TryAdd(component.GenId, CreateSnapshot(component)))
                throw new InvalidOperationException($"Duplicate data-gen-id '{component.GenId}' prevents safe region tracking.");
        }

        if (component.Children != null)
        {
            foreach (var child in component.Children)
                CollectRegions(child, result);
        }
        if (component.UseSlots == null) return;
        foreach (var slot in component.UseSlots)
        {
            if (slot.Components == null) continue;
            foreach (var child in slot.Components)
                CollectRegions(child, result);
        }
    }

    private RegionSnapshot CreateSnapshot(Component component)
    {
        var clone = Clone(component);
        CanonicalizeRegion(clone);
        var canonical = _serializer.Serialize(new List<Component> { clone });
        return new RegionSnapshot(component.GenId!, ComputeHash(canonical), component);
    }

    private static void CanonicalizeRegion(Component component)
    {
        component.Props = component.Props?
            .OrderBy(item => item.IsBind)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToList();
        component.Instructions = component.Instructions?
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToList();
        component.Events = component.Events?
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Expression ?? item.Body, StringComparer.Ordinal)
            .ToList();
        if (component.Children != null)
        {
            component.Children = component.Children.Select(child =>
            {
                if (!string.IsNullOrWhiteSpace(child.GenId)) return RegionPlaceholder(child.GenId);
                CanonicalizeRegion(child);
                return child;
            }).ToList();
        }
        if (component.UseSlots == null) return;
        foreach (var slot in component.UseSlots)
        {
            if (slot.Components == null) continue;
            slot.Components = slot.Components.Select(child =>
            {
                if (!string.IsNullOrWhiteSpace(child.GenId)) return RegionPlaceholder(child.GenId);
                CanonicalizeRegion(child);
                return child;
            }).ToList();
        }
    }

    private static Component RegionPlaceholder(string genId) => new()
    {
        Tag = "codemaster-region",
        GenId = genId
    };

    private static Dictionary<string, RegionDecision> BuildDecisions(
        IReadOnlyDictionary<string, RegionSnapshot> generatedRegions,
        IReadOnlyDictionary<string, RegionSnapshot> currentRegions,
        GeneratedVueRegionSidecar? sidecar)
    {
        var decisions = new Dictionary<string, RegionDecision>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> sidecarRegionIds = sidecar?.Regions.Keys ?? Enumerable.Empty<string>();
        var ids = generatedRegions.Keys
            .Concat(currentRegions.Keys)
            .Concat(sidecarRegionIds)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids)
        {
            generatedRegions.TryGetValue(id, out var generated);
            currentRegions.TryGetValue(id, out var current);
            string? baselineHash = null;
            var hasBaseline = sidecar?.Regions.TryGetValue(id, out baselineHash) == true;
            RegionDecision decision;

            if (generated != null && current != null)
            {
                var safeToReplace = string.Equals(current.Hash, generated.Hash, StringComparison.OrdinalIgnoreCase) ||
                                    hasBaseline && baselineHash != null &&
                                    string.Equals(current.Hash, baselineHash, StringComparison.OrdinalIgnoreCase);
                decision = safeToReplace
                    ? RegionDecision.UseNew(id, generated, current)
                    : RegionDecision.KeepCurrent(id, generated, current, "Generated region was modified outside CodeMaster.");
            }
            else if (generated != null)
            {
                decision = hasBaseline && baselineHash != null
                    ? RegionDecision.Missing(id, generated, "A previously generated data-gen-id anchor is missing from the current file.")
                    : RegionDecision.UseNew(id, generated, null);
            }
            else if (current != null)
            {
                var safeToDelete = hasBaseline && baselineHash != null &&
                                   string.Equals(current.Hash, baselineHash, StringComparison.OrdinalIgnoreCase);
                decision = safeToDelete
                    ? RegionDecision.Delete(id, current)
                    : RegionDecision.KeepCurrent(id, null, current, "Region is no longer generated but contains untracked or external changes.");
            }
            else
            {
                decision = RegionDecision.Delete(id, null);
            }

            decisions[id] = decision;
        }

        return decisions;
    }

    private static List<Component> MergeLists(
        List<Component>? generatedNodes,
        List<Component>? currentNodes,
        IReadOnlyDictionary<string, RegionDecision> decisions,
        IReadOnlyDictionary<string, RegionSnapshot> currentRegions,
        HashSet<string> consumedCurrentRegions,
        bool preferGeneratedShell = false)
    {
        generatedNodes ??= new List<Component>();
        currentNodes ??= new List<Component>();
        var result = new List<Component>();
        var usedCurrentIndexes = new HashSet<int>();

        for (var generatedIndex = 0; generatedIndex < generatedNodes.Count; generatedIndex++)
        {
            var generated = generatedNodes[generatedIndex];
            if (!string.IsNullOrWhiteSpace(generated.GenId) && decisions.TryGetValue(generated.GenId, out var regionDecision))
            {
                switch (regionDecision.Kind)
                {
                    case RegionDecisionKind.UseNew:
                    {
                        var currentRegion = currentRegions.GetValueOrDefault(generated.GenId)?.Component;
                        var generatedShell = Clone(generated);
                        generatedShell.Children = MergeLists(
                            generated.Children,
                            currentRegion?.Children,
                            decisions,
                            currentRegions,
                            consumedCurrentRegions,
                            preferGeneratedShell: true);
                        generatedShell.UseSlots = MergeSlots(
                            generated.UseSlots,
                            currentRegion?.UseSlots,
                            decisions,
                            currentRegions,
                            consumedCurrentRegions,
                            preferGeneratedShell: true);
                        result.Add(generatedShell);
                        if (currentRegion != null) consumedCurrentRegions.Add(generated.GenId);
                        break;
                    }
                    case RegionDecisionKind.KeepCurrent:
                        if (currentRegions.TryGetValue(generated.GenId, out var preservedRegion))
                        {
                            var currentShell = Clone(preservedRegion.Component);
                            currentShell.Children = MergeLists(
                                generated.Children,
                                preservedRegion.Component.Children,
                                decisions,
                                currentRegions,
                                consumedCurrentRegions);
                            currentShell.UseSlots = MergeSlots(
                                generated.UseSlots,
                                preservedRegion.Component.UseSlots,
                                decisions,
                                currentRegions,
                                consumedCurrentRegions);
                            result.Add(currentShell);
                            consumedCurrentRegions.Add(generated.GenId);
                        }
                        break;
                    case RegionDecisionKind.MissingConflict:
                        break;
                }
                continue;
            }

            var currentIndex = FindStructuralMatch(currentNodes, usedCurrentIndexes, generated, generatedIndex);
            if (currentIndex < 0)
            {
                var generatedShell = Clone(generated);
                generatedShell.Children = MergeLists(
                    generated.Children,
                    null,
                    decisions,
                    currentRegions,
                    consumedCurrentRegions);
                generatedShell.UseSlots = MergeSlots(
                    generated.UseSlots,
                    null,
                    decisions,
                    currentRegions,
                    consumedCurrentRegions);
                result.Add(generatedShell);
                continue;
            }

            usedCurrentIndexes.Add(currentIndex);
            var current = currentNodes[currentIndex];
            var mergedShell = preferGeneratedShell ? Clone(generated) : Clone(current);
            mergedShell.Children = MergeLists(
                generated.Children,
                current.Children,
                decisions,
                currentRegions,
                consumedCurrentRegions,
                preferGeneratedShell);
            mergedShell.UseSlots = MergeSlots(
                generated.UseSlots,
                current.UseSlots,
                decisions,
                currentRegions,
                consumedCurrentRegions,
                preferGeneratedShell);
            result.Add(mergedShell);
        }

        for (var index = 0; index < currentNodes.Count; index++)
        {
            if (usedCurrentIndexes.Contains(index)) continue;
            var current = currentNodes[index];
            if (!string.IsNullOrWhiteSpace(current.GenId) && decisions.TryGetValue(current.GenId, out var decision))
            {
                var preserveShell = decision.Kind == RegionDecisionKind.KeepCurrent ||
                                    decision.Kind == RegionDecisionKind.Delete && ContainsPreservedDescendant(current, decisions);
                if (preserveShell && consumedCurrentRegions.Add(current.GenId))
                {
                    var currentShell = Clone(current);
                    currentShell.Children = MergeLists(
                        null,
                        current.Children,
                        decisions,
                        currentRegions,
                        consumedCurrentRegions);
                    currentShell.UseSlots = MergeSlots(
                        null,
                        current.UseSlots,
                        decisions,
                        currentRegions,
                        consumedCurrentRegions);
                    result.Add(currentShell);
                }
                continue;
            }

            var unanchoredShell = Clone(current);
            unanchoredShell.Children = MergeLists(
                null,
                current.Children,
                decisions,
                currentRegions,
                consumedCurrentRegions);
            unanchoredShell.UseSlots = MergeSlots(
                null,
                current.UseSlots,
                decisions,
                currentRegions,
                consumedCurrentRegions);
            result.Add(unanchoredShell);
        }

        return result;
    }

    private static List<ComponentSlot>? MergeSlots(
        List<ComponentSlot>? generatedSlots,
        List<ComponentSlot>? currentSlots,
        IReadOnlyDictionary<string, RegionDecision> decisions,
        IReadOnlyDictionary<string, RegionSnapshot> currentRegions,
        HashSet<string> consumedCurrentRegions,
        bool preferGeneratedShell = false)
    {
        generatedSlots ??= new List<ComponentSlot>();
        currentSlots ??= new List<ComponentSlot>();
        var result = new List<ComponentSlot>();
        var used = new HashSet<int>();
        foreach (var generatedSlot in generatedSlots)
        {
            var currentIndex = -1;
            for (var index = 0; index < currentSlots.Count; index++)
            {
                if (used.Contains(index)) continue;
                var item = currentSlots[index];
                if (!string.Equals(item.Name, generatedSlot.Name, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.Parameter ?? string.Empty, generatedSlot.Parameter ?? string.Empty, StringComparison.Ordinal))
                    continue;
                currentIndex = index;
                break;
            }
            var merged = Clone(generatedSlot);
            if (currentIndex >= 0)
            {
                used.Add(currentIndex);
                merged.Components = MergeLists(
                    generatedSlot.Components,
                    currentSlots[currentIndex].Components,
                    decisions,
                    currentRegions,
                    consumedCurrentRegions,
                    preferGeneratedShell);
            }
            result.Add(merged);
        }

        for (var index = 0; index < currentSlots.Count; index++)
        {
            if (used.Contains(index)) continue;
            var currentOnly = Clone(currentSlots[index]);
            currentOnly.Components = MergeLists(
                null,
                currentSlots[index].Components,
                decisions,
                currentRegions,
                consumedCurrentRegions);
            result.Add(currentOnly);
        }
        return result.Count == 0 ? null : result;
    }

    private static int FindStructuralMatch(
        IReadOnlyList<Component> currentNodes,
        IReadOnlySet<int> used,
        Component generated,
        int preferredIndex)
    {
        if (preferredIndex < currentNodes.Count && !used.Contains(preferredIndex) &&
            IsStructuralMatch(generated, currentNodes[preferredIndex]))
            return preferredIndex;
        for (var index = 0; index < currentNodes.Count; index++)
        {
            if (!used.Contains(index) && IsStructuralMatch(generated, currentNodes[index]))
                return index;
        }
        return -1;
    }

    private static bool IsStructuralMatch(Component generated, Component current)
    {
        if (!string.IsNullOrWhiteSpace(generated.GenId) || !string.IsNullOrWhiteSpace(current.GenId))
            return string.Equals(generated.GenId, current.GenId, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(generated.Tag, current.Tag, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(generated.Id) || !string.IsNullOrWhiteSpace(current.Id))
            return string.Equals(generated.Id, current.Id, StringComparison.Ordinal);
        return true;
    }

    private static bool ContainsPreservedDescendant(
        Component component,
        IReadOnlyDictionary<string, RegionDecision> decisions)
    {
        if (component.Children?.Any(child => IsPreservedRegionOrDescendant(child, decisions)) == true) return true;
        return component.UseSlots?.Any(slot =>
            slot.Components?.Any(child => IsPreservedRegionOrDescendant(child, decisions)) == true) == true;
    }

    private static bool IsPreservedRegionOrDescendant(
        Component component,
        IReadOnlyDictionary<string, RegionDecision> decisions)
    {
        if (!string.IsNullOrWhiteSpace(component.GenId) &&
            decisions.TryGetValue(component.GenId, out var decision) &&
            decision.Kind is RegionDecisionKind.KeepCurrent or RegionDecisionKind.MissingConflict)
            return true;
        return ContainsPreservedDescendant(component, decisions);
    }

    private static string ReplaceTemplate(string generatedVue, string templateWithWrapper)
    {
        var opening = Regex.Match(generatedVue, @"<template(?:\s[^>]*)?>", RegexOptions.IgnoreCase);
        var closings = Regex.Matches(generatedVue, @"</template>", RegexOptions.IgnoreCase);
        if (!opening.Success || closings.Count == 0) return templateWithWrapper + Environment.NewLine + generatedVue;
        var closing = closings[^1];
        return generatedVue[..opening.Index] + templateWithWrapper.TrimEnd() + generatedVue[(closing.Index + closing.Length)..];
    }

    private static GeneratedVueRegionSidecar? DeserializeSidecar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var sidecar = JsonSerializer.Deserialize<GeneratedVueRegionSidecar>(json, JsonOptions);
            return sidecar?.Version == SidecarVersion ? sidecar : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Component Clone(Component component) =>
        JsonSerializer.Deserialize<Component>(JsonSerializer.Serialize(component, JsonOptions), JsonOptions) ?? new Component();

    private static ComponentSlot Clone(ComponentSlot slot) =>
        JsonSerializer.Deserialize<ComponentSlot>(JsonSerializer.Serialize(slot, JsonOptions), JsonOptions) ?? new ComponentSlot();

    private sealed record RegionSnapshot(string GenId, string Hash, Component Component);

    private enum RegionDecisionKind
    {
        UseNew,
        KeepCurrent,
        Delete,
        MissingConflict
    }

    private sealed class RegionDecision
    {
        public string GenId { get; init; } = string.Empty;
        public RegionDecisionKind Kind { get; init; }
        public RegionSnapshot? NewRegion { get; init; }
        public RegionSnapshot? CurrentRegion { get; init; }
        public GeneratedVueRegionConflict? Conflict { get; init; }

        public static RegionDecision UseNew(string id, RegionSnapshot generated, RegionSnapshot? current) =>
            new() { GenId = id, Kind = RegionDecisionKind.UseNew, NewRegion = generated, CurrentRegion = current };

        public static RegionDecision KeepCurrent(
            string id,
            RegionSnapshot? generated,
            RegionSnapshot current,
            string reason) => new()
        {
            GenId = id,
            Kind = RegionDecisionKind.KeepCurrent,
            NewRegion = generated,
            CurrentRegion = current,
            Conflict = new GeneratedVueRegionConflict { GenId = id, Reason = reason }
        };

        public static RegionDecision Delete(string id, RegionSnapshot? current) =>
            new() { GenId = id, Kind = RegionDecisionKind.Delete, CurrentRegion = current };

        public static RegionDecision Missing(string id, RegionSnapshot generated, string reason) => new()
        {
            GenId = id,
            Kind = RegionDecisionKind.MissingConflict,
            NewRegion = generated,
            Conflict = new GeneratedVueRegionConflict { GenId = id, Reason = reason }
        };
    }
}

public sealed class GeneratedVueRegionAnalysis
{
    public string Content { get; init; } = string.Empty;
    public string SidecarJson { get; init; } = string.Empty;
    public IReadOnlyList<GeneratedVueRegionConflict> Conflicts { get; init; } = Array.Empty<GeneratedVueRegionConflict>();
    public bool HasConflicts => Conflicts.Count > 0;
}

public sealed class GeneratedVueRegionConflict
{
    public string GenId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class GeneratedVueRegionSidecar
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedAtUtc { get; set; }
    public string WholeFileHash { get; set; } = string.Empty;
    public Dictionary<string, string?> Regions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GeneratedVueRegionConflict> Conflicts { get; set; } = new();
}
