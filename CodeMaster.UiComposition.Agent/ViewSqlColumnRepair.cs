using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;

namespace CodeMaster.UiComposition.Agent;

/// <summary>
/// Repairs the column references a hand-written view SELECT gets wrong, before anything reads it.
/// </summary>
/// <remarks>
/// The view SQL is written by the model from the entity catalog, and the mistake it makes most often
/// is naming a master table's primary key after the child's foreign key: it writes
/// <c>s.SupplierId</c> for <c>eng_supplier</c>, or <c>lc.LaborCompanyId</c> for
/// <c>eng_labor_company</c>, because <c>eng_purchase_contract.SupplierId</c> and
/// <c>eng_labor_contract.LaborCompanyId</c> do exist. The table it is joining from has no such
/// column — its key is <c>Id</c> — so the physical view is created against a column that cannot
/// exist.
///
/// Refusing the plan does not help: the work-plan task stores the model's definition verbatim and a
/// retry replays it, so the same mistake is reported forever while every stage behind Views stays
/// blocked. The catalog is the authority on what a managed table actually has, so the reference is
/// rewritten to the primary key and the rewrite is reported like every other automatic repair.
///
/// Only a qualified reference onto a managed table is considered, and only when the column provably
/// does not exist there while <c>&lt;EntityName&gt;Id</c> is exactly what was written. Anything else —
/// an external table, an alias the SQL does not declare, a column that exists, a name that is simply
/// misspelled — is left untouched rather than guessed at.
/// </remarks>
internal static class ViewSqlColumnRepair
{
    /// <summary>FROM/JOIN target and its optional alias. The alias is optional so a bare
    /// <c>FROM eng_supplier</c> still registers the table and its own name as qualifiers.</summary>
    private static readonly Regex FromOrJoin = new(
        @"\b(?:from|join)\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)(?:\s+(?:as\s+)?([A-Za-z_][A-Za-z0-9_]*))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QualifiedReference = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    /// <summary>Words that follow a table name in FROM/JOIN and are therefore never an alias.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "on", "left", "right", "inner", "outer", "join", "cross", "full", "natural",
        "group", "order", "having", "union", "limit", "offset", "and", "or", "set", "using",
        "as", "into", "for", "with"
    };

    /// <summary>The repaired SQL plus one sentence per rewrite, in the order they were made.</summary>
    internal sealed record Outcome(string Sql, List<string> Repairs);

    public static Outcome Repair(string? sql, IReadOnlyList<ViewBuilderEntityDto> catalog)
    {
        if (string.IsNullOrWhiteSpace(sql) || catalog.Count == 0)
            return new Outcome(sql ?? string.Empty, []);

        var aliases = BuildAliasMap(sql!, catalog);
        if (aliases.Count == 0)
            return new Outcome(sql!, []);

        var repairs = new List<string>();
        var repaired = QualifiedReference.Replace(sql!, match =>
        {
            var qualifier = match.Groups[1].Value;
            var column = match.Groups[2].Value;
            if (!aliases.TryGetValue(qualifier, out var entity))
                return match.Value;
            if (entity.Fields.Count == 0 || HasField(entity, column))
                return match.Value;

            var target = ResolvePrimaryKey(entity, column);
            if (target == null)
                return match.Value;

            repairs.Add(
                $"Read '{qualifier}.{column}' as '{qualifier}.{target}': table '{entity.TableName}' has no '{column}' column and '{target}' is its primary key.");
            return $"{qualifier}.{target}";
        });

        return new Outcome(repaired, repairs);
    }

    /// <summary>
    /// Maps every qualifier the SQL can legitimately use onto the catalog entity it stands for:
    /// the FROM/JOIN alias, the table name as written, and both the entity and physical table names.
    /// </summary>
    /// <remarks>
    /// A qualifier that two different entities answer to is dropped rather than guessed: the same
    /// short alias reused across <c>UNION</c> branches is exactly the case where rewriting on a
    /// guess would corrupt a working query.
    /// </remarks>
    private static Dictionary<string, ViewBuilderEntityDto> BuildAliasMap(
        string sql,
        IReadOnlyList<ViewBuilderEntityDto> catalog)
    {
        var candidates = new Dictionary<string, ViewBuilderEntityDto?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FromOrJoin.Matches(sql))
        {
            var table = match.Groups[1].Value.Replace(" ", string.Empty);
            var entity = FindEntity(catalog, table);
            if (entity == null)
                continue;

            Register(candidates, table, entity);
            if (entity.TableName.Length > 0)
                Register(candidates, entity.TableName, entity);
            if (entity.EntityName.Length > 0)
                Register(candidates, entity.EntityName, entity);

            var alias = match.Groups[2].Value;
            if (alias.Length == 0 || ReservedWords.Contains(alias))
                continue;
            Register(candidates, alias, entity);
        }

        var result = new Dictionary<string, ViewBuilderEntityDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entity) in candidates)
        {
            if (entity != null)
                result[key] = entity;
        }

        return result;
    }

    private static void Register(
        Dictionary<string, ViewBuilderEntityDto?> candidates,
        string key,
        ViewBuilderEntityDto entity)
    {
        if (key.Length == 0)
            return;
        if (candidates.TryGetValue(key, out var existing))
        {
            if (existing != null && existing.EntityId != entity.EntityId)
                candidates[key] = null;
            return;
        }

        candidates[key] = entity;
    }

    private static ViewBuilderEntityDto? FindEntity(IReadOnlyList<ViewBuilderEntityDto> catalog, string table)
    {
        var bare = table[(table.LastIndexOf('.') + 1)..];
        return catalog.FirstOrDefault(item =>
                   string.Equals(item.TableName, table, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.TableName, bare, StringComparison.OrdinalIgnoreCase))
               ?? catalog.FirstOrDefault(item =>
                   string.Equals(item.EntityName, table, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(item.EntityName, bare, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasField(ViewBuilderEntityDto entity, string column) =>
        entity.Fields.Any(field =>
            string.Equals(field.Name, column, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.ColumnName, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recognises "the child's foreign-key name applied to the parent": <c>SupplierId</c> on
    /// <c>Supplier</c> means <c>Id</c>. Nothing else is repaired — a genuine misspelling has no
    /// defensible target.
    /// </summary>
    private static string? ResolvePrimaryKey(ViewBuilderEntityDto entity, string column)
    {
        if (!string.Equals(column, entity.EntityName + "Id", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = entity.Fields.FirstOrDefault(field =>
            string.Equals(field.Name, "Id", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(field.ColumnName, "Id", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(key?.Name) ? null : key!.Name;
    }
}
