using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;

namespace CodeMaster.UiComposition.Tests;

/// <summary>
/// Pins how a view consumer's primary key is decided when the registration is built.
/// </summary>
/// <remarks>
/// A read-only entity may have no primary key: the platform then generates a list/query-only
/// service, no detail route, and an EF mapping with <c>HasNoKey()</c>. A view that concatenates
/// unrelated rows has no column that is unique across the union, so naming one as the key would
/// publish a <c>GetById</c> that answers with the wrong row. Registration therefore has to be able
/// to produce a keyless consumer instead of insisting on one.
/// </remarks>
public sealed class KeylessViewConsumerTests
{
    [Fact]
    public void Declared_fields_stay_keyless_when_the_caller_says_so()
    {
        var fields = ProjectViewBuilderService.BuildDeclaredViewFields(
            [
                Declared("PartyId", "PartyId", "long"),
                Declared("PartyName", "PartyName", "string"),
                Declared("TotalPayable", "TotalPayable", "decimal")
            ],
            requestedKeyColumn: null,
            hasPrimaryKey: false);

        Assert.DoesNotContain(fields, item => item.IsPrimaryKey);
        // Without a key nothing is renamed to Id, so the view keeps the names it projects.
        Assert.Equal(["PartyId", "PartyName", "TotalPayable"], fields.Select(item => item.Name));
    }

    [Fact]
    public void Declared_fields_default_to_the_first_integral_output_when_the_key_is_left_open()
    {
        var fields = ProjectViewBuilderService.BuildDeclaredViewFields(
            [
                Declared("PartyName", "PartyName", "string"),
                Declared("PartyId", "PartyId", "long")
            ],
            requestedKeyColumn: null,
            hasPrimaryKey: null);

        var key = Assert.Single(fields, item => item.IsPrimaryKey);
        Assert.Equal("Id", key.Name);
        Assert.Equal("PartyId", key.ColumnName);
        Assert.Equal("long", key.DataType);
    }

    [Fact]
    public void A_named_key_is_still_honoured()
    {
        var fields = ProjectViewBuilderService.BuildDeclaredViewFields(
            [
                Declared("ProjectId", "ProjectId", "long"),
                Declared("ProjectName", "ProjectName", "string")
            ],
            requestedKeyColumn: "ProjectId",
            hasPrimaryKey: true);

        var key = Assert.Single(fields, item => item.IsPrimaryKey);
        Assert.Equal("Id", key.Name);
        Assert.Equal("ProjectId", key.ColumnName);
    }

    [Fact]
    public void A_caller_that_demands_a_key_still_gets_an_error_when_none_is_integral()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ProjectViewBuilderService.BuildDeclaredViewFields(
                [Declared("PartyName", "PartyName", "string")],
                requestedKeyColumn: null,
                hasPrimaryKey: true));

        Assert.Contains("PartyName (string)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inferred_columns_stay_keyless_when_the_caller_says_so()
    {
        var fields = ProjectViewBuilderService.BuildInferredViewFields(
            [
                Column("party_name", "string", 0),
                Column("total_payable", "decimal", 1)
            ],
            requestedKeyColumn: null,
            viewName: "v_payable_ledger",
            hasPrimaryKey: false);

        Assert.DoesNotContain(fields, item => item.IsPrimaryKey);
        Assert.Equal(["PartyName", "TotalPayable"], fields.Select(item => item.Name));
    }

    [Fact]
    public void Inferred_columns_key_on_the_first_integral_column()
    {
        var fields = ProjectViewBuilderService.BuildInferredViewFields(
            [
                Column("party_name", "string", 0),
                Column("party_id", "long", 1)
            ],
            requestedKeyColumn: null,
            viewName: "v_party",
            hasPrimaryKey: null);

        var key = Assert.Single(fields, item => item.IsPrimaryKey);
        Assert.Equal("Id", key.Name);
        Assert.Equal("party_id", key.ColumnName);
        Assert.Equal(0, key.OrderNum);
    }

    private static ViewEntityFieldDefinitionDto Declared(string name, string columnName, string dataType) => new()
    {
        Name = name,
        DisplayName = name,
        ColumnName = columnName,
        DataType = dataType,
        ShowInList = true,
        ShowInDetail = true
    };

    private static ViewBuilderViewColumnDto Column(string name, string dataType, int ordinal) => new()
    {
        Name = name,
        DataType = dataType,
        Ordinal = ordinal
    };
}
