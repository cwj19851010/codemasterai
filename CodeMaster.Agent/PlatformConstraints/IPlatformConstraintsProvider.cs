using System.Text;
using System.Text.Json;

namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// DI-facing provider for the platform constraints document. Wired into the
/// Agent service collection so the <c>get_codegen_catalog</c> tool can read
/// from a single source of truth.
/// </summary>
public interface IPlatformConstraintsProvider
{
    PlatformConstraintsDocument Current { get; }

    /// <summary>Serialized document used by the agent's catalog tool.</summary>
    string GetSerializedDocument();

    /// <summary>Plain-text rendering used as an injected system prompt section.</summary>
    string GetPromptDocument();
}

public sealed class PlatformConstraintsProvider : IPlatformConstraintsProvider
{
    private PlatformConstraintsDocument _current = new();

    public PlatformConstraintsDocument Current => _current;

    public PlatformConstraintsProvider() { }

    public void Replace(PlatformConstraintsDocument document)
    {
        _current = document;
    }

    public string GetSerializedDocument()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(_current, options);
    }

    public string GetPromptDocument() => _current.RenderForPrompt();
}