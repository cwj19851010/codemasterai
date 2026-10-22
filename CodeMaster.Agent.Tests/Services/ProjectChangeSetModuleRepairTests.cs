using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// A model regularly describes a module that does not exist yet as a moduleUpdates entry with
/// moduleId 0 and then references it by name from every entity. That used to fail the whole change
/// set with "Module update target '0' does not exist in this project." plus one "must reference an
/// existing or proposed module" per entity, so these tests pin the repair that resolves it.
/// </summary>
public sealed class ProjectChangeSetModuleRepairTests
{
    private const string MissingModuleReference = "must reference an existing or proposed module";
    private const string MissingUpdateTarget = "Module update target";

    [Fact]
    public void Module_addressed_by_name_only_becomes_a_create()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "engineering management",
            ModuleUpdates =
            {
                new UpdateModuleProposal
                {
                    ModuleId = 0,
                    ModuleName = "EngineeringManagement",
                    ModuleDescription = "工程管理平台"
                }
            },
            Entities = { Entity("Project", "EngineeringManagement") }
        };

        var repairs = ProjectChangeSetIssueRepairs.ResolveIdlessModuleUpdates(
            proposal, Array.Empty<AiModuleBlueprintDto>());

        Assert.Empty(proposal.ModuleUpdates);
        var created = Assert.Single(proposal.Modules);
        Assert.Equal("EngineeringManagement", created.ModuleName);
        Assert.Equal("工程管理平台", created.ModuleDescription);
        Assert.Single(repairs);

        var validation = CodeMasterProjectChangeSetRules.Validate(new AiProjectBlueprintDto(), proposal);
        Assert.DoesNotContain(validation.Errors, error => error.Contains(MissingModuleReference));
        Assert.DoesNotContain(validation.Errors, error => error.Contains(MissingUpdateTarget));
    }

    [Fact]
    public void Module_addressed_by_name_only_updates_the_existing_module()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "rename module",
            ModuleUpdates =
            {
                new UpdateModuleProposal
                {
                    ModuleId = 0,
                    ModuleName = "EngineeringManagement",
                    ModuleDescription = "工程管理"
                }
            }
        };
        var existing = new AiModuleBlueprintDto { Id = 42, ModuleName = "EngineeringManagement", ModuleDescription = "工程管理" };

        ProjectChangeSetIssueRepairs.ResolveIdlessModuleUpdates(proposal, new[] { existing });

        var update = Assert.Single(proposal.ModuleUpdates);
        Assert.Equal(42, update.ModuleId);
        Assert.Empty(proposal.Modules);

        var validation = CodeMasterProjectChangeSetRules.Validate(
            new AiProjectBlueprintDto { Modules = { existing } }, proposal);
        Assert.DoesNotContain(validation.Errors, error => error.Contains(MissingUpdateTarget));
    }

    [Fact]
    public void Idless_update_without_a_name_is_dropped()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "empty update",
            ModuleUpdates = { new UpdateModuleProposal { ModuleId = 0 } }
        };

        var repairs = ProjectChangeSetIssueRepairs.ResolveIdlessModuleUpdates(
            proposal, Array.Empty<AiModuleBlueprintDto>());

        Assert.Empty(proposal.ModuleUpdates);
        Assert.Empty(proposal.Modules);
        Assert.Single(repairs);
    }

    [Fact]
    public void Entity_without_a_module_takes_the_only_candidate()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "single module",
            Modules = { new CreateModuleProposal { ModuleName = "EngineeringManagement", ModuleDescription = "工程管理" } },
            Entities =
            {
                new CreateEntityProposal
                {
                    Name = "Supplier",
                    Description = "供应商",
                    Fields = { new CreateEntityFieldProposal { Name = "Name", Description = "名称" } }
                }
            }
        };

        var repairs = ProjectChangeSetIssueRepairs.AssignSingleCandidateModule(
            proposal, Array.Empty<AiModuleBlueprintDto>());

        Assert.Equal("EngineeringManagement", proposal.Entities[0].ModuleName);
        Assert.Single(repairs);
        var validation = CodeMasterProjectChangeSetRules.Validate(new AiProjectBlueprintDto(), proposal);
        Assert.DoesNotContain(validation.Errors, error => error.Contains(MissingModuleReference));
    }

    [Fact]
    public void Ambiguous_module_choice_is_left_to_the_model()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "two modules",
            Modules =
            {
                new CreateModuleProposal { ModuleName = "MasterData", ModuleDescription = "基础数据" },
                new CreateModuleProposal { ModuleName = "Purchasing", ModuleDescription = "采购" }
            },
            Entities =
            {
                new CreateEntityProposal
                {
                    Name = "Supplier",
                    Description = "供应商",
                    Fields = { new CreateEntityFieldProposal { Name = "Name", Description = "名称" } }
                }
            }
        };

        var repairs = ProjectChangeSetIssueRepairs.AssignSingleCandidateModule(
            proposal, Array.Empty<AiModuleBlueprintDto>());

        Assert.Empty(repairs);
        Assert.True(proposal.Entities[0].ModuleId <= 0);
        Assert.True(string.IsNullOrWhiteSpace(proposal.Entities[0].ModuleName));
    }

    private static CreateEntityProposal Entity(string name, string moduleName) => new()
    {
        Name = name,
        Description = name,
        ModuleName = moduleName,
        Fields = { new CreateEntityFieldProposal { Name = "Name", Description = "名称" } }
    };
}
