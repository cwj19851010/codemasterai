using System.Text;
using System.Text.RegularExpressions;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 视图 SQL 命名解析。
///
/// <para>视图 SQL 在设计期按"实体名 + 属性名"书写（<c>SELECT p.ProjectName FROM Project p</c>），
/// 而生成项目的物理表/列名经过 snake_case 与模块前缀转换
/// （<c>engineering_management__project.project_name</c>）。直接把设计期 SQL 交给数据库执行，
/// SQLite 的 <c>CREATE VIEW</c> 是延迟解析的——视图能建成功，直到真正查询才报
/// <c>no such table / no such column</c>。</para>
///
/// <para>转换分三步，都是幂等的，可安全地在预览与执行两处重复调用：</para>
/// <list type="number">
/// <item>表名：<c>FROM/JOIN &lt;实体名&gt;</c> 换成物理表名；没有别名的补 <c>AS &lt;实体名&gt;</c>，
/// 让原有的 <c>Project.xxx</c> 引用继续有效。</item>
/// <item>带别名前缀的列：<c>p.ProjectName</c> → <c>p.project_name</c>。</item>
/// <item>单表作用域内的裸列：<c>SELECT Id FROM MaterialReceipt</c> 里的 <c>Id</c> 也能解析。
/// 只在"该层只有一张受管表"且"标识符确实命中该表列名"时替换，且跳过 <c>AS</c> 后面的
/// 输出别名——避免把视图的对外列名改掉。</item>
/// </list>
/// </summary>
internal static class ViewSqlTableResolver
{
    private static readonly Regex FromJoinRegex = new(
        @"\b(FROM|JOIN)\s+([A-Za-z_][A-Za-z0-9_]*)(?:\s+(?:AS\s+)?([A-Za-z_][A-Za-z0-9_]*))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareIdentifierRegex = new(
        @"(?<![\w.'""`\[])([A-Za-z_][A-Za-z0-9_]*)(?![\w.])",
        RegexOptions.Compiled);

    private static readonly Regex SubqueryAliasRegex = new(
        @"\)\s+(?:AS\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HasTrailingAliasRegex = new(
        @"\bAS\s+[A-Za-z_][A-Za-z0-9_]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QualifiedColumnRegex = new(
        @"^([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "FETCH", "UNION", "EXCEPT",
        "INTERSECT", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "NATURAL", "JOIN", "AS",
        "SET", "VALUES", "SELECT", "FROM", "AND", "OR", "NOT", "IN", "IS", "NULL", "EXISTS",
        "CASE", "WHEN", "THEN", "ELSE", "END", "WITH", "USING", "WINDOW", "DISTINCT", "ALL",
        "BETWEEN", "LIKE", "ASC", "DESC", "BY", "INTO"
    };

    private sealed record Mapping(
        Dictionary<string, string> TableByEntity,
        Dictionary<string, string> EntityByTable,
        Dictionary<string, Dictionary<string, string>> ColumnsByEntity);

    /// <summary>实体名 -> 物理表名。</summary>
    public static Dictionary<string, string> BuildTableMap(IEnumerable<ModuleEntity> entities)
        => BuildMapping(entities, Array.Empty<EntityField>()).TableByEntity;

    /// <summary>仅做表名替换（不涉及列名），供只需要表名的调用方使用。</summary>
    public static string Resolve(string sql, IReadOnlyDictionary<string, string> tableByEntityName)
    {
        if (string.IsNullOrWhiteSpace(sql) || tableByEntityName.Count == 0)
            return sql;
        return FromJoinRegex.Replace(sql, match =>
        {
            var keyword = match.Groups[1].Value;
            var table = match.Groups[2].Value;
            if (!tableByEntityName.TryGetValue(table, out var physical))
                return match.Value;
            var alias = ReadAlias(match);
            return alias is null
                ? $"{keyword} {physical} AS {table}{ReadTrailingKeyword(match)}"
                : $"{keyword} {physical} {alias}";
        });
    }

