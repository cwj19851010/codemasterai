using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public class OpenAiCompatibleHttpHandlerTests
{
    [Fact]
    public void Interactive_agent_timeout_and_retry_budget_are_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(150), AiChatClientFactory.OpenAiCompatibleNetworkTimeout);
        Assert.Equal(2, AiChatClientFactory.OpenAiCompatibleMaxRetries);
    }

    [Fact]
    public void ApplyHeaders_ReplacesSdkUserAgentAndKeepsAuthorization()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("User-Agent", "OpenAI/2.10.0 MEAI/10.6.0");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer original-key");

        var headers = OpenAiCompatibleHttpHandler.ParseExtraHeaders(
            "{\"X-Provider-Route\":\"primary\",\"Authorization\":\"Bearer replacement\"}");
        OpenAiCompatibleHttpHandler.ApplyHeaders(request, headers);

        Assert.Equal("CodeMaster/1.0", string.Join(" ", request.Headers.GetValues("User-Agent")));
        Assert.Equal("Bearer original-key", string.Join(" ", request.Headers.GetValues("Authorization")));
        Assert.Equal("primary", string.Join(" ", request.Headers.GetValues("X-Provider-Route")));
    }

    [Fact]
    public void ParseExtraHeaders_RejectsNestedValues()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            OpenAiCompatibleHttpHandler.ParseExtraHeaders("{\"X-Options\":{\"region\":\"cn\"}}"));

        Assert.Contains("must be a scalar value", exception.Message);
    }

    [Fact]
    public void OpenRouterReasoningBody_AddsReasoningWhenMissing()
    {
        var body = OpenAiCompatibleHttpHandler.OpenRouterReasoningHttpContent
            .AddOpenRouterReasoning("{\"model\":\"stealth/ox-alpha\",\"messages\":[]}");

        Assert.Contains("\"reasoning\":{\"enabled\":true}", body);
    }

    [Fact]
    public void OpenRouterReasoningBody_DoesNotOverwriteExistingReasoning()
    {
        var body = OpenAiCompatibleHttpHandler.OpenRouterReasoningHttpContent
            .AddOpenRouterReasoning("{\"reasoning\":{\"effort\":\"high\"}}");

        Assert.DoesNotContain("enabled", body);
        Assert.Contains("effort", body);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void FinishReasonNormalizer_MapsProviderSpecificValuesToStop(string value)
    {
        var body = $$"""
        {"choices":[{"message":{"role":"assistant","content":null},"finish_reason":"{{value}}"}]}
        """;

        var normalized = OpenAiCompatibleHttpHandler.FinishReasonNormalizingHttpContent
            .NormalizeFinishReasons(body);

        Assert.Contains("\"finish_reason\":\"stop\"", normalized);
        Assert.DoesNotContain($"\"finish_reason\":\"{value}\"", normalized);
    }

    [Fact]
    public void FinishReasonNormalizer_HandlesStreamingEventsAndPreservesStandardValues()
    {
        const string body = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                            "data: {\"choices\":[{\"delta\":{},\"finish_reason\": \"error\"}]}\n\n" +
                            "data: [DONE]\n\n";

        var normalized = OpenAiCompatibleHttpHandler.FinishReasonNormalizingHttpContent
            .NormalizeFinishReasons(body);

        Assert.Contains("\"finish_reason\":\"tool_calls\"", normalized);
        Assert.Contains("\"finish_reason\": \"stop\"", normalized);
        Assert.Contains("data: [DONE]", normalized);
    }

    [Theory]
    [InlineData("https://token-plan.maas.qianwenaiapi.com/compatible-mode/v1", "qwen3.8-flash")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus")]
    [InlineData("https://api.example.com/v1", "qwen-plus")]
    public void Detects_qwen_compatible_provider(string baseUrl, string modelName)
    {
        Assert.True(OpenAiCompatibleHttpHandler.IsQwenCompatible(baseUrl, modelName));
    }

    [Fact]
    public void Qwen_forced_tool_request_disables_thinking_without_changing_other_requests()
    {
        const string forced = "{\"model\":\"qwen3.8-flash\",\"tool_choice\":\"required\",\"messages\":[]}";
        const string automatic = "{\"model\":\"qwen3.8-flash\",\"tool_choice\":\"auto\",\"messages\":[]}";

        var forcedResult = OpenAiCompatibleHttpHandler.QwenForcedToolHttpContent
            .DisableThinkingForQwen(forced);
        var automaticResult = OpenAiCompatibleHttpHandler.QwenForcedToolHttpContent
            .DisableThinkingForQwen(automatic);

        Assert.Contains("\"enable_thinking\":false", forcedResult);
        Assert.Contains("\"enable_thinking\":false", automaticResult);
    }

    [Fact]
    public void Qwen_forced_object_tool_request_overwrites_existing_thinking_flag()
    {
        const string body = "{\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"upsert_project_work_plan\"}},\"enable_thinking\":true}";

        var result = OpenAiCompatibleHttpHandler.QwenForcedToolHttpContent
            .DisableThinkingForQwen(body);

        Assert.Contains("\"enable_thinking\":false", result);
        Assert.DoesNotContain("\"enable_thinking\":true", result);
    }

    [Theory]
    [InlineData("{\"name\":\"foo\"}", "{\"name\":\"foo\"}")]
    [InlineData("  { \"name\" : \"foo\" }  ", "{ \"name\" : \"foo\" }")]
    [InlineData("", "{}")]
    public void ToolCallArgumentsExtract_ReturnsValidJson_WhenAlreadyClean(string raw, string expected)
    {
        var (json, ok) = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.TryExtractJson(raw);
        Assert.True(ok);
        Assert.Equal(expected, json);
    }

    [Fact]
    public void ToolCallArgumentsExtract_StripsMarkdownFences()
    {
        const string raw = "```json\n{\"projectId\":2103854966820179968,\"stages\":[]}\n```";

        var (json, ok) = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.TryExtractJson(raw);

        Assert.True(ok);
        Assert.Equal("{\"projectId\":2103854966820179968,\"stages\":[]}", json);
    }

    [Fact]
    public void ToolCallArgumentsExtract_ExtractsLeadingJsonFromProse()
    {
        const string raw = "参数应该是这样的: {\"projectId\":2103854966820179968,\"stages\":[]} 请确认。";

        var (json, ok) = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.TryExtractJson(raw);

        Assert.True(ok);
        Assert.Equal("{\"projectId\":2103854966820179968,\"stages\":[]}", json);
    }

    [Fact]
    public void ToolCallArgumentsExtract_ReturnsFalse_WhenNoJsonFound()
    {
        const string raw = "no json here at all, just prose";

        var (json, ok) = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.TryExtractJson(raw);

        Assert.False(ok);
        Assert.Equal(string.Empty, json);
    }

    [Fact]
    public void Scrub_RewritesNonJsonArgumentsInSseStream()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"foo\",\"arguments\":\"args: {\\\"x\\\":1}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";

        var scrubbed = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.Scrub(body);

        Assert.Contains("\\u0022x\\u0022:1", scrubbed);
        Assert.DoesNotContain("args: {", scrubbed);
    }

    [Fact]
    public void Scrub_DropsToolCallWithUnrecoverableArguments()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"foo\",\"arguments\":\"no json here\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";

        var scrubbed = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.Scrub(body);

        Assert.DoesNotContain("\"name\":\"foo\"", scrubbed);
        Assert.Contains("\"tool_calls\":[]", scrubbed);
    }

    [Fact]
    public void Scrub_PreservesValidJsonUnchanged()
    {
        const string body =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"foo\",\"arguments\":\"{\\\"x\\\":1}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";

        var scrubbed = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.Scrub(body);

        Assert.Contains("\\u0022x\\u0022:1", scrubbed);
    }

    [Fact]
    public void Scrub_RepairsBadArgumentsInOutgoingRequestMessages()
    {
        const string body =
            "{\"messages\":[{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[" +
            "{\"id\":\"call_1\",\"function\":{\"name\":\"upsert_project_work_plan\",\"arguments\":\"args: {\\\"projectId\\\":1}\"}}]}]}";

        var scrubbed = OpenAiCompatibleHttpHandler.ToolCallArgumentsScrubber.Scrub(body);
        Assert.Contains("projectId", scrubbed);
    }
}
