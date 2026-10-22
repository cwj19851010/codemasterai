using System.Text.RegularExpressions;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Qualifies root-namespace references inside generated framework files with <c>global::</c>.
///
/// A generated project's namespaces are <c>{Root}.{Layer}.{Module}</c>. When a module is named
/// exactly like the root namespace (a natural choice — project "EngineeringMgmt" with module
/// "EngineeringMgmt"), a sibling file under <c>{Root}.{Layer}.System</c> can no longer resolve the
/// bare root name: the enclosing <c>{Root}.{Layer}</c> namespace contains a member with that exact
/// name (the module namespace itself), so the compiler stops there and reports
/// <c>CS0234: the namespace '{Root}.{Layer}.{Module}' does not contain a definition for '{Layer}'</c>.
/// That breaks the whole generated solution, and only for that naming shape.
///
/// <c>global::</c> pins the lookup to the global namespace and makes the reference unambiguous
/// whatever the module is called. It is applied to text files coming from the template at
/// extraction time, so no template source is edited.
/// </summary>
internal static class GeneratedProjectNamespaceQualifier
{
    /// <summary>
    /// Prefixes <c>{projectName}.</c> with <c>global::</c> on every reference that sits inside a
    /// namespace declaration. References in <c>using</c> directives above the namespace are already
    /// resolved from the global scope and are left untouched.
    /// </summary>
    public static string QualifyRootReferences(string content, string projectName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(projectName))
            return content;

        var pattern = new Regex(
            @"(?<![A-Za-z0-9_.])" + Regex.Escape(projectName) + @"\.",
            RegexOptions.Compiled);

        var lines = content.Split('\n');
        var namespaceIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("namespace ", StringComparison.Ordinal))
            {
                namespaceIndex = i;
                break;
            }
        }

        if (namespaceIndex < 0)
            return content;

        for (var i = namespaceIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//"))
                continue;

            lines[i] = pattern.Replace(line, match =>
            {
                // Never rewrite a reference that lives inside a string literal: generator code
                // compares namespace strings (e.g. assemblySymbol.Name == "Root.Domain") and a
                // global:: prefix there would break the comparison instead of qualifying a type.
                return IsInsideStringLiteral(line, match.Index)
                    ? match.Value
                    : "global::" + match.Value;
            });
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// True when the character position sits inside a double-quoted string on that line. Good
    /// enough for the single-line references the template contains; a multi-line string that
    /// starts before the namespace declaration is not a case the template uses.
    /// </summary>
    private static bool IsInsideStringLiteral(string line, int index)
    {
        var quotes = 0;
        for (var i = 0; i < index; i++)
        {
            if (line[i] != '"')
                continue;
            // Skip an escaped quote.
            if (i > 0 && line[i - 1] == '\\')
                continue;
            quotes++;
        }

        return quotes % 2 == 1;
    }
}
