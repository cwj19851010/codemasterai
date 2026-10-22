using System.Text;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// 在模型配置字段控件之前，把"当前真实字段类型"重新读一遍并注入本轮提示词。
/// <para>
/// 为什么需要它：计划可能跨轮次执行，模型上下文里的字段快照可能已经过期——用户中途在实体
/// 设计器改了元数据、上一轮修关系时改过字段类型、或者上下文里新旧值并存。模型不一定会主动
/// 重读，于是按旧类型选控件，写入时被 <c>ValidateFieldControlsAsync</c> 拒绝，白跑一轮。
/// </para>
/// <para>
/// 数据源刻意复用 <see cref="ICodeMasterProjectBlueprintService"/>：它和
/// <c>get_entity_blueprint</c> 工具读的是同一份蓝图，所以注入的内容与模型主动查询的结果
/// 必然一致，不会出现"两套事实"。
/// </para>
/// <para>
/// 这里**不改数据库、不改模型的选择**，只是把事实摆到模型面前。真正的兜底仍然是
/// <c>ValidateFieldControlsAsync</c> 的类型校验——它负责"错了不许写进去"，本类负责
/// "别拿旧数据去试"。
/// </para>
/// </summary>
public static class AiStageFieldTypeSync
{
    /// <summary>
    /// 单次注入的实体上限。这是个经验值，不是实测出来的：18 个实体约 9.7KB，
    /// 只占 200k 上下文的 1.5%，所以它防的不是"装不下"，而是不想让提示词随项目规模线性增长。
    /// 真要按体积控制，应该改成字符预算而不是实体个数。
    /// </summary>
    private const int MaximumEntities = 12;

    /// <summary>单个实体展开的字段上限，防止某个宽表挤掉其他实体。</summary>
    private const int MaximumFieldsPerEntity = 30;

    /// <summary>
    /// 渲染当前字段类型快照。项目里没有任何关系时返回空字符串，调用方直接跳过即可。
    /// </summary>
    /// <remarks>
    /// 只收集**参与关系**的实体，但"参与"是双向的：既包括作为 Source 的实体，也包括
    /// 只作为 Target 出现的子表。子表恰恰是外键所在、控件最容易选错的地方，漏掉它这个快照
    /// 就失去意义。
    /// </remarks>
    public static async Task<string> BuildSnapshotAsync(
        ICodeMasterProjectBlueprintService blueprintService,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blueprintService);
        if (projectId <= 0)
            return string.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        var blueprint = await blueprintService.GetProjectAsync(projectId);
        var allEntities = blueprint.Modules
            .SelectMany(module => module.Entities)
            .ToList();
        if (allEntities.Count == 0)
            return string.Empty;

        var entityById = allEntities
            .GroupBy(entity => entity.Id)
            .ToDictionary(group => group.Key, group => group.First());

        // 关系表里只有 source 侧持有 Relations 集合，所以"参与关系的实体"必须两边都收。
        var relatedEntityIds = new HashSet<long>();
        var relations = new List<AiRelationBlueprintDto>();
        foreach (var entity in allEntities)
        {
            foreach (var relation in entity.Relations)
            {
                relations.Add(relation);
                relatedEntityIds.Add(relation.SourceEntityId);
                relatedEntityIds.Add(relation.TargetEntityId);
            }
        }

        if (relations.Count == 0)
            return string.Empty;

        var foreignKeys = BuildForeignKeyMap(relations, entityById);
        var entities = allEntities
            .Where(entity => relatedEntityIds.Contains(entity.Id))
            .OrderBy(entity => entity.Id)
            .ToList();

        var lines = new List<string>
        {
            "Current field types (re-read from metadata in this turn; treat these as authoritative):"
        };
        var emittedEntities = 0;
        var omittedEntities = new List<string>();

        foreach (var entity in entities)
        {
            if (emittedEntities >= MaximumEntities)
            {
                // 数到上限后不再渲染，但继续收集名字。只写"前 12 个"会让模型无法区分
                // "这个实体没有关系"和"这个实体被截掉了"——两种情况该做的动作正好相反：
                // 前者什么都不用做，后者必须去 get_entity_blueprint 补读。
                if (entity.Fields.Any(field => !field.IsIgnore))
                    omittedEntities.Add(entity.Name);
                continue;
            }

            var line = RenderEntity(entity, foreignKeys);
            if (line.Length == 0)
                continue;
            lines.Add(line);
            emittedEntities++;
        }

        if (emittedEntities == 0)
            return string.Empty;

        if (omittedEntities.Count > 0)
            lines.Add($"(not listed: {string.Join(", ", omittedEntities)} — these entities do exist in this project; read get_entity_blueprint for them before changing their fields)");

