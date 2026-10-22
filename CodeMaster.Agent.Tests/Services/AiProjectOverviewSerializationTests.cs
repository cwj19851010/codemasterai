using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;

namespace CodeMaster.Agent.Tests.Services;

public class AiProjectOverviewSerializationTests
{
    [Fact]
    public void Structure_version_is_stable_and_changes_with_project_metadata()
    {
        var overview = CreateOverview();

        var first = CodeMasterProjectBlueprintService.ComputeStructureVersion(overview);
        var second = CodeMasterProjectBlueprintService.ComputeStructureVersion(overview);
        overview.Modules[0].Entities[0].Fields[0].BusinessDescription = "Changed business semantics";
        var changed = CodeMasterProjectBlueprintService.ComputeStructureVersion(overview);

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
        Assert.All(first, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void Project_summary_returns_small_not_modified_payload_for_known_version()
    {
        var overview = CreateOverview();
        overview.StructureVersion = CodeMasterProjectBlueprintService.ComputeStructureVersion(overview);

        var full = CodeMasterAgentToolFactory.BuildProjectSummaryInvocation(overview, null);
        var cached = CodeMasterAgentToolFactory.BuildProjectSummaryInvocation(
            overview,
            overview.StructureVersion.ToLowerInvariant());

        Assert.False(full.ProjectStructureNotModified);
        Assert.True(cached.ProjectStructureNotModified);
        Assert.Equal(overview.StructureVersion, cached.ProjectStructureVersion);
        Assert.Contains("\"notModified\":true", cached.Output);
        Assert.True(cached.Output.Length < full.Output.Length);
        Assert.DoesNotContain("ControlTestMaster", cached.Output);
        using var fullJson = JsonDocument.Parse(full.Output);
        using var cachedJson = JsonDocument.Parse(cached.Output);
        Assert.Equal(overview.StructureVersion, fullJson.RootElement.GetProperty("projectStructureHash").GetString());
        Assert.False(fullJson.RootElement.TryGetProperty("structureVersion", out _));
        Assert.Equal(overview.StructureVersion, cachedJson.RootElement.GetProperty("projectStructureHash").GetString());
        Assert.False(cachedJson.RootElement.TryGetProperty("structureVersion", out _));
    }

    [Fact]
    public void Compact_overview_omits_unused_nullable_field_configuration()
    {
        var overview = CreateOverview(28);
        var options = new JsonSerializerOptions(AgentJsonSerializer.Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(overview, options);

        Assert.DoesNotContain("selectDataSource", json);
        Assert.DoesNotContain("relatedEntityName", json);
        Assert.True(json.Length < 10_000, $"Overview was unexpectedly large: {json.Length} characters.");
    }

    [Fact]
    public void Compact_overview_includes_entity_and_field_business_semantics()
    {
        var overview = CreateOverview();
        overview.Modules[0].Entities[0].BusinessDescription = "用于验证项目级业务流程完整性。";
        overview.Modules[0].Entities[0].Fields[0].BusinessDescription = "业务编码，在当前租户内唯一。";

        var json = JsonSerializer.Serialize(overview, AgentJsonSerializer.Options);

        Assert.Contains("businessDescription", json);
        Assert.Contains("\\u7528\\u4E8E\\u9A8C\\u8BC1", json);
    }

    [Fact]
    public void Full_structure_version_changes_for_metadata_omitted_from_compact_overview()
    {
        var project = new AiProjectSummaryDto { Id = 1, ProjectName = "RegressionTesting" };
        var entity = new AiEntityBlueprintDto
        {
            Id = 3,
            ModuleId = 2,
            Name = "ControlTestMaster",
            TableName = "control_test_master",
            Fields =
            {
                new AiFieldBlueprintDto
                {
                    Id = 4,
                    Name = "Code",
                    ColumnName = "business_code",
                    DataType = "string",
                    MaxLength = 64,
                    FormControlType = "input"
                }
            }
        };
        var modules = new List<AiModuleBlueprintDto>
        {
            new() { Id = 2, ModuleName = "RegressionTesting", Entities = { entity } }
        };

        var first = CodeMasterProjectBlueprintService.ComputeStructureVersion(project, modules);
        entity.Fields[0].MaxLength = 128;
        entity.TableName = "control_test_master_v2";
        var changed = CodeMasterProjectBlueprintService.ComputeStructureVersion(project, modules);

        Assert.NotEqual(first, changed);

        var json = JsonSerializer.Serialize(entity, AgentJsonSerializer.Options);
        Assert.Contains("business_code", json);
    }

    [Fact]
    public void Chat_stream_final_event_uses_web_names_and_string_long_ids()
    {
        var streamEvent = new AiChatStreamEvent
        {
            Event = "final",
            RequestId = "request-1",
            Sequence = 7,
            Phase = "completed",
            Result = new AiChatResult
            {
                Message = new AiMessageDto
                {
                    Id = 9_223_372_036_854_775_000,
                    RequestId = "request-1",
                    Role = "assistant",
                    Content = "done"
                }
            }
        };

        var json = JsonSerializer.Serialize(streamEvent, AgentJsonSerializer.Options);

        Assert.Contains("\"event\":\"final\"", json);
        Assert.Contains("\"requestId\":\"request-1\"", json);
        Assert.Contains("\"sequence\":7", json);
        Assert.Contains("\"id\":\"9223372036854775000\"", json);
    }

    private static AiProjectOverviewDto CreateOverview(int fieldCount = 1)
    {
        return new AiProjectOverviewDto
        {
            Project = new AiProjectSummaryDto
            {
                Id = 1,
                ProjectName = "RegressionTesting",
                DisplayName = "Regression Testing"
            },
            Modules =
            [
                new AiModuleOverviewDto
                {
                    Id = 2,
                    ModuleName = "RegressionTesting",
                    Entities =
                    [
                        new AiEntityOverviewDto
                        {
                            Id = 3,
                            ModuleId = 2,
                            Name = "ControlTestMaster",
                            Fields = Enumerable.Range(1, fieldCount)
                                .Select(index => new AiFieldOverviewDto
                                {
                                    Id = index,
                                    Name = $"Field{index}",
                                    Description = $"Field {index}",
                                    DataType = "string",
                                    FormControlType = "input",
                                    FieldCategory = "Normal",
                                    OrderNum = index
                                })
                                .ToList()
                        }
                    ]
                }
            ]
        };
    }
}
