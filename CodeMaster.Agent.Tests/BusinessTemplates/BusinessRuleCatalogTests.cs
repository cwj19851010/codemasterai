using CodeMaster.Agent.BusinessTemplates;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Tests.BusinessTemplates;

public sealed class BusinessRuleCatalogTests
{
    [Fact]
    public void Match_attaches_engineering_catalog_for_explicit_domain_request()
    {
        var template = BusinessRuleCatalog.Match("我现在要做一个工程类的管理系统，材料采购入库领用");

        Assert.NotNull(template);
        Assert.Equal(EngineeringManagementRules.CatalogKey, template!.Key);
    }

    [Fact]
    public void Match_attaches_catalog_when_two_weak_keywords_hit()
    {
        // 没有点名"工程管理"，但同时提到付款和对账，足以判定属于该领域。
        var template = BusinessRuleCatalog.Match("帮我做付款对账的流程");

        Assert.NotNull(template);
        Assert.Equal(EngineeringManagementRules.CatalogKey, template!.Key);
    }

    [Fact]
    public void Match_ignores_single_weak_keyword_to_avoid_false_positive()
    {
        // 只提到"采购"，是局部需求，不该把整个工程管理目录塞进提示词。
        var template = BusinessRuleCatalog.Match("给物料目录加一个采购字段");

        Assert.Null(template);
    }

    [Fact]
    public void Match_returns_null_for_unrelated_request()
    {
        Assert.Null(BusinessRuleCatalog.Match("做一个博客系统和评论功能"));
        Assert.Null(BusinessRuleCatalog.Match(null));
        Assert.Null(BusinessRuleCatalog.Match("   "));
    }

    [Fact]
    public void BuildPromptSection_returns_empty_string_when_nothing_matches()
    {
        Assert.Equal(string.Empty, BusinessRuleCatalog.BuildPromptSection("做一个博客系统"));
    }

    [Fact]
    public void RenderForPrompt_lists_every_rule_and_requires_consistency_check_item()
    {
        var template = EngineeringManagementRules.Template;
        var section = BusinessRuleCatalog.RenderForPrompt(template);

        // 每条规则都必须出现，否则模型看不到约束就等于没挂载
        foreach (var rule in template.Rules)
        {
            Assert.Contains(rule.Key, section);
            Assert.Contains(rule.Name, section);
        }

        // 计划里要留下可验收的痕迹：目录引用 + 每条规则一个 ConsistencyCheck 检查项
        Assert.Contains($"businessTemplate={template.Key}", section);
        Assert.Contains("ConsistencyCheck", section);

        // 跨实体约束的关键前提不能丢
        Assert.Contains("ContractItemId", section);
        Assert.Contains("PaymentReconciliationItem", section);
    }

    [Fact]
    public void Platform_constraints_dto_exposes_business_rules()
    {
        // get_codegen_catalog 的 platformConstraints 里必须能读到业务规则目录，
        // 否则模型在规划阶段只能靠猜。
        var dto = new AiPlatformConstraintsDto { BusinessRules = BusinessRuleCatalog.All };

        Assert.NotEmpty(dto.BusinessRules);
        Assert.Contains(dto.BusinessRules, item => item.Key == EngineeringManagementRules.CatalogKey);
        Assert.All(dto.BusinessRules, item => Assert.NotEmpty(item.Rules));
    }

    [Fact]
    public void Platform_constraints_serialize_business_rules_into_the_tool_payload()
    {
        // 模型看到的是 JSON，不是 DTO。字段名写错、被忽略条件吃掉，模型就等于没拿到规则。
        var dto = new AiPlatformConstraintsDto { BusinessRules = BusinessRuleCatalog.All };
        var json = System.Text.Json.JsonSerializer.Serialize(
            dto,
            new System.Text.Json.JsonSerializerOptions(CodeMaster.Agent.Serialization.AgentJsonSerializer.Options));

        Assert.Contains("\"businessRules\"", json, StringComparison.Ordinal);
        Assert.Contains(EngineeringManagementRules.CatalogKey, json, StringComparison.Ordinal);

        // 每条规则的关键字段都要真的出现在报文里。
        foreach (var rule in EngineeringManagementRules.Template.Rules)
        {
            Assert.Contains(rule.Key, json, StringComparison.Ordinal);
            Assert.Contains(rule.Constraints[0].TriggerEntity, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Engineering_catalog_covers_the_documented_workflow_constraints()
    {
        var keys = EngineeringManagementRules.Template.Rules.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);

        // 采购/入库/对账/锁定 这条主链路是文档里点名的场景，不能漏
        Assert.Contains("contract-cumulative-cap", keys);
        Assert.Contains("receipt-locked-after-reconciliation", keys);
        Assert.Contains("reconciliation-single-use", keys);
        Assert.Contains("reconciliation-amount-cap", keys);
        Assert.Contains("project-ownership", keys);
        Assert.Contains("labor-contract-receivable-cap", keys);
    }
}
