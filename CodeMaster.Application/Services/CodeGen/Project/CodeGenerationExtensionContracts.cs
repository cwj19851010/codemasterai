using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Extensibility;
using CodeMaster.Infrastructure.VueParser.Model;

namespace CodeMaster.Application.Services.CodeGen;

public interface ICodeGenerationMetadataObserver : ICodeMasterExtension
{
    Task EntityChangedAsync(long entityId);
}

public interface IGenerationBundleContributor : ICodeMasterExtension
{
    Task ContributeAsync(long projectId, GenerationBundleDto bundle);
}

/// <summary>
/// Project-level generation hook. Entity generation loops over entities; the pages a
/// complex-page task creates live in their own metadata and are only marked NeedsSync
/// when something they bind changes. Without this hook nothing in the entity loop ever
/// touches them, so a project whose pages were created by the Agent materializes with
/// its list/detail pages only and every business page stuck on NeedsSync.
/// Implementations run after the entity pass and must not throw: a page that fails is
/// recorded on the page row itself.
/// </summary>
public interface IProjectCodeGenerationObserver : ICodeMasterExtension
{
    Task ProjectGeneratedAsync(ProjectCodeGenerationContext context);
}

public sealed class ProjectCodeGenerationContext
{
    public long ProjectId { get; init; }
    public bool Incremental { get; init; }

    /// <summary>Aggregate roots the run actually regenerated; empty means nothing changed.</summary>
    public IReadOnlyList<long> EntityIds { get; init; } = Array.Empty<long>();
}

/// <summary>
/// Optional per-entity output that rides along with standard generation, for companion
/// targets that mirror the main slice instead of replacing it (a second front end, a
/// printable layout, and so on). Called after the entity's own slice has been written.
/// </summary>
/// <remarks>
/// The core generator knows nothing about what an implementation produces: it only hands
/// over the identity of the entity and whether this run was tree-only. Implementations own
/// their own files, their own incremental logic and their own failure handling — throwing
/// here must not be what turns a successful generation into a failed one, so the caller
/// swallows exceptions and records nothing.
/// </remarks>
public interface IEntityCodeGenerationObserver : ICodeMasterExtension
{
    Task EntityGeneratedAsync(EntityCodeGenerationContext context);
}

public sealed class EntityCodeGenerationContext
{
    public long ProjectId { get; init; }
    public long EntityId { get; init; }

    /// <summary>Only the component tree was refreshed; no code was written for this entity.</summary>
    public bool TreeOnly { get; init; }
}

public interface ILocalExecutionCompletionHandler : ICodeMasterExtension
{
    bool CanHandle(string action);
    Task HandleAsync(LocalExecutionCompleteDto input, DateTime completedAt);
}

public interface IGeneratedProjectInitializer : ICodeMasterExtension
{
    int ContractVersion { get; }
    Task InitializeAsync(string projectPath, string projectName);
}

public interface IComponentTreeMergeService : ICodeMasterExtension
{
    string MergeOrder(string newTreeJson, string oldTreeJson);
    string MergeIncremental(string newTreeJson, string oldTreeJson, ComponentTreeChangeSet changes);
}

/// <summary>
/// Optional page-generation extension point.  The public contract only carries
/// generic generation data; feature-specific data-source policies stay in the
/// private UI-composition module.
/// </summary>
public interface ICodeGenerationPageExtension : ICodeMasterExtension
{
    Task ProcessPageAsync(CodeGenerationPageContext context);
    Task ContributeIncrementalChangesAsync(long entityId, DateTime since, ComponentTreeChangeSet changes);
}

/// <summary>
/// Optional entity-generation override. Implementations can take ownership of a
/// metadata entity and emit a specialized output set while the standard CRUD
/// generation path remains unchanged for all other entities.
/// </summary>
public interface ICodeGenerationEntityExtension : ICodeMasterExtension
{
    Task<bool> CanHandleAsync(long entityId);
    Task GenerateAsync(CodeGenerationEntityContext context);
    Task<bool> IsOutputCurrentAsync(CodeGenerationEntityContext context);
}

