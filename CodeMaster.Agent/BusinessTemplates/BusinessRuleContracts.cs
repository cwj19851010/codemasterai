namespace CodeMaster.Agent.BusinessTemplates;

/// <summary>
/// 一条跨实体业务规则。它不是运行时校验器，而是给模型看的领域知识：说明在什么动作下
/// 必须先满足什么条件、以及动作会产生哪些禁止项。模型据此在 EntityFields / Relations /
/// FieldControls 阶段补齐字段与关系，并在 ConsistencyCheck 阶段产出对应的检查项。
/// </summary>
/// <param name="Key">稳定标识，用于日志与测试断言，不随文案变化。</param>
/// <param name="Name">面向用户的中文名。</param>
/// <param name="Summary">一句话说明这条规则约束的是什么。</param>
public sealed record BusinessRule(
    string Key,
    string Name,
    string Summary,
    IReadOnlyList<RuleConstraint> Constraints);

/// <summary>
/// 规则里的一次具体约束：谁、在什么动作下、需要什么前提、产生什么后果。
/// </summary>
/// <param name="TriggerEntity">触发实体，用元数据里的实体名（如 MaterialReceipt）。</param>
/// <param name="TriggerEvent">触发动作（如 创建付款对账单）。</param>
/// <param name="Preconditions">动作开始前必须成立的条件。</param>
/// <param name="SideEffects">动作完成后必须成立或必须禁止的事项。</param>
public sealed record RuleConstraint(
    string TriggerEntity,
    string TriggerEvent,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> SideEffects);

/// <summary>
/// 一个业务领域的规则目录。<see cref="Keywords"/> 用于按用户需求自动挂载，
/// 不要求用户显式点名这个目录。
/// </summary>
/// <param name="Key">目录标识，模型在计划里引用它。</param>
/// <param name="DisplayName">面向用户的中文名。</param>
/// <param name="Keywords">命中关键词即挂载该目录。</param>
public sealed record BusinessRuleTemplate(
    string Key,
    string DisplayName,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<BusinessRule> Rules);
