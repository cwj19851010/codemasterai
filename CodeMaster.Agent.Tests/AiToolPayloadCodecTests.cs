using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Domain.Entities.Ai;
using System.Text;

namespace CodeMaster.Agent.Tests;

public class AiToolPayloadCodecTests
{
    [Fact]
    public void Encode_ShortJson_StoredAsIs()
    {
        var json = "{\"ok\":true}";

        var encoded = AiToolPayloadCodec.Encode(json);

        Assert.Equal(json, encoded);
    }

    [Fact]
    public void Encode_LargeCompressibleJson_RoundTrips()
    {
        var json = new string('a', 20000);

        var encoded = AiToolPayloadCodec.Encode(json);

        Assert.StartsWith("gzip:", encoded);
        Assert.True(encoded.Length < 7800);
        Assert.Equal(json, AiToolPayloadCodec.Decode(encoded));
    }

    [Fact]
    public void Encode_IncompressibleOversizedJson_Throws()
    {
        var random = new Random(42);
        var bytes = new byte[20000];
        random.NextBytes(bytes);
        var json = Convert.ToBase64String(bytes);

        var ex = Assert.Throws<InvalidOperationException>(() => AiToolPayloadCodec.Encode(json));

        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Encode_RespectsCustomMaximum()
    {
        var json = "ab";

        var encoded = AiToolPayloadCodec.Encode(json, maximumStoredLength: 5);

        Assert.Equal(json, encoded);
    }

    [Fact]
    public void Decode_PlainValue_ReturnedAsIs()
    {
        Assert.Equal("hello", AiToolPayloadCodec.Decode("hello"));
    }

    [Fact]
    public void Decode_GzipValue_RoundTrips()
    {
        var original = new string('x', 5000);
        var encoded = AiToolPayloadCodec.Encode(original);

        Assert.Equal(original, AiToolPayloadCodec.Decode(encoded));
    }

    [Fact]
    public void BinaryPayload_LargeIncompressibleJson_RoundTripsWithoutTextLimit()
    {
        var random = new Random(42);
        var bytes = new byte[200_000];
        random.NextBytes(bytes);
        var json = Convert.ToBase64String(bytes);

        var payload = AiToolPayloadCodec.EncodeBinary(json);

        Assert.True(payload.Length > 7_800);
        Assert.Equal(json, AiToolPayloadCodec.DecodeBinary(payload));
    }

    [Fact]
    public void ToolExecutionDto_PrefersBinaryPayloadAndKeepsLegacyFallback()
    {
        const string currentInput = "{\"summary\":\"large approval\"}";
        var current = CodeMasterAgentToolFactory.ToDto(new AiToolExecution
        {
            InputJson = "{\"storage\":\"gzip-binary-v1\"}",
            InputPayload = AiToolPayloadCodec.EncodeBinary(currentInput)
        });
        var legacy = CodeMasterAgentToolFactory.ToDto(new AiToolExecution
        {
            InputJson = AiToolPayloadCodec.Encode(new string('x', 10_000))
        });

        Assert.Equal(currentInput, current.InputJson);
        Assert.Equal(new string('x', 10_000), legacy.InputJson);
    }

    [Fact]
    public void ToolExecutionDto_exposes_work_plan_links()
    {
        var dto = CodeMasterAgentToolFactory.ToDto(new AiToolExecution
        {
            WorkPlanId = 10,
            WorkBatchId = 20,
            WorkTaskId = 30
        });

        Assert.Equal(10, dto.WorkPlanId);
        Assert.Equal(20, dto.WorkBatchId);
        Assert.Equal(30, dto.WorkTaskId);
    }
}
