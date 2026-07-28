namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Central naming rules for generated frontend identifiers and filesystem/route paths.
/// </summary>
public static class CodeGenNaming
{
    /// <summary>
    /// Converts a PascalCase C# name to the lower-camel form used by JavaScript identifiers.
    /// </summary>
    public static string ToJavaScriptIdentifier(string value)
    {
        return string.IsNullOrEmpty(value)
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];
    }

    /// <summary>
    /// Converts a technical name to the canonical lowercase path segment used by generated
    /// folders, API module paths, Vue routes, and menu component paths.
    /// </summary>
    public static string ToPathSegment(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }

}
