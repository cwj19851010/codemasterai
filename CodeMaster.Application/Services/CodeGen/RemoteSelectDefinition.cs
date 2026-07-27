using System.Text.Json;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Metadata contract for a normal select whose options come from another entity.
/// The contract deliberately reuses the existing EntityField columns so old
/// projects do not need a schema migration.
///
/// SelectDataSource = api
/// RelatedEntityName = source entity name
/// RelatedEntityIdField = value field on the source entity
/// RelatedEntityDisplayFields = JSON string array of label fields
/// SelectOptions = { "parentField": "ProvinceCode", "parentValueField": "ParentCode", "loadMode": "batch" }
/// </summary>
public sealed class RemoteSelectDefinition
{
    public string ParentField { get; init; } = string.Empty;
    public string ParentValueField { get; init; } = string.Empty;
    public string LoadMode { get; init; } = "batch";

    public bool HasParent => !string.IsNullOrWhiteSpace(ParentField);

    public static bool IsRemote(EntityField? field) =>
        field != null &&
        string.Equals(field.FormControlType, "select", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(field.SelectDataSource, "api", StringComparison.OrdinalIgnoreCase);

    public static RemoteSelectDefinition Parse(EntityField field)
    {
        if (!IsRemote(field))
            throw new InvalidOperationException($"Field '{field.Name}' is not an api select.");

        var parentField = string.Empty;
        var parentValueField = string.Empty;
        var loadMode = "batch";
        if (!string.IsNullOrWhiteSpace(field.SelectOptions))
        {
            try
            {
                using var document = JsonDocument.Parse(field.SelectOptions);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    parentField = ReadString(document.RootElement, "parentField");
                    parentValueField = ReadString(document.RootElement, "parentValueField");
                    loadMode = ReadString(document.RootElement, "loadMode");
                }
            }
            catch (JsonException)
            {
                // Validation reports the actionable metadata error; generation
                // keeps the default batch mode for backward-compatible recovery.
            }
        }

        return new RemoteSelectDefinition
        {
            ParentField = parentField,
            ParentValueField = string.IsNullOrWhiteSpace(parentValueField)
                ? DefaultParentValueField(parentField)
                : parentValueField,
            LoadMode = string.IsNullOrWhiteSpace(loadMode) ? "batch" : loadMode
        };
    }

    public static List<string> ParseDisplayFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>();
        }
        catch (JsonException)
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        return value.GetString()?.Trim() ?? string.Empty;
    }

    private static string DefaultParentValueField(string parentField) =>
        string.IsNullOrWhiteSpace(parentField) ? string.Empty : parentField;
}
