using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Agent.Tools;

namespace CodeMaster.UiComposition.Tests;

/// <summary>
/// Pins how a view plan's KeyColumn is resolved against the SQL the model actually wrote.
/// </summary>
/// <remarks>
/// The payable-ledger view that motivated this resolution is a <c>UNION ALL</c> over suppliers and
/// labor companies: it concatenates unrelated sources, so it has no <c>Id</c> to project and the
/// model still declared <c>KeyColumn = "Id"</c>. Rejecting that plan is permanent — the work-plan
/// task replays the same definition on every retry, and the Views stage runs early enough that
/// every relation, control, page and generation task behind it is skipped too. Read-only entities
/// may be keyless, so the projection is asked for a key and the consumer is registered without one
/// when the view genuinely has none.
/// </remarks>
public sealed class ViewPlanKeyColumnResolutionTests
{
    /// <summary>The exact SQL that failed with "KeyColumn must match a declared view entity field ColumnName."</summary>
    private const string PayableLedgerSql =
        "SELECT s.SupplierId AS PartyId, s.SupplierName AS PartyName, 'Supplier' AS PartyType, " +
        "COALESCE((SELECT SUM(mr.Amount) FROM eng_material_receipt mr " +
        "INNER JOIN eng_purchase_contract pc ON mr.PurchaseContractId = pc.Id " +
        "WHERE pc.SupplierId = s.SupplierId AND mr.IsDeleted = 0 AND pc.IsDeleted = 0), 0) AS TotalPayable, " +
        "COALESCE((SELECT SUM(pr.PaidAmount) FROM fin_payment_record pr " +
        "INNER JOIN fin_payment_statement ps ON pr.PaymentStatementId = ps.Id " +
        "INNER JOIN fin_statement_receipt_detail srd ON ps.Id = srd.PaymentStatementId " +
        "INNER JOIN eng_material_receipt mr ON srd.MaterialReceiptId = mr.Id " +
        "INNER JOIN eng_purchase_contract pc ON mr.PurchaseContractId = pc.Id " +
        "WHERE pc.SupplierId = s.SupplierId AND pr.IsDeleted = 0 AND ps.IsDeleted = 0 " +
        "AND pc.IsDeleted = 0 AND mr.IsDeleted = 0), 0) AS TotalPaid, " +
        "(COALESCE((SELECT SUM(mr.Amount) FROM eng_material_receipt mr " +
        "INNER JOIN eng_purchase_contract pc ON mr.PurchaseContractId = pc.Id " +
        "WHERE pc.SupplierId = s.SupplierId AND mr.IsDeleted = 0 AND pc.IsDeleted = 0), 0) - " +
        "COALESCE((SELECT SUM(pr.PaidAmount) FROM fin_payment_record pr " +
        "INNER JOIN fin_payment_statement ps ON pr.PaymentStatementId = ps.Id " +
        "INNER JOIN fin_statement_receipt_detail srd ON ps.Id = srd.PaymentStatementId " +
        "INNER JOIN eng_material_receipt mr ON srd.MaterialReceiptId = mr.Id " +
        "INNER JOIN eng_purchase_contract pc ON mr.PurchaseContractId = pc.Id " +
        "WHERE pc.SupplierId = s.SupplierId AND pr.IsDeleted = 0 AND ps.IsDeleted = 0 " +
        "AND pc.IsDeleted = 0 AND mr.IsDeleted = 0), 0)) AS UnpaidBalance " +
        "FROM eng_supplier s WHERE s.IsDeleted = 0 " +
        "UNION ALL " +
        "SELECT lc.LaborCompanyId AS PartyId, lc.CompanyName AS PartyName, 'Labor' AS PartyType, " +
        "COALESCE((SELECT SUM(lc2.ContractAmount) FROM eng_labor_contract lc2 " +
        "WHERE lc2.LaborCompanyId = lc.LaborCompanyId AND lc2.IsDeleted = 0), 0) AS TotalPayable, " +
        "COALESCE((SELECT SUM(pr.PaidAmount) FROM fin_payment_record pr " +
        "INNER JOIN fin_payment_statement ps ON pr.PaymentStatementId = ps.Id " +
        "INNER JOIN fin_statement_labor_detail sld ON ps.Id = sld.PaymentStatementId " +
        "INNER JOIN eng_labor_contract lc3 ON sld.LaborContractId = lc3.Id " +
        "WHERE lc3.LaborCompanyId = lc.LaborCompanyId AND pr.IsDeleted = 0 AND ps.IsDeleted = 0 " +
        "AND lc3.IsDeleted = 0), 0) AS TotalPaid, " +
        "(COALESCE((SELECT SUM(lc2.ContractAmount) FROM eng_labor_contract lc2 " +
        "WHERE lc2.LaborCompanyId = lc.LaborCompanyId AND lc2.IsDeleted = 0), 0) - " +
        "COALESCE((SELECT SUM(pr.PaidAmount) FROM fin_payment_record pr " +
        "INNER JOIN fin_payment_statement ps ON pr.PaymentStatementId = ps.Id " +
        "INNER JOIN fin_statement_labor_detail sld ON ps.Id = sld.PaymentStatementId " +
        "INNER JOIN eng_labor_contract lc3 ON sld.LaborContractId = lc3.Id " +
        "WHERE lc3.LaborCompanyId = lc.LaborCompanyId AND pr.IsDeleted = 0 AND ps.IsDeleted = 0 " +
        "AND lc3.IsDeleted = 0), 0)) AS UnpaidBalance " +
        "FROM eng_labor_company lc WHERE lc.IsDeleted = 0";

