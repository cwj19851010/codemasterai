using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Agent;

namespace CodeMaster.UiComposition.Tests;

/// <summary>
/// Pins the repair of a master table's primary key named after the child's foreign key.
/// </summary>
/// <remarks>
/// The payable-ledger view joins <c>eng_purchase_contract.SupplierId</c> and
/// <c>eng_labor_contract.LaborCompanyId</c>, which do exist, and then writes <c>s.SupplierId</c> /
/// <c>lc.LaborCompanyId</c> against the master tables, which do not. Refusing the plan blocks the
/// Views stage forever — the task replays the same definition on every retry — so the reference is
/// rewritten to the key the catalog proves and the rewrite is reported.
/// </remarks>
public sealed class ViewSqlColumnRepairTests
{
    private const string PayableLedgerSql =
        "SELECT s.SupplierId AS PartyId, s.SupplierName AS PartyName, 'Supplier' AS PartyType, " +
        "COALESCE((SELECT SUM(mr.Amount) FROM eng_material_receipt mr " +
        "INNER JOIN eng_purchase_contract pc ON mr.PurchaseContractId = pc.Id " +
        "WHERE pc.SupplierId = s.SupplierId AND mr.IsDeleted = 0 AND pc.IsDeleted = 0), 0) AS TotalPayable " +
        "FROM eng_supplier s WHERE s.IsDeleted = 0 " +
        "UNION ALL " +
        "SELECT lc.LaborCompanyId AS PartyId, lc.CompanyName AS PartyName, 'Labor' AS PartyType, " +
        "COALESCE((SELECT SUM(lc2.ContractAmount) FROM eng_labor_contract lc2 " +
        "WHERE lc2.LaborCompanyId = lc.LaborCompanyId AND lc2.IsDeleted = 0), 0) AS TotalPayable " +
        "FROM eng_labor_company lc WHERE lc.IsDeleted = 0";

    [Fact]
    public void Rewrites_a_master_key_named_after_the_child_foreign_key()
    {
        var outcome = ViewSqlColumnRepair.Repair(PayableLedgerSql, Catalog());

        // The two references onto the master tables become their primary key.
        Assert.Contains("SELECT s.Id AS PartyId", outcome.Sql, StringComparison.Ordinal);
        Assert.Contains("SELECT lc.Id AS PartyId", outcome.Sql, StringComparison.Ordinal);
        Assert.Contains("pc.SupplierId = s.Id", outcome.Sql, StringComparison.Ordinal);
        Assert.Contains("lc2.LaborCompanyId = lc.Id", outcome.Sql, StringComparison.Ordinal);

        // The foreign keys that really exist on the child tables stay untouched.
        Assert.Contains("mr.PurchaseContractId = pc.Id", outcome.Sql, StringComparison.Ordinal);
        Assert.Contains("pc.SupplierId", outcome.Sql, StringComparison.Ordinal);
        Assert.Contains("lc2.LaborCompanyId", outcome.Sql, StringComparison.Ordinal);

        // Four references are rewritten: each master key is selected in the projection and compared
        // against in the correlated subquery's WHERE.
        Assert.Equal(4, outcome.Repairs.Count);
        Assert.All(outcome.Repairs, item => Assert.Contains("primary key", item, StringComparison.Ordinal));
    }

    /// <summary>A column the catalog proves is left exactly as written.</summary>
    [Fact]
    public void Leaves_a_resolvable_column_alone()
    {
        const string sql = "SELECT s.SupplierName, s.Address FROM eng_supplier s";

        var outcome = ViewSqlColumnRepair.Repair(sql, Catalog());

        Assert.Equal(sql, outcome.Sql);
        Assert.Empty(outcome.Repairs);
    }

    /// <summary>An unmanaged table has no catalog entry, so nothing about it can be proven.</summary>
    [Fact]
    public void Leaves_an_external_table_alone()
    {
        const string sql = "SELECT x.SupplierId FROM legacy_supplier x";

        var outcome = ViewSqlColumnRepair.Repair(sql, Catalog());

        Assert.Equal(sql, outcome.Sql);
        Assert.Empty(outcome.Repairs);
    }

    /// <summary>A misspelling has no defensible target, so it is reported by nothing and changed by nothing.</summary>
    [Fact]
    public void Does_not_guess_at_a_misspelled_column()
    {
        const string sql = "SELECT s.SupplierNmae FROM eng_supplier s";

        var outcome = ViewSqlColumnRepair.Repair(sql, Catalog());

        Assert.Equal(sql, outcome.Sql);
        Assert.Empty(outcome.Repairs);
    }

