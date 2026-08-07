using System.Globalization;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Converts field metadata into a stable default control and the small set of
/// props that can be shared by normal and complex-page generators.
/// Explicit control configuration always wins; inference is only used when the
/// metadata is empty or explicitly marked as <c>auto</c>.
/// </summary>
public static class FieldSemanticMapper
{
    private static readonly HashSet<string> ExplicitControls = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "textarea", "number", "select", "switch", "checkbox", "checkbox-group",
        "radio", "radio-group", "date", "datetime", "upload", "file", "image", "editor",
        "select-table", "cascader"
    };

    public static FieldSemanticMapping Map(EntityField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var configured = field.FormControlType?.Trim();
        var controlType = !string.IsNullOrWhiteSpace(configured) &&
                           !string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase)
            ? configured!.ToLowerInvariant()
            : InferControlType(field);

        if (!ExplicitControls.Contains(controlType))
            controlType = InferControlType(field);

        var props = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["multiple"] = field.IsMultiple
        };
        if (field.MaxLength is > 0)
            props["maxlength"] = field.MaxLength.Value;
        if (field.Precision is >= 0)
            props["precision"] = field.Precision.Value;
        if (field.Scale is >= 0)
            props["scale"] = field.Scale.Value;
        if (field.IsEmail)
            props["inputType"] = "email";
        else if (field.IsPhone)
            props["inputType"] = "tel";

        return new FieldSemanticMapping(controlType, props);
    }

    private static string InferControlType(EntityField field)
    {
        if (!string.IsNullOrWhiteSpace(field.RelatedEntityName))
            return "select-table";
        if (!string.IsNullOrWhiteSpace(field.SelectDataSource) || field.IsMultiple)
            return "select";

        var dataType = NormalizeDataType(field.DataType);
        if (dataType is "bool" or "boolean")
            return "switch";
        if (dataType is "datetime" or "datetimeoffset" or "date" or "time")
            return "datetime";
        if (IsNumeric(dataType))
            return "number";
        if (dataType is "byte[]" or "binary" or "stream")
            return "upload";
        if (dataType is "string" or "char" && field.MaxLength is > 500)
            return "textarea";
        return "input";
    }

    private static string NormalizeDataType(string? dataType)
        => (dataType ?? string.Empty)
            .Trim()
            .TrimEnd('?')
            .Replace("System.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

    private static bool IsNumeric(string dataType)
        => dataType is "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or
            "float" or "double" or "decimal";
}

public sealed class FieldSemanticMapping
{
    public FieldSemanticMapping(string controlType, IReadOnlyDictionary<string, object?> props)
    {
        ControlType = controlType;
        Props = props;
    }

    public string ControlType { get; }
    public IReadOnlyDictionary<string, object?> Props { get; }
}