        lines.Add("When choosing FormControlType, use the data types above. Do not reuse a field type quoted earlier in this conversation.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 建立 "实体 Id -> (外键字段名 -> 被引用实体名)" 的索引。
    /// </summary>
    /// <remarks>
    /// 外键落在哪一侧取决于关系形态，不能一律读 SourceField：
    /// <list type="bullet">
    /// <item>Owned OneToMany / OneToOne：Source 是父、SourceField 是父主键，外键是 TargetField，
    /// 也就是 Order.Id -> OrderItem.OrderId 里的 OrderId。</item>
    /// <item>Reference ManyToOne：Source 就是持有外键的一方，外键是 SourceField，
    /// 也就是 Order.CustomerId -> Customer.Id 里的 CustomerId。</item>
    /// </list>
    /// 判完方向后再做一次存在性兜底：如果选中的那侧字段在元数据里不存在，就换另一侧试一次。
    /// 这样即使某条关系的 Ownership/Cardinality 写得不符合约定，也不会凭空造出一个外键箭头。
    /// </remarks>
    private static Dictionary<long, Dictionary<string, string>> BuildForeignKeyMap(
        IEnumerable<AiRelationBlueprintDto> relations,
        IReadOnlyDictionary<long, AiEntityBlueprintDto> entityById)
    {
        var map = new Dictionary<long, Dictionary<string, string>>();

        foreach (var relation in relations)
        {
            if (!entityById.TryGetValue(relation.SourceEntityId, out var sourceEntity) ||
                !entityById.TryGetValue(relation.TargetEntityId, out var targetEntity))
            {
                continue;
            }

            var foreignKeyOnTarget = IsOwnedChildRelation(relation);

            var ownerEntityId = foreignKeyOnTarget ? targetEntity.Id : sourceEntity.Id;
            var ownerField = foreignKeyOnTarget ? relation.TargetField : relation.SourceField;
            var referencedName = foreignKeyOnTarget ? sourceEntity.Name : targetEntity.Name;

            if (!HasField(entityById[ownerEntityId], ownerField))
            {
                // 兜底：换另一侧。
                ownerEntityId = foreignKeyOnTarget ? sourceEntity.Id : targetEntity.Id;
                ownerField = foreignKeyOnTarget ? relation.SourceField : relation.TargetField;
                referencedName = foreignKeyOnTarget ? targetEntity.Name : sourceEntity.Name;
            }

            if (string.IsNullOrWhiteSpace(ownerField) ||
                string.IsNullOrWhiteSpace(referencedName) ||
                !HasField(entityById[ownerEntityId], ownerField))
            {
                continue;
            }

            if (!map.TryGetValue(ownerEntityId, out var fields))
            {
                fields = new Dictionary<string, string>(StringComparer.Ordinal);
                map[ownerEntityId] = fields;
            }

            fields.TryAdd(ownerField, referencedName);
        }

        return map;
    }

    /// <summary>
    /// 判断这条关系的外键是否落在 Target 侧（Owned 子表形态）。
    /// </summary>
    private static bool IsOwnedChildRelation(AiRelationBlueprintDto relation)
    {
        var owned = relation.Ownership.Contains("Owned", StringComparison.OrdinalIgnoreCase);
        if (!owned)
            return false;

        // Owned 且是"一"这端的父持有子集合；ManyToOne 即使标了 Owned 也仍是子持有外键。
        return !relation.Cardinality.Contains("ManyToOne", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasField(AiEntityBlueprintDto entity, string? fieldName)
        => !string.IsNullOrWhiteSpace(fieldName) &&
           entity.Fields.Any(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal));

    private static string RenderEntity(
        AiEntityBlueprintDto entity,
        IReadOnlyDictionary<long, Dictionary<string, string>> foreignKeys)
    {
        var targetByField = foreignKeys.TryGetValue(entity.Id, out var map)
            ? map
            : new Dictionary<string, string>(StringComparer.Ordinal);

        // 外键排在最前面：它是控件选错的高发区，截断时优先保留。
        var ordered = entity.Fields
            .Where(field => !field.IsIgnore)
            .OrderByDescending(field => targetByField.ContainsKey(field.Name))
            .ThenBy(field => field.Name, StringComparer.Ordinal)
            .Take(MaximumFieldsPerEntity)
            .Select(field =>
            {
                var builder = new StringBuilder()
                    .Append(field.Name)
                    .Append('=')
                    .Append(string.IsNullOrWhiteSpace(field.DataType) ? "unknown" : field.DataType);
                if (!string.IsNullOrWhiteSpace(field.FormControlType))
                    builder.Append('(').Append(field.FormControlType).Append(')');
                if (targetByField.TryGetValue(field.Name, out var target) && !string.IsNullOrWhiteSpace(target))
                    builder.Append("->").Append(target);
                return builder.ToString();
            })
            .ToList();
        if (ordered.Count == 0)
            return string.Empty;

        return $"- {entity.Name}: {string.Join(", ", ordered)}";
    }
}
