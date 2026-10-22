namespace CodeMaster.Agent.Services;

/// <summary>
/// Answers whether a UI control can represent a stored field type.
/// </summary>
/// <remarks>
/// The rules mirror the field-control validation that runs when metadata is saved. They are
/// duplicated here on purpose: the agent-facing change set has to make the same decision *before*
/// writing, because a binding that reaches validation is rejected there and takes the whole work-plan
/// stage — and everything queued behind it — down with it. A model that binds a number control to a
/// text column is making a presentation mistake, not a metadata error, so the binding is dropped and
/// the field keeps the control its type already implies.
/// </remarks>
internal static class FieldControlCompatibility
{
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "int16", "int32", "int64", "float", "double", "decimal"
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "text", "char"
    };

    private static readonly HashSet<string> TemporalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "dateonly", "datetimeoffset", "date", "time"
    };

    /// <summary>
    /// True when <paramref name="control"/> is acceptable for <paramref name="dataType"/>, including
    /// when either side is unknown — an absent control or type is not evidence of a conflict.
    /// </summary>
    public static bool Accepts(string? control, string? dataType)
    {
        var normalizedControl = (control ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedType = (dataType ?? string.Empty).Trim().TrimEnd('?').ToLowerInvariant();
        if (normalizedControl.Length == 0 || normalizedType.Length == 0)
            return true;

        return normalizedControl switch
        {
            "switch" or "checkbox" => normalizedType == "bool",
            "number" => NumericTypes.Contains(normalizedType),
            "date" or "datetime" => TemporalTypes.Contains(normalizedType),
            "textarea" or "editor" or "file" or "image" => TextTypes.Contains(normalizedType),
            _ => true
        };
    }
}
