namespace CodeMaster.Agent.BusinessTemplates;

/// <summary>
/// 业务规则目录的注册表与挂载判定。
/// <para>
/// 挂载策略刻意保守：强关键词（工程管理 / 工程项目 / 工程施工）命中即挂载；
/// 其余关键词需要命中至少 <see cref="WeakKeywordThreshold"/> 个，避免"建一个采购物料目录"
/// 这种局部需求被误判成整个工程管理项目，把无关规则塞进提示词。
/// </para>
/// </summary>
public static class BusinessRuleCatalog
{
    /// <summary>出现即挂载的强关键词。只有明确点名业务领域时才走这条路。</summary>
    private static readonly string[] StrongKeywords = ["工程管理", "工程项目", "工程施工"];

    /// <summary>弱关键词需要同时命中的个数。</summary>
    private const int WeakKeywordThreshold = 2;

    public static IReadOnlyList<BusinessRuleTemplate> All { get; } = [EngineeringManagementRules.Template];

    /// <summary>
    /// 按用户需求文本挑选要挂载的规则目录。无命中返回 <c>null</c>。
    /// </summary>
    public static BusinessRuleTemplate? Match(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var template in All)
        {
            var hits = template.Keywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword) &&
                                  content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (hits.Count == 0)
                continue;
            if (hits.Any(hit => StrongKeywords.Contains(hit, StringComparer.OrdinalIgnoreCase)))
                return template;
            if (hits.Count >= WeakKeywordThreshold)
                return template;
        }

        return null;
    }

    /// <summary>
    /// 把目录渲染成提示词片段。命中的目录会被要求逐条落成 ConsistencyCheck 检查项，
    /// 否则模型很容易只把规则读进上下文、却在计划里不留任何可验收的痕迹。
    /// </summary>
    public static string RenderForPrompt(BusinessRuleTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var lines = new List<string>
        {
            $"Business rule catalog attached: {template.Key} ({template.DisplayName}).",
            "The request matches a known business domain. The rules below are REQUIRED cross-entity constraints, not suggestions.",
            $"- Add every field and relation a rule needs during EntityFields / Relations.",
            $"- Configure the control a rule needs (for example the reconciliation reference) during FieldControls.",
            $"- Add one explicit ConsistencyCheck task item per rule key below. Do not fold them into a generic \"validate metadata\" item.",
            $"- Record the attachment in the plan Summary as: businessTemplate={template.Key}",
            "Rules:"
        };

        for (var index = 0; index < template.Rules.Count; index++)
        {
            var rule = template.Rules[index];
            lines.Add($"{index + 1}. [{rule.Key}] {rule.Name} — {rule.Summary}");
            foreach (var constraint in rule.Constraints)
            {
                lines.Add($"   - {constraint.TriggerEntity} · {constraint.TriggerEvent}");
                if (constraint.Preconditions.Count > 0)
                    lines.Add($"     require: {string.Join("; ", constraint.Preconditions)}");
                if (constraint.SideEffects.Count > 0)
                    lines.Add($"     then: {string.Join("; ", constraint.SideEffects)}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 需求命中目录时返回可追加到提示词的段落；未命中返回空字符串，调用方直接跳过即可。
    /// </summary>
    public static string BuildPromptSection(string? content)
    {
        var template = Match(content);
        return template is null ? string.Empty : RenderForPrompt(template);
    }
}