    /// <summary>The same table written by entity name instead of physical name repairs identically.</summary>
    [Fact]
    public void Repairs_through_the_entity_name_form()
    {
        var outcome = ViewSqlColumnRepair.Repair(
            "SELECT Supplier.SupplierId FROM Supplier",
            Catalog());

        Assert.Contains("Supplier.Id", outcome.Sql, StringComparison.Ordinal);
        Assert.Single(outcome.Repairs);
    }

    private static List<ViewBuilderEntityDto> Catalog() =>
    [
        new()
        {
            EntityId = 1,
            EntityName = "Supplier",
            TableName = "eng_supplier",
            Fields =
            [
                Field(1, "Id", "Id", "long"),
                Field(1, "SupplierName", "", "string"),
                Field(1, "Address", "", "string"),
                Field(1, "IsDeleted", "", "bool")
            ]
        },
        new()
        {
            EntityId = 2,
            EntityName = "LaborCompany",
            TableName = "eng_labor_company",
            Fields =
            [
                Field(2, "Id", "Id", "long"),
                Field(2, "CompanyName", "", "string"),
                Field(2, "IsDeleted", "", "bool")
            ]
        },
        new()
        {
            EntityId = 3,
            EntityName = "MaterialReceipt",
            TableName = "eng_material_receipt",
            Fields =
            [
                Field(3, "Id", "Id", "long"),
                Field(3, "Amount", "", "decimal"),
                Field(3, "PurchaseContractId", "", "long"),
                Field(3, "IsDeleted", "", "bool")
            ]
        },
        new()
        {
            EntityId = 4,
            EntityName = "PurchaseContract",
            TableName = "eng_purchase_contract",
            Fields =
            [
                Field(4, "Id", "Id", "long"),
                Field(4, "SupplierId", "", "long"),
                Field(4, "IsDeleted", "", "bool")
            ]
        },
        new()
        {
            EntityId = 5,
            EntityName = "LaborContract",
            TableName = "eng_labor_contract",
            Fields =
            [
                Field(5, "Id", "Id", "long"),
                Field(5, "ContractAmount", "", "decimal"),
                Field(5, "LaborCompanyId", "", "long"),
                Field(5, "IsDeleted", "", "bool")
            ]
        }
    ];

    private static ViewBuilderFieldDto Field(long entityId, string name, string columnName, string dataType) => new()
    {
        EntityId = entityId,
        FieldId = entityId * 100 + name.Length,
        Name = name,
        ColumnName = columnName,
        DataType = dataType
    };
}

/// <summary>
/// Pins that a FROM/JOIN written with the physical table name is resolved like an entity name.
/// </summary>
/// <remarks>
/// The model copies table names straight out of the entity catalog, so half the hand-written view
/// SQL says <c>FROM eng_supplier s</c>. Only entity names used to be registered, the FROM never
/// matched, the alias was never registered, and every <c>s.SupplierName</c> kept its design-time
/// name — which PostgreSQL folds to lowercase and then cannot find.
/// </remarks>
public sealed class ViewSqlTableResolverPhysicalNameTests
{
    [Fact]
    public void Resolves_columns_when_the_sql_uses_the_physical_table_name()
    {
        var resolved = ViewSqlTableResolver.Resolve(
            "SELECT s.SupplierName FROM eng_supplier s WHERE s.IsDeleted = 0",
            Entities(),
            Fields());

        Assert.Contains("s.supplier_name", resolved, StringComparison.Ordinal);
        Assert.Contains("s.is_deleted", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_a_physical_table_name_twice_changes_nothing()
    {
        var once = ViewSqlTableResolver.Resolve("SELECT s.SupplierName FROM eng_supplier s", Entities(), Fields());

        Assert.Equal(once, ViewSqlTableResolver.Resolve(once, Entities(), Fields()));
    }

    private static List<ModuleEntity> Entities() =>
    [
        new() { Id = 1, Name = "Supplier", TableName = "eng_supplier" }
    ];

    private static List<EntityField> Fields() =>
    [
        new() { ModuleEntityId = 1, Name = "Id", ColumnName = string.Empty },
        new() { ModuleEntityId = 1, Name = "SupplierName", ColumnName = string.Empty },
        new() { ModuleEntityId = 1, Name = "IsDeleted", ColumnName = string.Empty }
    ];
}
