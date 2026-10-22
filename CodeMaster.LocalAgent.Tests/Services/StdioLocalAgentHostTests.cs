using System.Text;
using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.LocalAgent.Models;
using CodeMaster.LocalAgent.Services;

namespace CodeMaster.LocalAgent.Tests.Services;

public class StdioLocalAgentHostTests
{
    [Fact]
    public void SerializeProtocolResponse_preserves_snowflake_ids_as_strings()
    {
        const long executionId = 2082875495313182720;
        var response = new StdioLocalExecutionResponse
        {
            Id = "transport-id",
            Success = true,
            DataOnly = true,
            Data = new AiChatResult
            {
                PendingApprovals =
                [
                    new AiToolExecutionDto
                    {
                        Id = executionId,
                        ConversationId = 2082868555778494464,
                        ProjectId = 2078508707184287744,
                        ToolName = "project_change_set",
                        Status = "PendingApproval"
                    }
                ]
            }
        };

        var json = StdioLocalAgentHost.SerializeProtocolResponse(response);

        Assert.Contains($"\"id\":\"{executionId}\"", json);
        Assert.DoesNotContain($"\"id\":{executionId}", json);
    }

    [Fact]
    public async Task ProtocolStreams_RoundTripChineseTextAsUtf8WithoutBom()
    {
        const string line = "{\"content\":\"做一个主表和一个一对多的子表\"}";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
        using var reader = StdioLocalAgentHost.CreateProtocolReader(input);

        Assert.Equal(line, await reader.ReadLineAsync());

        await using var output = new MemoryStream();
        await using (var writer = StdioLocalAgentHost.CreateProtocolWriter(output))
        {
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
        }

        var bytes = output.ToArray();
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(line, Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void SerializeProtocolProgress_includes_transport_and_stream_fields()
    {
        var progress = new StdioLocalExecutionProgress
        {
            Id = "transport-id",
            Event = "text_delta",
            Phase = "generating",
            Text = "正在整理实体字段"
        };

        var json = StdioLocalAgentHost.SerializeProtocolProgress(progress);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("transport-id", root.GetProperty("id").GetString());
        Assert.Equal("progress", root.GetProperty("frameType").GetString());
        Assert.Equal("text_delta", root.GetProperty("event").GetString());
        Assert.Equal("正在整理实体字段", root.GetProperty("text").GetString());
    }
}
