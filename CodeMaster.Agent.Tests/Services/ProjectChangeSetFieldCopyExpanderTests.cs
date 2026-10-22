using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public class ProjectChangeSetFieldCopyExpanderTests
{
    [Fact]
    public async Task ExpandAsync_copies_business_fields_and_keeps_explicit_overrides()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Entities =
            {
                new CreateEntityProposal
                {
                    Name = "NewDetail",
                    CopyFieldsFromEntityName = "ControlTestDetail",
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "MasterId",
                            Description = "New master id",
                            DataType = "long",
                            FormControlType = "input",
                            OrderNum = 5
                        }
                    }
                }
            }
        };
        EntityBlueprintQuery? query = null;

        await ProjectChangeSetFieldCopyExpander.ExpandAsync(proposal, input =>
        {
            query = input;
            return Task.FromResult(new AiEntityBlueprintDto
            {
                Name = "ControlTestDetail",
                Fields =
                {
                    new AiFieldBlueprintDto
                    {
                        Name = "Id",
                        Description = "Primary key",
                        DataType = "long",
                        IsSystemField = true,
                        IsPrimaryKey = true
                    },
                    new AiFieldBlueprintDto
                    {
                        Name = "TextValue",
                        Description = "Text",
                        DataType = "string",
                        MaxLength = 200,
                        FormControlType = "input",
                        ShowInList = true,
                        ShowInDetail = true,
                        ShowInAddForm = true,
                        ShowInEditForm = true,
                        OrderNum = 10
                    },
                    new AiFieldBlueprintDto
                    {
                        Name = "MasterId",
                        Description = "Old master id",
                        DataType = "long",
                        FormControlType = "number",
                        OrderNum = 20
                    }
                }
            });
        });

        var entity = Assert.Single(proposal.Entities);
        Assert.Equal("ControlTestDetail", query?.EntityName);
        Assert.Null(entity.CopyFieldsFromEntityId);
        Assert.Null(entity.CopyFieldsFromEntityName);
        Assert.Equal(2, entity.Fields.Count);
        Assert.Equal("TextValue", entity.Fields[0].Name);
        Assert.Equal(200, entity.Fields[0].MaxLength);
        Assert.Equal("MasterId", entity.Fields[1].Name);
        Assert.Equal("New master id", entity.Fields[1].Description);
        Assert.Equal("input", entity.Fields[1].FormControlType);
    }

    [Fact]
    public async Task ExpandAsync_rejects_relation_bound_aggregate_fields()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Entities =
            {
                new CreateEntityProposal
                {
                    Name = "NewMaster",
                    CopyFieldsFromEntityId = 42
                }
            }
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectChangeSetFieldCopyExpander.ExpandAsync(proposal, _ =>
                Task.FromResult(new AiEntityBlueprintDto
                {
                    Name = "ControlTestMaster",
                    Fields =
                    {
                        new AiFieldBlueprintDto
                        {
                            Name = "DetailTotal",
                            Description = "Total",
                            DataType = "decimal",
                            FieldCategory = "Aggregate",
                            AggregateChildEntityId = 100,
                            AggregateChildFieldName = "Amount",
                            FormControlType = "number"
                        }
                    }
                })));

        Assert.Contains("aggregate fields", exception.Message);
    }
}
