using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// Entities that declare a primary key while flagging none of their fields used to be rejected
/// outright with "contains an Id field that must be marked as the primary key" — even though the
/// change-set field map already synthesises that implicit <c>Id</c> key for the same shape. The
/// repair makes it explicit so the stored proposal is self-consistent.
/// </summary>
public sealed class ProjectChangeSetPrimaryKeyRepairTests
{
    private static CreateEntityFieldProposal Field(string name, bool isPrimaryKey = false)
        => new() { Name = name, DataType = "string", IsPrimaryKey = isPrimaryKey };

    private static CreateEntityProposal Entity(
        string name,
        bool hasPrimaryKey,
        params CreateEntityFieldProposal[] fields)
        => new()
        {
            Name = name,
            Description = name,
            HasPrimaryKey = hasPrimaryKey,
            Fields = fields.ToList()
        };

    private static ProjectChangeSetProposal Proposal(params CreateEntityProposal[] entities)
        => new() { Summary = "test", Entities = entities.ToList() };

    [Fact]
    public void Marks_the_id_field_when_an_entity_claims_a_key_but_flags_none()
    {
        var id = Field("Id");
        var proposal = Proposal(Entity("EngineeringProjectProfitView", hasPrimaryKey: true, id, Field("Amount")));

        var repairs = CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal);

        Assert.True(id.IsPrimaryKey);
        Assert.Single(repairs);
        Assert.Contains("EngineeringProjectProfitView", repairs[0], StringComparison.Ordinal);
        Assert.Contains("Id", repairs[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_an_explicitly_flagged_key_alone()
    {
        var explicitKey = Field("Code", isPrimaryKey: true);
        var id = Field("Id");
        var proposal = Proposal(Entity("Order", hasPrimaryKey: true, explicitKey, id));

        var repairs = CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal);

        Assert.Empty(repairs);
        Assert.False(id.IsPrimaryKey);
        Assert.True(explicitKey.IsPrimaryKey);
    }

    [Fact]
    public void Leaves_keyless_entities_alone()
    {
        // HasPrimaryKey=false is a different contract: the entity must be read-only instead.
        var id = Field("Id");
        var proposal = Proposal(Entity("ReadOnlyView", hasPrimaryKey: false, id));

        var repairs = CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal);

        Assert.Empty(repairs);
        Assert.False(id.IsPrimaryKey);
    }

    [Fact]
    public void Leaves_entities_without_an_id_field_alone()
    {
        // Nothing to promote, and the validator has no complaint for this shape either.
        var proposal = Proposal(Entity("Metric", hasPrimaryKey: true, Field("Name"), Field("Value")));

        var repairs = CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal);

        Assert.Empty(repairs);
    }

    [Fact]
    public void Matches_the_id_field_case_insensitively()
    {
        var lower = Field("id");
        var proposal = Proposal(Entity("Order", hasPrimaryKey: true, lower));

        var repairs = CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal);

        Assert.True(lower.IsPrimaryKey);
        Assert.Single(repairs);
    }

    [Fact]
    public void Repairs_each_affected_entity_independently()
    {
        var firstId = Field("Id");
        var secondId = Field("Id");
        var proposal = Proposal(
            Entity("Order", hasPrimaryKey: true, firstId),
            Entity("OrderItem", hasPrimaryKey: true, secondId),
            Entity("AlreadyKeyed", hasPrimaryKey: true, Field("Id", isPrimaryKey: true)));

        var repairs = CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal);

        Assert.True(firstId.IsPrimaryKey);
        Assert.True(secondId.IsPrimaryKey);
        Assert.Equal(2, repairs.Count);
    }

    [Fact]
    public void Does_not_report_a_repair_when_nothing_changed()
    {
        var proposal = Proposal(Entity("Order", hasPrimaryKey: true, Field("Id", isPrimaryKey: true)));

        Assert.Empty(CodeMasterAgentToolFactory.RepairImplicitPrimaryKeys(proposal));
    }
}
