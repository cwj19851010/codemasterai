using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Agent.Tests.Services;

public sealed class PrerequisiteReferenceValidationTests
{
    [Fact]
    public async Task Upsert_rejects_plan_with_missing_module_reference()
    {
        var blueprint = BuildEmptyBlueprint();
        var planService = TestablePlanServiceFactory.Create(blueprint);
        var request = BuildRequest("ef-module", new List<string> { "module:Engineering" });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => planService.UpsertForTest(request));
        Assert.Contains("module:Engineering", ex.Message);
    }

    [Fact]
    public async Task Upsert_rejects_plan_with_missing_entity_reference()
    {
        var blueprint = BuildEmptyBlueprint();
        var planService = TestablePlanServiceFactory.Create(blueprint);
        var request = BuildRequest("ef-project", new List<string> { "entity:Project" });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => planService.UpsertForTest(request));
        Assert.Contains("entity:Project", ex.Message);
    }

    [Fact]
    public async Task Upsert_accepts_plan_when_module_reference_exists()
    {
        var blueprint = BuildEmptyBlueprint();
        blueprint.Modules.Add(new AiModuleBlueprintDto { Id = 1, ModuleName = "Engineering" });
        var planService = TestablePlanServiceFactory.Create(blueprint);
        var request = BuildRequest("ef-project", new List<string> { "module:Engineering" });

        await planService.UpsertForTest(request);
    }

    [Fact]
    public async Task Upsert_accepts_plan_with_only_project_reference()
    {
        var blueprint = BuildEmptyBlueprint();
        var planService = TestablePlanServiceFactory.Create(blueprint);
        var request = BuildRequest("ef-module", new List<string> { "project" });

        await planService.UpsertForTest(request);
    }

    private static AiProjectBlueprintDto BuildEmptyBlueprint() => new()
    {
        SchemaVersion = 1,
        Modules = new List<AiModuleBlueprintDto>(),
        Controls = new List<AiControlCatalogDto>(),
        PageTemplates = new List<AiPageTemplateCatalogDto>(),
        ChildTemplates = new List<AiChildTemplateCatalogDto>()
    };

    private static UpsertAiProjectWorkPlanRequest BuildRequest(string taskKey, List<string> references)
    {
        return new UpsertAiProjectWorkPlanRequest
        {
            ProjectId = 1,
            ConversationId = 1,
            Title = "Test plan",
            Stages = new List<AiProjectWorkStageInput>
            {
                new AiProjectWorkStageInput
                {
                    StageKey = "EntityFields",
                    Kind = "EntityFields",
                    Title = "EntityFields",
                    Tasks = new List<AiProjectWorkTaskInput>
                    {
                        new AiProjectWorkTaskInput
                        {
                            TaskKey = taskKey,
                            OperationKind = "project_change_set",
                            SemanticReferences = references
                        }
                    }
                }
            }
        };
    }
}

internal sealed class StubBlueprintService : ICodeMasterProjectBlueprintService
{
    private readonly AiProjectBlueprintDto _blueprint;
    public StubBlueprintService(AiProjectBlueprintDto blueprint) => _blueprint = blueprint;

    public async Task<AiProjectBlueprintDto> GetProjectAsync(long projectId)
        => await Task.FromResult(_blueprint);
    public async Task<AiProjectBlueprintDto> GetProjectAsync(long projectId, AiCodegenCatalogDto? catalog = null)
        => await Task.FromResult(_blueprint);
    public async Task<AiCodegenCatalogDto> GetCatalogAsync()
        => await Task.FromResult(new AiCodegenCatalogDto());
    public async Task<AiProjectOverviewDto> GetProjectOverviewAsync(long projectId)
        => await Task.FromResult(new AiProjectOverviewDto
        {
            Modules = _blueprint.Modules.Select(m => new AiModuleOverviewDto
            {
                Id = m.Id,
                ModuleName = m.ModuleName,
                Entities = m.Entities.Select(e => new AiEntityOverviewDto { Id = e.Id, Name = e.Name }).ToList()
            }).ToList()
        });
    public async Task<AiEntityBlueprintDto> GetEntityAsync(long projectId, long? entityId, string? entityName)
        => await Task.FromResult(new AiEntityBlueprintDto());
    public async Task<AiUiPageBlueprintDto> GetUiPageAsync(long projectId, long? entityId, string? entityName, string pageType)
        => await Task.FromResult(new AiUiPageBlueprintDto());
    public async Task<AiProjectSummaryDto> GetProjectSummaryAsync(long projectId)
        => await Task.FromResult(new AiProjectSummaryDto { Id = projectId, ProjectName = "Test" });
}

internal static class TestablePlanServiceFactory
{
    public static AiProjectWorkPlanService Create(AiProjectBlueprintDto blueprint)
    {
        // Pick the constructor with the most parameters (the production one
        // exposes optional dependencies at the tail). Substitute every
        // dependency with null/default except the blueprint service which the
        // validation hook actually touches.
        var ctor = typeof(AiProjectWorkPlanService).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = ctor.GetParameters();
        var blueprintService = new StubBlueprintService(blueprint);
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var paramType = parameters[i].ParameterType;
            if (paramType == typeof(ICodeMasterProjectBlueprintService))
            {
                args[i] = blueprintService;
            }
            else if (parameters[i].HasDefaultValue)
            {
                args[i] = parameters[i].DefaultValue;
            }
            else if (paramType.IsValueType)
            {
                args[i] = Activator.CreateInstance(paramType);
            }
            else
            {
                args[i] = null;
            }
        }
        return (AiProjectWorkPlanService)ctor.Invoke(args);
    }

    public static async Task<AiProjectWorkPlanDto> UpsertForTest(this AiProjectWorkPlanService service, UpsertAiProjectWorkPlanRequest request)
    {
        var method = typeof(AiProjectWorkPlanService).GetMethod(
            "ValidatePrerequisiteReferencesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new MissingMethodException("ValidatePrerequisiteReferencesAsync");
        var task = (Task)method.Invoke(service, new object[] { request })!;
        await task;
        return new AiProjectWorkPlanDto();
    }
}