public sealed class CodeGenerationEntityContext
{
    public long EntityId { get; init; }
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string ModuleName { get; init; } = string.Empty;
    public string EntityName { get; init; } = string.Empty;
    public bool TreeOnly { get; init; }
    public bool Incremental { get; init; }
}

public sealed class CodeGenerationPageContext
{
    public long EntityId { get; init; }
    public string PageType { get; init; } = string.Empty;
    public PageGenerationResult Result { get; init; } = new();
}

public sealed class ComponentTreeChangeSet
{
    public bool Enabled { get; init; } = true;
    public DateTime? Since { get; init; }
    public HashSet<long> ChangedFieldIds { get; } = new();
    public HashSet<long> DeletedFieldIds { get; } = new();
    public HashSet<string> ChangedRelationNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeletedRelationNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> ChangedEntityRelationIds { get; } = new();
    public HashSet<long> DeletedEntityRelationIds { get; } = new();
    public HashSet<string> ChangedGeneratedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DeletedGeneratedIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> ChangedDataSourceIds { get; } = new();
    public HashSet<long> DeletedDataSourceIds { get; } = new();

    public bool HasChanges => ChangedFieldIds.Count > 0 || DeletedFieldIds.Count > 0 ||
                              ChangedRelationNames.Count > 0 || DeletedRelationNames.Count > 0 ||
                              ChangedEntityRelationIds.Count > 0 || DeletedEntityRelationIds.Count > 0 ||
                              ChangedGeneratedIds.Count > 0 || DeletedGeneratedIds.Count > 0 ||
                              ChangedDataSourceIds.Count > 0 || DeletedDataSourceIds.Count > 0;

    public bool IsChangedGeneratedId(string genId)
    {
        if (ChangedGeneratedIds.Contains(genId) || DeletedGeneratedIds.Contains(genId)) return true;
        if (TryParseGeneratedFieldId(genId, out var fieldId))
            return ChangedFieldIds.Contains(fieldId) || DeletedFieldIds.Contains(fieldId);
        if (TryParseOwnedRelationId(genId, out var relationId))
            return ChangedEntityRelationIds.Contains(relationId) || DeletedEntityRelationIds.Contains(relationId);

        const string childPrefix = "gen_child_";
        if (!genId.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var childName = genId[childPrefix.Length..];
        return ChangedRelationNames.Contains(childName) || DeletedRelationNames.Contains(childName);
    }

    private static bool TryParseGeneratedFieldId(string genId, out long fieldId)
    {
        fieldId = 0;
        if (genId.StartsWith("gen_owned_", StringComparison.OrdinalIgnoreCase))
        {
            var markerIndex = genId.IndexOf("_field_", StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) markerIndex = genId.IndexOf("_col_", StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var markerLength = genId[markerIndex..].StartsWith("_field_", StringComparison.OrdinalIgnoreCase) ? 7 : 5;
                return TryParseNumber(genId, markerIndex + markerLength, out fieldId);
            }
        }

        var prefix = genId.StartsWith("gen_field_", StringComparison.OrdinalIgnoreCase)
            ? "gen_field_"
            : genId.StartsWith("gen_col_", StringComparison.OrdinalIgnoreCase) ? "gen_col_" : null;
        return prefix != null && TryParseNumber(genId, prefix.Length, out fieldId);
    }

    private static bool TryParseOwnedRelationId(string genId, out long relationId)
    {
        relationId = 0;
        const string prefix = "gen_owned_";
        return genId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               TryParseNumber(genId, prefix.Length, out relationId);
    }

    private static bool TryParseNumber(string value, int start, out long number)
    {
        number = 0;
        var end = start;
        while (end < value.Length && char.IsDigit(value[end])) end++;
        return end > start && long.TryParse(value[start..end], out number);
    }
}