    /// <summary>表名 + 列名全量解析。</summary>
    public static string Resolve(
        string sql,
        IEnumerable<ModuleEntity> entities,
        IEnumerable<EntityField> fields)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return sql;
        var mapping = BuildMapping(entities, fields);
        if (mapping.TableByEntity.Count == 0)
            return sql;
        return Rewrite(sql, mapping);
    }

    private static string Rewrite(string sql, Mapping mapping)
    {
        var aliasColumns = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var rewritten = ReplaceTables(sql, mapping, aliasColumns);
        RegisterSubqueryAliases(rewritten, mapping, aliasColumns);
        rewritten = ReplaceQualifiedColumns(rewritten, aliasColumns);
        rewritten = EnsureProjectionAliases(rewritten, aliasColumns);
        return ReplaceBareColumns(rewritten, mapping);
    }

    /// <summary>
    /// 视图的对外列名是契约：<c>SELECT p.ProjectName</c> 转换后列名会变成
    /// <c>project_name</c>，下游视图实体仍按 <c>ProjectName</c> 读，就会报 no such column。
    /// 给最外层投影里"没有显式 AS 的纯列引用"补回属性名，保持对外列名不变。
    /// </summary>
    private static string EnsureProjectionAliases(
        string sql,
        Dictionary<string, Dictionary<string, string>> aliasColumns)
    {
        var selectIndex = FindTopLevelKeyword(sql, "SELECT", 0);
        if (selectIndex < 0)
            return sql;
        var fromIndex = FindTopLevelKeyword(sql, "FROM", selectIndex + 6);
        if (fromIndex < 0)
            return sql;

        var projection = sql[(selectIndex + 6)..fromIndex];
        var items = SplitTopLevel(projection)
            .Select(item => AddOutputAlias(item.Trim(), aliasColumns))
            .ToList();
        return sql[..(selectIndex + 6)] + " " + string.Join(", ", items) + " " + sql[fromIndex..];
    }

    private static string AddOutputAlias(
        string item,
        Dictionary<string, Dictionary<string, string>> aliasColumns)
    {
        if (item.Length == 0 || HasTrailingAliasRegex.IsMatch(item))
            return item;
        var match = QualifiedColumnRegex.Match(item);
        if (!match.Success)
            return item;
        if (!aliasColumns.TryGetValue(match.Groups[1].Value, out var columns))
            return item;
        var column = match.Groups[2].Value;
        var property = columns
            .FirstOrDefault(pair => string.Equals(pair.Value, column, StringComparison.OrdinalIgnoreCase))
            .Key;
        return property is null ? item : $"{item} AS {property}";
    }

    private static int FindTopLevelKeyword(string sql, string keyword, int start)
    {
        var depth = 0;
        for (var index = start; index < sql.Length; index++)
        {
            var current = sql[index];
            if (current == '(') { depth++; continue; }
            if (current == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth != 0) continue;
            if (index + keyword.Length > sql.Length) break;
            if (string.Compare(sql, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            var beforeOk = index == 0 || !char.IsLetterOrDigit(sql[index - 1]) && sql[index - 1] != '_';
            var afterIndex = index + keyword.Length;
            var afterOk = afterIndex >= sql.Length || !char.IsLetterOrDigit(sql[afterIndex]) && sql[afterIndex] != '_';
            if (beforeOk && afterOk)
                return index;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(': depth++; break;
                case ')': depth = Math.Max(0, depth - 1); break;
                case ',' when depth == 0:
                    parts.Add(text[start..index]);
                    start = index + 1;
                    break;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    private static string ReplaceTables(
        string sql,
        Mapping mapping,
        Dictionary<string, Dictionary<string, string>> aliasColumns)
        => FromJoinRegex.Replace(sql, match =>
        {
            var keyword = match.Groups[1].Value;
            var table = match.Groups[2].Value;
            if (!mapping.TableByEntity.TryGetValue(table, out var physical))
                return match.Value;

            var alias = ReadAlias(match);
            var effectiveAlias = alias ?? table;
            if (mapping.ColumnsByEntity.TryGetValue(table, out var columns))
                aliasColumns[effectiveAlias] = columns;
            // 没有别名时补上实体名，让原有的 <Entity>.<Property> 引用继续可解析。
            // 正则的别名组会把 WHERE / GROUP 这类关键字也吞进去，替换时要原样吐回，
            // 否则 "FROM Project WHERE ..." 会变成 "FROM <table> AS Project" —— 条件整段丢失。
            return alias is null
                ? $"{keyword} {physical} AS {table}{ReadTrailingKeyword(match)}"
                : $"{keyword} {physical} {alias}";
        });

    /// <summary>
    /// <c>(SELECT ... ) mc</c> 这种派生表：外层用 <c>mc.ProjectId</c> 引用的是子查询的
    /// 输出列名。子查询里的 <c>mc2.ProjectId</c> 已经被换成 <c>mc2.project_id</c>，输出列
    /// 也就叫 <c>project_id</c>，所以这里把派生表别名登记成"子查询内实体列名的并集"，
    /// 让外层的 <c>mc.ProjectId</c> 一起被转换。
    /// </summary>
    private static void RegisterSubqueryAliases(
        string sql,
        Mapping mapping,
        Dictionary<string, Dictionary<string, string>> aliasColumns)
    {
        foreach (Match match in SubqueryAliasRegex.Matches(sql))
        {
            var alias = match.Groups[1].Value;
            if (SqlKeywords.Contains(alias))
                continue;
            var close = match.Index;
            var open = FindMatchingOpen(sql, close);
            if (open < 0)
                continue;
            var inner = sql[(open + 1)..close];
            if (!inner.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                continue;

            var entities = ExtractTopLevelEntities(inner, mapping);
            if (entities.Count == 0)
                continue;

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (!mapping.ColumnsByEntity.TryGetValue(entity, out var columns))
                    continue;
                foreach (var (property, column) in columns)
                    merged[property] = column;
            }
            if (merged.Count > 0)
                aliasColumns[alias] = merged;
        }
    }

    private static int FindMatchingOpen(string sql, int closeIndex)
    {
        var depth = 0;
        for (var index = closeIndex; index >= 0; index--)
        {
            if (sql[index] == ')') depth++;
            else if (sql[index] == '(')
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }
        return -1;
    }

    private static string ReplaceQualifiedColumns(
        string sql,
        Dictionary<string, Dictionary<string, string>> aliasColumns)
    {
        foreach (var (alias, columns) in aliasColumns)
        {
            if (columns.Count == 0)
                continue;
            var pattern = new Regex(
                $@"\b{Regex.Escape(alias)}\.([A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.Compiled);
            sql = pattern.Replace(sql, match =>
                columns.TryGetValue(match.Groups[1].Value, out var column)
                    ? $"{alias}.{column}"
                    : match.Value);
        }
        return sql;
    }

    /// <summary>
    /// 自内向外逐层处理：每一层都能独立判断"本层只有一张受管表"。
    /// 递归而不是循环——处理完最内层括号后括号本身还在，循环会一直命中同一个位置。
    /// </summary>
    private static string ReplaceBareColumns(string sql, Mapping mapping)
    {
        var builder = new StringBuilder(sql.Length + 16);
        var segmentStart = 0;
        for (var index = 0; index < sql.Length; index++)
        {
            if (sql[index] != '(')
                continue;
            var close = FindMatchingClose(sql, index);
            if (close < 0)
                break;
            builder.Append(sql, segmentStart, index - segmentStart);
            builder.Append('(');
            builder.Append(ReplaceBareColumns(sql[(index + 1)..close], mapping));
            builder.Append(')');
            index = close;
            segmentStart = close + 1;
        }
        builder.Append(sql, segmentStart, sql.Length - segmentStart);
        return RewriteSingleScope(builder.ToString(), mapping);
    }

    private static int FindMatchingClose(string sql, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < sql.Length; index++)
        {
            if (sql[index] == '(') depth++;
            else if (sql[index] == ')')
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }
        return -1;
    }

    private static string RewriteSingleScope(string scope, Mapping mapping)
    {
        var entities = ExtractTopLevelEntities(scope, mapping);
        if (entities.Count != 1)
            return scope;
        if (!mapping.ColumnsByEntity.TryGetValue(entities[0], out var columns) || columns.Count == 0)
            return scope;

        return BareIdentifierRegex.Replace(scope, match =>
        {
            var identifier = match.Groups[1].Value;
            if (SqlKeywords.Contains(identifier))
                return match.Value;
            if (!columns.TryGetValue(identifier, out var column))
                return match.Value;
            // AS 后面是输出列别名，改了会动到视图的对外列名。
            return FollowsAsKeyword(scope, match.Index) ? match.Value : column;
        });
    }

    private static bool FollowsAsKeyword(string text, int index)
    {
        var prefix = text.AsSpan(0, index).TrimEnd();
        if (prefix.Length < 2)
            return false;
        if (!prefix[^1].Equals('s') && !prefix[^1].Equals('S'))
            return false;
        if (!prefix[^2].Equals('a') && !prefix[^2].Equals('A'))
            return false;
        return prefix.Length == 2 || !char.IsLetterOrDigit(prefix[^3]) && prefix[^3] != '_';
    }

    /// <summary>
    /// 只扫描当前层（跳过括号内已处理的内容）的 FROM / JOIN，反查实体名。
    /// </summary>
    private static List<string> ExtractTopLevelEntities(string scope, Mapping mapping)
    {
        var result = new List<string>();
        var depth = 0;
        var segmentStart = 0;
        void Scan(int end)
        {
            if (end <= segmentStart)
                return;
            var segment = scope[segmentStart..end];
            foreach (Match match in FromJoinRegex.Matches(segment))
            {
                var table = match.Groups[2].Value;
                var entity = mapping.EntityByTable.TryGetValue(table, out var fromTable)
                    ? fromTable
                    : mapping.TableByEntity.ContainsKey(table) ? table : null;
                if (entity is not null && !result.Contains(entity, StringComparer.OrdinalIgnoreCase))
                    result.Add(entity);
            }
        }

        for (var index = 0; index < scope.Length; index++)
        {
            switch (scope[index])
            {
                case '(':
                    Scan(index);
                    depth++;
                    segmentStart = index + 1;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    segmentStart = index + 1;
                    break;
            }
        }
        Scan(scope.Length);
        return result;
    }

    private static string? ReadAlias(Match match)
    {
        if (!match.Groups[3].Success)
            return null;
        var alias = match.Groups[3].Value;
        return SqlKeywords.Contains(alias) ? null : alias;
    }

    /// <summary>
    /// 别名组误吞的关键字（WHERE / GROUP / ORDER ...）要原样接回去。
    /// </summary>
    private static string ReadTrailingKeyword(Match match)
        => match.Groups[3].Success && SqlKeywords.Contains(match.Groups[3].Value)
            ? $" {match.Groups[3].Value}"
            : string.Empty;

    private static Mapping BuildMapping(IEnumerable<ModuleEntity> entities, IEnumerable<EntityField> fields)
    {
        var tableByEntity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entityByTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var columnsByEntity = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var entityNameById = new Dictionary<long, string>();

        foreach (var entity in entities)
        {
            if (entity.IsDeleted || string.IsNullOrWhiteSpace(entity.Name))
                continue;
            var physical = CodeGeneratorService.ResolveTableName(entity.Name, entity.TableName, entity.IsReadOnly);
            tableByEntity[entity.Name] = physical;
            entityByTable[physical] = entity.Name;
            entityNameById[entity.Id] = entity.Name;
        }

        foreach (var field in fields)
        {
            if (field.IsDeleted || field.IsIgnore || string.IsNullOrWhiteSpace(field.Name))
                continue;
            if (!entityNameById.TryGetValue(field.ModuleEntityId, out var entityName))
                continue;
            if (!columnsByEntity.TryGetValue(entityName, out var columns))
            {
                columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                columnsByEntity[entityName] = columns;
            }
            columns[field.Name] = string.IsNullOrWhiteSpace(field.ColumnName)
                ? SqlSugarSetup.ToSnakeCase(field.Name)
                : field.ColumnName.Trim();
        }

        return new Mapping(tableByEntity, entityByTable, columnsByEntity);
    }
}
