using System.Text;
using System.Text.Json;
using System.Linq;

namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// Aggregated CodeMaster platform constraints. Returned to the model through
/// the <c>get_codegen_catalog</c> tool so it does not re-derive the framework
/// choice or the generation artifact taxonomy every turn.
/// </summary>
public sealed record PlatformConstraintsDocument
{
    public FrontendConvention Frontend { get; init; } = new();
    public BackendConvention Backend { get; init; } = new();
    public RelationConvention Relations { get; init; } = new();
    public ControlConventionRegistry Controls { get; init; } = new(ControlConvention.Defaults());
    public DictionaryConvention Dictionaries { get; init; } = new();
    public WorkflowConvention Workflow { get; init; } = new();
    public GenerationConvention Generation { get; init; } = new();

    public string RenderForPrompt()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# CodeMaster platform constraints");
        builder.AppendLine();
        builder.AppendLine(Frontend.RenderForPrompt());
        builder.AppendLine();
        builder.AppendLine(Backend.RenderForPrompt());
        builder.AppendLine();
        builder.AppendLine(Relations.RenderForPrompt());
        builder.AppendLine();
        builder.AppendLine("Control conventions (data-driven; the registry can be extended without code changes):");
        builder.AppendLine(Controls.RenderForPrompt());
        builder.AppendLine();
        builder.AppendLine(Dictionaries.RenderForPrompt());
        builder.AppendLine();
        builder.AppendLine(Workflow.RenderForPrompt());
        builder.AppendLine();
        builder.AppendLine(Generation.RenderForPrompt());
        return builder.ToString();
    }
}

/// <summary>
/// Lookup + render for the registered control set. Defaults come from
/// <see cref="ControlConvention.Defaults"/>; production deployments may extend
/// the registry from <c>sys_field_control_templates</c>.
/// </summary>
public sealed class ControlConventionRegistry
{
    private readonly Dictionary<string, ControlConvention> _byId;
    private readonly List<ControlConvention> _all;

    public ControlConventionRegistry(IEnumerable<ControlConvention> controls)
    {
        _all = controls.ToList();
        _byId = _all.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ControlConvention> All => _all;

    public ControlConvention? Find(string id) =>
        _byId.TryGetValue(id, out var c) ? c : null;

    public void Register(ControlConvention control)
    {
        if (_byId.ContainsKey(control.Id))
            return;
        _byId[control.Id] = control;
        _all.Add(control);
    }

    public string RenderForPrompt()
    {
        var header = "Pick the control id from this catalog; the registry stores the SuitableFor description that explains when to use it.";
        var body = string.Join("\n", _all.Select(c => c.RenderForPrompt()));
        return $"{header}\n{body}";
    }

    /// <summary>Pick a control id from a C# type. Returns null when no default fits.</summary>
    public string? RecommendFromCSharpType(string cSharpType, bool hasRelatedEntity, bool relatedEntityIsTree)
    {
        var match = ControlConventionResolver.RecommendFromCSharpType(_all, cSharpType, hasRelatedEntity, relatedEntityIsTree);
        return match?.Id;
    }
}