    [Fact]
    public void Registers_a_union_view_without_a_primary_key_when_it_projects_no_Id()
    {
        var repairs = new List<string>();

        var resolved = ViewDefinitionBatchOperationAdapter.ResolveProjection(
            RepairedSql(),
            Catalog(),
            [],
            "Id",
            [],
            repairs);

        // Supplier 1 and labor company 1 both project PartyId = 1, so naming it the key would make
        // GetById answer with the wrong branch row. A keyless read-only consumer is the honest shape.
        Assert.Equal(string.Empty, resolved.KeyColumn);
        // Nothing is promoted to the generated Id, so the projection keeps its own property names.
        Assert.DoesNotContain(resolved.Fields, item => string.Equals(item.Name, "Id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["PartyId", "PartyName", "PartyType", "TotalPayable", "TotalPaid", "UnpaidBalance"],
            resolved.Fields.Select(item => item.ColumnName));
        Assert.Equal("long", resolved.Fields.Single(item => item.ColumnName == "PartyId").DataType);
        Assert.Contains(repairs, item => item.Contains("without a primary key", StringComparison.Ordinal));
    }

    /// <summary>
    /// The union's money columns are computed expressions, so nothing but the branch key is integral.
    /// </summary>
    [Fact]
    public void Reads_the_money_columns_of_a_union_view_as_decimals()
    {
        var resolved = ViewDefinitionBatchOperationAdapter.ResolveProjection(
            RepairedSql(),
            Catalog(),
            [],
            "Id",
            [],
            new List<string>());

        foreach (var name in new[] { "TotalPayable", "TotalPaid", "UnpaidBalance" })
            Assert.Equal("decimal", resolved.Fields.Single(item => item.ColumnName == name).DataType);
        Assert.Equal("string", resolved.Fields.Single(item => item.ColumnName == "PartyName").DataType);
    }

    [Fact]
    public void Follows_a_projection_that_renames_the_key()
    {
        var repairs = new List<string>();

        var resolved = ViewDefinitionBatchOperationAdapter.ResolveProjection(
            "SELECT p.Id AS ProjectId, p.ProjectName FROM eng_project p",
            Catalog(),
            [],
            "Id",
            [],
            repairs);

        Assert.Equal("ProjectId", resolved.KeyColumn);
        Assert.Equal("long", resolved.Fields.Single(item => item.ColumnName == "ProjectId").DataType);
        Assert.Contains(repairs, item => item.Contains("ProjectId", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_an_explicitly_declared_key()
    {
        var repairs = new List<string>();
        var declared = new List<ViewEntityFieldDefinitionDto>
        {
            new()
            {
                Name = "Id", DisplayName = "Id", ColumnName = "SupplierId", DataType = "long",
                ShowInList = true, ShowInDetail = true, OrderNum = 1
            },
            new()
            {
                Name = "SupplierName", DisplayName = "供应商", ColumnName = "SupplierName", DataType = "string",
                ShowInList = true, ShowInDetail = true, OrderNum = 2
            }
        };

        var resolved = ViewDefinitionBatchOperationAdapter.ResolveProjection(
            "SELECT s.SupplierId, s.SupplierName FROM eng_supplier s",
            Catalog(),
            declared,
            "SupplierId",
            [],
            repairs);

        Assert.Equal("SupplierId", resolved.KeyColumn);
        Assert.Equal(2, resolved.Fields.Count);
        Assert.Empty(repairs);
    }

    /// <summary>
    /// With nothing integral to key on the plan still runs: the consumer is registered keyless
    /// rather than rejected, because a read-only entity is allowed to have no primary key.
    /// </summary>
    [Fact]
    public void Registers_a_text_only_projection_without_a_key()
    {
        var repairs = new List<string>();

        var resolved = ViewDefinitionBatchOperationAdapter.ResolveProjection(
            "SELECT s.SupplierName AS PartyName FROM eng_supplier s",
            Catalog(),
            [],
            "Id",
            [],
            repairs);

        Assert.Equal(string.Empty, resolved.KeyColumn);
        Assert.Equal("PartyName", resolved.Fields.Single().ColumnName);
        Assert.Equal("string", resolved.Fields.Single().DataType);
        Assert.Contains(repairs, item => item.Contains("without a primary key", StringComparison.Ordinal));
    }

    /// <summary>
    /// A text key cannot back the generated long Id either. The model cannot correct a replayed task
    /// definition, so this is repaired into a keyless consumer instead of ending the whole plan.
    /// </summary>
    [Fact]
    public void Registers_a_consumer_without_a_key_when_the_named_key_is_text()
    {
        var repairs = new List<string>();
        var declared = new List<ViewEntityFieldDefinitionDto>
        {
            new()
            {
                Name = "PartyName", DisplayName = "往来单位", ColumnName = "PartyName", DataType = "string",
                ShowInList = true, ShowInDetail = true, OrderNum = 1
            }
        };

        var resolved = ViewDefinitionBatchOperationAdapter.ResolveProjection(
            "SELECT s.SupplierName AS PartyName FROM eng_supplier s",
            Catalog(),
            declared,
            "PartyName",
            [],
            repairs);

        Assert.Equal(string.Empty, resolved.KeyColumn);
        Assert.Equal("string", resolved.Fields.Single().DataType);
        Assert.Contains(repairs, item => item.Contains("must be integral", StringComparison.Ordinal));
    }

    /// <summary>
    /// The SQL as the adapter hands it over: the column repair has already replaced the master keys
    /// the model named after the child foreign keys, so the key output is provably integral.
    /// </summary>
    private static string RepairedSql() => ViewSqlColumnRepair.Repair(PayableLedgerSql, Catalog()).Sql;

    private static List<ViewBuilderEntityDto> Catalog() =>
    [
        new()
        {
            EntityId = 1,
            EntityName = "Supplier",
            TableName = "eng_supplier",
            Fields =
            [
                Field(11, "Id", "Id", "long"),
                Field(13, "SupplierName", "SupplierName", "string")
            ]
        },
        new()
        {
            EntityId = 2,
            EntityName = "LaborCompany",
            TableName = "eng_labor_company",
            Fields =
            [
                Field(21, "Id", "Id", "long"),
                Field(23, "CompanyName", "CompanyName", "string")
            ]
        },
        new()
        {
            EntityId = 3,
            EntityName = "MaterialReceipt",
            TableName = "eng_material_receipt",
            Fields =
            [
                Field(31, "Id", "Id", "long"),
                Field(32, "Amount", "Amount", "decimal"),
                Field(33, "PurchaseContractId", "PurchaseContractId", "long")
            ]
        },
        new()
        {
            EntityId = 4,
            EntityName = "PurchaseContract",
            TableName = "eng_purchase_contract",
            Fields =
            [
                Field(41, "Id", "Id", "long"),
                Field(42, "SupplierId", "SupplierId", "long")
            ]
        },
        new()
        {
            EntityId = 5,
            EntityName = "PaymentRecord",
            TableName = "fin_payment_record",
            Fields =
            [
                Field(51, "Id", "Id", "long"),
                Field(52, "PaidAmount", "PaidAmount", "decimal"),
                Field(53, "PaymentStatementId", "PaymentStatementId", "long")
            ]
        },
        new()
        {
            EntityId = 6,
            EntityName = "LaborContract",
            TableName = "eng_labor_contract",
            Fields =
            [
                Field(61, "Id", "Id", "long"),
                Field(62, "ContractAmount", "ContractAmount", "decimal"),
                Field(63, "LaborCompanyId", "LaborCompanyId", "long")
            ]
        },
        new()
        {
            EntityId = 7,
            EntityName = "Project",
            TableName = "eng_project",
            Fields =
            [
                Field(71, "Id", "Id", "long"),
                Field(72, "ProjectName", "ProjectName", "string")
            ]
        }
    ];

    private static ViewBuilderFieldDto Field(long id, string name, string columnName, string dataType) => new()
    {
        FieldId = id,
        Name = name,
        ColumnName = columnName,
        DataType = dataType
    };
}
