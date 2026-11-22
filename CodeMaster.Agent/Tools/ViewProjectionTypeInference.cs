using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;

namespace CodeMaster.Agent.Tools;

/// <summary>
/// Derives the C# type of a view output column from the SELECT projection that produces it.
/// </summary>
/// <remarks>
/// A read-only view consumer gets its property types from the declared field list, and that list is
/// routinely written with <c>string</c> for every column that is not the key — both by the model and
/// by the platform's own projection reader. For a profit view that means <c>ContractAmount</c>,
/// <c>NetProfit</c> and friends are declared as text even though the SQL projects numeric
/// expressions, so the later field-control stage has every reason to ask for a number control on a
/// field that is still typed as text, and validation then stops the whole work plan on metadata the
/// platform itself produced.
///
/// The projection is the authority on what the column actually contains, so read the type from it
/// instead of trusting the placeholder. Only a type this class can prove is returned; callers keep
/// whatever was declared when nothing can be proven.
/// </remarks>
internal static class ViewProjectionTypeInference
{
    /// <summary>Words that follow a table name in FROM/JOIN and are therefore never an alias.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "on", "left", "right", "inner", "outer", "join", "cross", "full", "natural",
        "group", "order", "having", "union", "limit", "offset", "and", "or", "set", "using",
        "as", "into", "for", "with"
    };

    private static readonly HashSet<string> IntegralTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "int16", "int32", "int64"
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "text", "char"
    };

    private static readonly HashSet<string> TemporalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "dateonly", "datetimeoffset", "date", "time"
    };

    /// <summary>Aggregates whose result is always a decimal-typed number.</summary>
    private static readonly HashSet<string> DecimalAggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "avg", "average", "total"
    };

    /// <summary>Functions that return the type of their first resolvable argument.</summary>
    private static readonly HashSet<string> FirstArgumentFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "coalesce", "ifnull", "nvl", "isnull", "nullif", "abs", "round", "trunc", "truncate",
        "ceil", "ceiling", "floor", "greatest", "least", "min", "max"
    };

    /// <summary>Functions that always return text.</summary>
    private static readonly HashSet<string> TextFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "length", "len", "upper", "lower", "trim", "ltrim", "rtrim", "substr", "substring",
        "replace", "concat", "to_char", "date_format", "strftime", "format", "left", "right",
        "regexp_replace"
    };

    /// <summary>Functions that always return a timestamp.</summary>
    private static readonly HashSet<string> TimestampFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "now", "current_timestamp", "getdate", "sysdatetime", "sysutcdatetime", "utc_timestamp",
        "current_date"
    };

    private static readonly Regex FromOrJoin = new(
        @"\b(?:from|join)\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s+(?:(?:as)\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the proven C# type for the projection exposed as <paramref name="outputName"/>, or
    /// null when the SQL does not identify one.
    /// </summary>
    public static string? Infer(string? sql, IReadOnlyList<ViewBuilderEntityDto> catalog, string outputName)
    {
        if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(outputName))
            return null;

        var expression = FindProjectionExpression(sql!, outputName);
        if (expression == null)
            return null;

        return InferExpression(expression, catalog, BuildAliasMap(sql!));
    }

    /// <summary>
    /// Maps the aliases a hand-written SELECT introduces (<c>FROM Project p</c>) onto the physical
    /// table names the catalog knows. Without it every <c>p.ContractAmount</c> in a projection would
    /// look unresolvable, which is the normal shape of the SQL the agent writes.
    /// </summary>
    private static Dictionary<string, string> BuildAliasMap(string sql)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FromOrJoin.Matches(sql))
        {
            var table = match.Groups[1].Value.Replace(" ", string.Empty);
            var alias = match.Groups[2].Value;
            if (ReservedWords.Contains(alias))
                continue;
            aliases[alias] = table;
            // The physical name stays usable too: "Project.ContractAmount" needs no alias entry.
            aliases.TryAdd(table, table);
            var bare = table[(table.LastIndexOf('.') + 1)..];
            aliases.TryAdd(bare, table);
        }

        return aliases;
    }

    /// <summary>
    /// Locates the expression behind an output name: the projection's AS alias, or — for a bare
    /// column reference — the column itself.
    /// </summary>
    private static string? FindProjectionExpression(string sql, string outputName)
    {
        var selectAt = IndexOfKeyword(sql, "select");
        if (selectAt < 0)
            return null;
        var fromAt = IndexOfKeyword(sql, "from", selectAt + "select".Length);
        if (fromAt < 0)
            return null;

        foreach (var projection in SplitTopLevel(sql[(selectAt + "select".Length)..fromAt]))
        {
            var asAt = IndexOfKeyword(projection, "as");
            if (asAt >= 0)
            {
                if (string.Equals(TrimIdentifier(projection[(asAt + 2)..]), outputName, StringComparison.OrdinalIgnoreCase))
                    return projection[..asAt].Trim();
                continue;
            }

            // No alias: the output name is the trailing identifier, so "p.Amount" answers to "Amount".
            var separator = projection.LastIndexOf('.');
            var trailing = TrimIdentifier(separator >= 0 ? projection[(separator + 1)..] : projection);
            if (string.Equals(trailing, outputName, StringComparison.OrdinalIgnoreCase))
                return projection.Trim();
        }

        return null;
    }

    private static string? InferExpression(
        string expression,
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        IReadOnlyDictionary<string, string> aliases)
    {
        var expr = StripOuterParentheses(expression.Trim());
        if (expr.Length == 0)
            return null;

        if (IsQuotedLiteral(expr))
            return "string";

        if (IsNumericLiteral(expr))
            return expr.Contains('.') ? "decimal" : "long";

        // A bare or qualified column reference: read the source field's type.
        var (qualifier, column) = SplitQualifiedColumn(expr);
        if (column != null)
            return ResolveColumnType(catalog, aliases, qualifier, column);

        var (name, arguments) = ReadFunctionCall(expr);
        if (name != null)
            return InferFunction(name, arguments, catalog, aliases);

        if (expr.StartsWith("case ", StringComparison.OrdinalIgnoreCase) ||
            expr.StartsWith("case(", StringComparison.OrdinalIgnoreCase))
            return InferCase(expr, catalog, aliases);

        return InferArithmetic(expr, catalog, aliases);
    }

    private static string? InferFunction(
        string name,
        string arguments,
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (DecimalAggregates.Contains(name))
            return "decimal";
        if (string.Equals(name, "count", StringComparison.OrdinalIgnoreCase))
            return "long";
        if (TextFunctions.Contains(name))
            return "string";
        if (TimestampFunctions.Contains(name))
            return "DateTime";
        if (string.Equals(name, "cast", StringComparison.OrdinalIgnoreCase))
            return ReadCastTarget(arguments);

        if (!FirstArgumentFunctions.Contains(name))
            return null;

        var parts = SplitTopLevel(arguments);

        // A literal argument is a null fallback, not the type: COALESCE(r.Total, 0) must read as a
        // number, and "0" alone would say integral. Resolve the real arguments first.
        foreach (var part in parts)
        {
            var trimmed = StripOuterParentheses(part.Trim());
            if (IsNumericLiteral(trimmed) || IsQuotedLiteral(trimmed))
                continue;

            var inferred = InferExpression(part, catalog, aliases);
            if (inferred != null)
                return inferred;
        }

        // Everything the catalog could not resolve: a numeric fallback still says "number", which is
        // what a money projection over a derived table needs.
        if (parts.Any(part => IsNumericLiteral(StripOuterParentheses(part.Trim()))))
            return "decimal";

        return parts.Any(part => IsQuotedLiteral(StripOuterParentheses(part.Trim()))) ? "string" : null;
    }

    private static string? InferCase(
        string expression,
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        IReadOnlyDictionary<string, string> aliases)
    {
        var thenAt = IndexOfKeyword(expression, "then");
        if (thenAt < 0)
            return null;

        var rest = expression[(thenAt + "then".Length)..];
        var endAt = IndexOfKeyword(rest, "when");
        var elseAt = IndexOfKeyword(rest, "else");
        var end = new[] { endAt, elseAt }.Where(index => index >= 0).DefaultIfEmpty(rest.Length).Min();
        return InferExpression(rest[..end], catalog, aliases);
    }

    /// <summary>
    /// Infers arithmetic from its operands: decimal when any operand is decimal or unresolvable,
    /// integral when every operand the catalog can resolve is integral, and nothing at all when an
    /// operand is text or temporal — concatenation and date maths are not numbers.
    /// </summary>
    private static string? InferArithmetic(
        string expression,
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (!HasTopLevelArithmetic(expression))
            return null;

        var operands = SplitArithmeticOperands(expression)
            .Select(operand => StripOuterParentheses(operand.Trim()))
            .Where(operand => operand.Length > 0)
            .ToList();

        // Every step has to shrink the expression. If the split produced the input again, the shape
        // was misread — return instead of recursing on it, so no parser gap can ever hang a request.
        if (operands.Count == 0 || (operands.Count == 1 && operands[0] == expression))
            return null;

        var resolved = operands
            .Select(operand => InferExpression(operand, catalog, aliases))
            .ToList();

        if (resolved.Any(type => type != null && (TextTypes.Contains(type) || TemporalTypes.Contains(type))))
            return null;
        if (resolved.All(type => type == null))
            return null;

        var numeric = resolved.Where(type => type != null).Select(type => type!).ToList();
        if (numeric.Any(type => !IntegralTypes.Contains(type)))
            return "decimal";
        return expression.Contains('.') ? "decimal" : "long";
    }

    /// <summary>Reads a numeric/temporal type out of <c>CAST(x AS numeric(18,2))</c>.</summary>
    private static string? ReadCastTarget(string arguments)
    {
        var asAt = IndexOfKeyword(arguments, "as");
        if (asAt < 0)
            return null;

        var target = TrimIdentifier(arguments[(asAt + "as".Length)..]).ToLowerInvariant();
        return target switch
        {
            "int" or "integer" or "int4" or "smallint" or "int2" => "int",
            "bigint" or "int8" => "long",
            "numeric" or "decimal" or "money" or "float8" or "double" => "decimal",
            "real" or "float" or "float4" => "double",
            "text" or "varchar" or "char" or "nvarchar" or "nchar" => "string",
            "date" => "DateOnly",
            "timestamp" or "datetime" => "DateTime",
            "boolean" or "bool" or "bit" => "bool",
            _ => null
        };
    }

    private static string? ResolveColumnType(
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        IReadOnlyDictionary<string, string> aliases,
        string? qualifier,
        string column)
    {
        var table = qualifier == null
            ? null
            : aliases.TryGetValue(qualifier, out var mapped) ? mapped : qualifier;

        var candidates = catalog
            .Where(entity => table == null ||
                             string.Equals(entity.TableName, table, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entity.EntityName, table, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(entity.TableName, qualifier, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entity => entity.Fields
                .Where(field => string.Equals(field.ColumnName, column, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(field.Name, column, StringComparison.OrdinalIgnoreCase))
                .Select(field => field.DataType))
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An unqualified column is only usable when the whole catalog agrees on its type; the same
        // name in two entities is exactly the case where guessing would be wrong.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static (string? Qualifier, string? Column) SplitQualifiedColumn(string expression)
    {
        if (!IsIdentifierChain(expression))
            return (null, null);

        var separator = expression.LastIndexOf('.');
        return separator < 0
            ? (null, expression)
            : (expression[..separator], expression[(separator + 1)..]);
    }

    private static (string? Name, string Arguments) ReadFunctionCall(string expression)
    {
        var open = expression.IndexOf('(');
        if (open <= 0 || expression[^1] != ')')
            return (null, string.Empty);

        var name = expression[..open].Trim();
        if (!IsIdentifierChain(name))
            return (null, string.Empty);

        // The call has to own the whole expression. "COALESCE(x, 0) - COALESCE(y, 0)" also starts
        // with a call and ends with ')', but its first '(' closes long before the end; reading it as
        // one call made the argument list a compound expression of the same shape, and the recursion
        // never shrank — it overflowed the stack and took the whole request down.
        if (!ClosesAtEnd(expression, open))
            return (null, string.Empty);

        return (name[(name.LastIndexOf('.') + 1)..], expression[(open + 1)..^1]);
    }

    /// <summary>True when the '(' at <paramref name="openIndex"/> is closed by the final character.</summary>
    private static bool ClosesAtEnd(string text, int openIndex)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'' || c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote)
                continue;
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    return i == text.Length - 1;
            }
        }

        return false;
    }

    private static bool IsIdentifierChain(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var segment in value.Split('.'))
        {
            if (segment.Length == 0)
                return false;
            if (!char.IsLetter(segment[0]) && segment[0] != '_')
                return false;
            if (segment.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
                return false;
        }

        return true;
    }

    private static bool IsQuotedLiteral(string value)
    {
        if (value.Length < 2)
            return false;
        return (value[0] == '\'' && value[^1] == '\'') ||
               (value[0] == '"' && value[^1] == '"');
    }

    private static bool IsNumericLiteral(string value) =>
        value.Length > 0 &&
        value.Any(char.IsDigit) &&
        value.All(c => char.IsDigit(c) || c == '.' || c == '-' || c == '+');

    private static string StripOuterParentheses(string value)
    {
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && IsBalancedWrapper(value))
            value = value[1..^1].Trim();
        return value;
    }

    /// <summary>True when the first '(' closes at the very end, so the whole value is one group.</summary>
    private static bool IsBalancedWrapper(string value)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\'' || c == '"')
                inQuote = !inQuote;
            if (inQuote)
                continue;
            if (c == '(')
                depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                    return i == value.Length - 1;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an operator sits outside every parenthesis. The splitter only cuts there, so an
    /// operator inside a call argument must not make the expression look arithmetic — otherwise the
    /// split returns the expression unchanged and the caller recurses on itself.
    /// </summary>
    private static bool HasTopLevelArithmetic(string expression)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\'' || c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote)
                continue;
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                depth--;
                continue;
            }
            if (depth != 0)
                continue;
            if (IsArithmeticOperator(expression, i))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the character at <paramref name="index"/> is a binary operator. A '-' is only one
    /// when something precedes it: it is a sign at the start of an operand, and whitespace around an
    /// operator ("Amount - Cost") must not hide it.
    /// </summary>
    private static bool IsArithmeticOperator(string expression, int index)
    {
        var c = expression[index];
        if (c is '+' or '*' or '/')
            return true;
        if (c != '-')
            return false;

        var previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
            previous--;
        if (previous < 0)
            return false;
        // "->" is a JSON operator and "--" starts a comment.
        return expression[previous] != '>' && expression[previous] != '-' &&
               (char.IsLetterOrDigit(expression[previous]) || expression[previous] == ')' ||
                expression[previous] == '_' || expression[previous] == '\'' || expression[previous] == '"');
    }

    private static List<string> SplitArithmeticOperands(string expression)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '\'' || c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote)
                continue;
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')')
            {
                depth--;
                continue;
            }
            if (depth != 0)
                continue;
            if (!IsArithmeticOperator(expression, i))
                continue;

            parts.Add(expression[start..i]);
            start = i + 1;
        }

        parts.Add(expression[start..]);
        return parts;
    }

    /// <summary>Finds a whole-word keyword outside any parentheses, so a subquery never matches.</summary>
    private static int IndexOfKeyword(string text, string keyword, int startIndex = 0)
    {
        var depth = 0;
        var inQuote = false;
        for (var i = startIndex; i <= text.Length - keyword.Length; i++)
        {
            var c = text[i];
            if (c == '\'' || c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote)
                continue;
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0)
                continue;
            if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
                continue;
            if (string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            var after = i + keyword.Length;
            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
                continue;
            return i;
        }

        return -1;
    }

    /// <summary>Splits a projection on the commas that sit outside every parenthesis.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'' || c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote)
                continue;
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    private static string TrimIdentifier(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '`' && text[^1] == '`') || (text[0] == '[' && text[^1] == ']')))
            return text[1..^1].Trim();

        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;

        return end == 0 ? string.Empty : text[..end];
    }
}
