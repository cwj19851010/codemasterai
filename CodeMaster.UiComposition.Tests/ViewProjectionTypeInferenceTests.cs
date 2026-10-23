using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.UiComposition.Agent;

namespace CodeMaster.UiComposition.Tests;

/// <summary>
/// Pins the type a view output column is given from its SELECT projection.
/// </summary>
/// <remarks>
/// The profit view that motivated this inference declared every non-key column as <c>string</c>,
/// including <c>ContractAmount</c> and <c>NetProfit</c>. The field-control stage then bound a number
/// control to those columns and validation stopped the whole work plan with "Field 'ContractAmount'
/// uses number and must have a numeric data type." The projection is the authority on the type, so
/// these cases read it from there.
/// </remarks>
public sealed class ViewProjectionTypeInferenceTests
{
    private const string ProfitViewSql =
        "SELECT p.Id, p.ProjectName, p.ContractAmount, " +
        "COALESCE(r.TotalReceived, 0) AS TotalReceived, " +
        "COALESCE(mc.MaterialCost, 0) AS MaterialCost, " +
        "COALESCE(lc.LaborCost, 0) AS LaborCost, " +
        "COALESCE(oe.OtherCost, 0) AS OtherCost, " +
        "(p.ContractAmount - COALESCE(mc.MaterialCost, 0) - COALESCE(lc.LaborCost, 0) - COALESCE(oe.OtherCost, 0)) AS NetProfit " +
        "FROM Project p " +
        "LEFT JOIN (SELECT ProjectId, SUM(ReceivedAmount) AS TotalReceived FROM ReceivableRecord GROUP BY ProjectId) r ON p.Id = r.ProjectId " +
        "LEFT JOIN (SELECT ProjectId, SUM(Quantity * ReceiptPrice) AS MaterialCost FROM MaterialReceipt GROUP BY ProjectId) mc ON p.Id = mc.ProjectId " +
        "LEFT JOIN (SELECT ProjectId, SUM(PaidAmount) AS LaborCost FROM LaborContract GROUP BY ProjectId) lc ON p.Id = lc.ProjectId " +
        "LEFT JOIN (SELECT ProjectId, SUM(Amount) AS OtherCost FROM OtherExpense GROUP BY ProjectId) oe ON p.Id = oe.ProjectId";

    [Theory]
    [InlineData("ContractAmount", "decimal")]
    [InlineData("TotalReceived", "decimal")]
    [InlineData("MaterialCost", "decimal")]
    [InlineData("LaborCost", "decimal")]
    [InlineData("OtherCost", "decimal")]
    [InlineData("NetProfit", "decimal")]
    [InlineData("Id", "long")]
    [InlineData("ProjectName", "string")]
    public void ReadsTheTypeOfAMoneyProjection(string outputName, string expected)
        => Assert.Equal(expected, ViewProjectionTypeInference.Infer(ProfitViewSql, Catalog(), outputName));

    [Fact]
    public void ResolvesAPhysicalTableNameWithoutAnAlias()
        => Assert.Equal(
            "decimal",
            ViewProjectionTypeInference.Infer(
                "SELECT Project.ContractAmount FROM Project",
                Catalog(),
                "ContractAmount"));

    [Fact]
    public void ReadsAnAggregateAndACast()
    {
        Assert.Equal(
            "decimal",
            ViewProjectionTypeInference.Infer(
                "SELECT SUM(r.ReceivedAmount) AS Total FROM ReceivableRecord r",
                Catalog(),
                "Total"));
        Assert.Equal(
            "long",
            ViewProjectionTypeInference.Infer(
                "SELECT COUNT(*) AS Total FROM Project",
                Catalog(),
                "Total"));
        Assert.Equal(
            "string",
            ViewProjectionTypeInference.Infer(
                "SELECT CAST(p.Id AS text) AS Code FROM Project p",
                Catalog(),
                "Code"));
    }

    /// <summary>
    /// Concatenation and date arithmetic are not numbers: an operand the catalog proves is text has
    /// to stop the inference rather than let a "+" turn a label into a decimal.
    /// </summary>
    [Fact]
    public void RefusesArithmeticOverText()
        => Assert.Null(
            ViewProjectionTypeInference.Infer(
                "SELECT p.ProjectName + 'x' AS Label FROM Project p",
                Catalog(),
                "Label"));

    /// <summary>An unprovable projection keeps whatever was declared, so nothing is invented.</summary>
    [Fact]
    public void ReturnsNothingWhenTheProjectionIsUnknown()
        => Assert.Null(
            ViewProjectionTypeInference.Infer(
                "SELECT external.Value AS Value FROM Project p",
                Catalog(),
                "Value"));

    private static List<ViewBuilderEntityDto> Catalog() =>
    [
        new()
        {
            EntityId = 1,
            EntityName = "Project",
            TableName = "Project",
            Fields =
            [
                Field(11, "Id", "Id", "long"),
                Field(12, "ProjectName", "ProjectName", "string"),
                Field(13, "ContractAmount", "ContractAmount", "decimal")
            ]
        },
        new()
        {
            EntityId = 2,
            EntityName = "ReceivableRecord",
            TableName = "ReceivableRecord",
            Fields =
            [
                Field(21, "Id", "Id", "long"),
                Field(22, "ReceivedAmount", "ReceivedAmount", "decimal")
            ]
        },
        new()
        {
            EntityId = 3,
            EntityName = "MaterialReceipt",
            TableName = "MaterialReceipt",
            Fields =
            [
                Field(31, "Id", "Id", "long"),
                Field(32, "Quantity", "Quantity", "decimal"),
                Field(33, "ReceiptPrice", "ReceiptPrice", "decimal")
            ]
        },
        new()
        {
            EntityId = 4,
            EntityName = "LaborContract",
            TableName = "LaborContract",
            Fields =
            [
                Field(41, "Id", "Id", "long"),
                Field(42, "PaidAmount", "PaidAmount", "decimal")
            ]
        },
        new()
        {
            EntityId = 5,
            EntityName = "OtherExpense",
            TableName = "OtherExpense",
            Fields =
            [
                Field(51, "Id", "Id", "long"),
                Field(52, "Amount", "Amount", "decimal")
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
