namespace CodeMaster.Agent.BusinessTemplates;

/// <summary>
/// 工程管理领域的跨实体业务规则目录。
/// <para>
/// 实体名与 CodeMaster 元数据里的 <c>ModuleEntity.Name</c> 对齐（Project / MaterialContract /
/// MaterialReceipt / PaymentReconciliation / LaborContract / ReceivableRecord ...），
/// 这样模型能直接把规则映射到它正在创建的实体上。
/// </para>
/// <para>
/// 这里只放**跨实体**约束。单字段的长度、必填、正则属于字段级校验，由 EntityField 承载，
/// 不要写进规则目录，否则提示词会被无关细节淹没。
/// </para>
/// </summary>
public static class EngineeringManagementRules
{
    public const string CatalogKey = "engineering-management";

    public static BusinessRuleTemplate Template { get; } = new(
        CatalogKey,
        "工程管理",
        ["工程管理", "工程项目", "工程施工", "采购", "入库", "付款", "对账", "结算", "工期", "工程地点", "材料合同", "劳务合同"],
        [
            new BusinessRule(
                "contract-cumulative-cap",
                "合同累计上限",
                "采购合同的累计入库数量与金额都不能超过合同本身。",
                [
                    new RuleConstraint(
                        "MaterialReceiptItem",
                        "提交入库明细",
                        ["入库明细必须指向采购合同明细（ContractItemId）", "入库单必须归属同一份采购合同"],
                        [
                            "同一 ContractItemId 下所有入库明细的 Quantity 合计不得超过 MaterialContractItem.Quantity",
                            "同一合同下所有入库明细的 ActualAmount 合计不得超过 MaterialContract.ContractAmount"
                        ])
                ]),
            new BusinessRule(
                "receipt-locked-after-reconciliation",
                "已对账入库单锁定",
                "一旦入库单被付款对账单引用，它就不能再修改或删除。",
                [
                    new RuleConstraint(
                        "MaterialReceipt",
                        "修改或删除入库单",
                        ["该入库单未被任何 PaymentReconciliationItem 引用"],
                        ["已被引用时必须拒绝修改与删除，只能通过冲红单据处理"])
                ]),
            new BusinessRule(
                "reconciliation-single-use",
                "入库单只对账一次",
                "同一张入库单只能出现在一张付款对账单里。",
                [
                    new RuleConstraint(
                        "PaymentReconciliationItem",
                        "创建对账明细",
                        ["该 ReceiptId 在 PaymentReconciliationItem 中不存在未删除记录"],
                        ["同一入库单重复对账必须被拒绝"])
                ]),
            new BusinessRule(
                "reconciliation-amount-cap",
                "对账金额上限",
                "对账金额不能超过对应入库明细的实际金额。",
                [
                    new RuleConstraint(
                        "PaymentReconciliationItem",
                        "创建或修改对账明细",
                        ["必须引用具体的 MaterialReceiptItem"],
                        ["对账金额合计不得超过该入库明细的 ActualAmount"])
                ]),
            new BusinessRule(
                "project-ownership",
                "单据必须归属工程",
                "工程项目是所有业务单据的聚合根，任何单据都不能脱离工程存在。",
                [
                    new RuleConstraint(
                        "Project",
                        "创建任意业务单据",
                        ["Project 实体必须存在，且每张单据都带 ProjectId"],
                        [
                            "MaterialContract / MaterialReceipt / LaborContract / OtherExpense / ReceivableRecord / PaymentReconciliation 均以 ProjectId 为必填外键",
                            "删除工程前必须先处理其下所有单据"
                        ])
                ]),
            new BusinessRule(
                "labor-contract-receivable-cap",
                "劳务应收上限",
                "劳务合同的累计应收金额不能超过合同金额。",
                [
                    new RuleConstraint(
                        "ReceivableRecord",
                        "创建应收记录",
                        ["应收记录必须指向劳务合同（LaborContractId）"],
                        ["同一劳务合同下所有应收记录的 Amount 合计不得超过 LaborContract.ContractAmount"])
                ])
        ]);
}
