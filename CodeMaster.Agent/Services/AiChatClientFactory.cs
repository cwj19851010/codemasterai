using Anthropic;
using CodeMaster.Agent.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace CodeMaster.Agent.Services;

public static class AiChatClientFactory
{
    /// <summary>
    /// LLM 调用重试次数（429/5xx/瞬时网络抖动时自动重试，避免单次失败击穿整个 agent turn）
    /// </summary>
    public const int OpenAiCompatibleMaxRetries = 2;
    public static readonly TimeSpan OpenAiCompatibleNetworkTimeout = TimeSpan.FromSeconds(150);
    public const int QwenMaxRetries = 1;

    /// <summary>Environment variable that overrides the whole-request bound, in seconds.</summary>
    internal const string NetworkTimeoutEnvVar = "CODEMASTER_NETWORK_TIMEOUT_SECONDS";

    /// <summary>Environment variable that overrides the Qwen whole-request bound, in seconds.</summary>
    internal const string QwenNetworkTimeoutEnvVar = "CODEMASTER_QWEN_NETWORK_TIMEOUT_SECONDS";

    /// <summary>
    /// Whole-request bound for Qwen-compatible providers, in seconds.
    /// </summary>
    /// <remarks>
    /// A Code-mode turn streams a very large plan: measured dumps run past 700 KB of SSE payload
    /// before the model is done. The previous 90s bound cut those turns off mid-flight with
    /// "The operation was cancelled because it exceeded the configured timeout of 0:01:30", which
    /// killed the whole generation turn before any metadata had been written.
    /// </remarks>
    internal const int DefaultQwenNetworkSeconds = 900;

    public static readonly TimeSpan QwenNetworkTimeout = ResolveNetworkTimeout(
        Environment.GetEnvironmentVariable(QwenNetworkTimeoutEnvVar),
        DefaultQwenNetworkSeconds);

    public static readonly TimeSpan ResolvedNetworkTimeout = ResolveNetworkTimeout(
        Environment.GetEnvironmentVariable(NetworkTimeoutEnvVar),
        (int)OpenAiCompatibleNetworkTimeout.TotalSeconds);

    /// <summary>
    /// Parses a configured whole-request bound, falling back to <paramref name="fallbackSeconds"/>
    /// when the value is unset, unparseable or non-positive.
    /// </summary>
    internal static TimeSpan ResolveNetworkTimeout(string? rawSeconds, int fallbackSeconds)
        => int.TryParse(rawSeconds, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(fallbackSeconds);
    /// <summary>
    /// Default idle bound for a streaming response, in seconds.
    /// </summary>
    /// <remarks>
    /// A reasoning model can stay silent for minutes while it composes a large plan. The previous
    /// hardcoded 60s bound aborted generation mid-flight with "Streaming response exceeded the 60s
    /// idle timeout" whenever the model thought for longer than that before emitting its first
    /// chunk.
    /// </remarks>
    internal const int DefaultStreamIdleSeconds = 300;

    /// <summary>Environment variable that overrides the idle bound, in seconds.</summary>
    internal const string StreamIdleTimeoutEnvVar = "CODEMASTER_STREAM_IDLE_TIMEOUT_SECONDS";

    public static readonly TimeSpan StreamIdleTimeout =
        ResolveStreamIdleTimeout(Environment.GetEnvironmentVariable(StreamIdleTimeoutEnvVar));

    /// <summary>
    /// Parses a configured idle bound, falling back to <see cref="DefaultStreamIdleSeconds"/> when
    /// the value is unset, unparseable or non-positive.
    /// </summary>
    internal static TimeSpan ResolveStreamIdleTimeout(string? rawSeconds)
        => int.TryParse(rawSeconds, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(DefaultStreamIdleSeconds);

    public static IChatClient Create(AiProviderConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelName))
            throw new ArgumentException("Model name is required.");

        return settings.ProviderType switch
        {
            AiProviderTypes.OpenAiCompatible => CreateOpenAiCompatibleClient(settings),
            AiProviderTypes.Anthropic => CreateAnthropicClient(settings),
            _ => throw new InvalidOperationException($"Unsupported AI provider type: {settings.ProviderType}")
        };
    }

    private static IChatClient CreateOpenAiCompatibleClient(AiProviderConnectionSettings settings)
    {
        var isQwen = OpenAiCompatibleHttpHandler.IsQwenCompatible(settings.BaseUrl, settings.ModelName);
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = isQwen ? QwenNetworkTimeout : ResolvedNetworkTimeout,
            RetryPolicy = new ClientRetryPolicy(isQwen ? QwenMaxRetries : OpenAiCompatibleMaxRetries)
        };
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            options.Endpoint = new Uri(settings.BaseUrl.Trim().TrimEnd('/') + "/");

        var httpClient = new HttpClient(new OpenAiCompatibleHttpHandler(
            settings.ExtraHeadersJson,
            settings.CompatibilityMode,
            settings.BaseUrl,
            settings.ModelName))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        options.Transport = new HttpClientPipelineTransport(httpClient);

        var apiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? "local-model" : settings.ApiKey;
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return client.GetChatClient(settings.ModelName).AsIChatClient();
    }

    public static ChatClientAgentOptions CreateAgentOptions(
        string instructions,
        string name,
        string description,
        IList<AITool> tools,
        string? compatibilityMode)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = instructions,
            Tools = tools
        };

        if (string.Equals(compatibilityMode, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase))
        {
            chatOptions.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.None
            };
        }

        return new ChatClientAgentOptions
        {
            Name = name,
            Description = description,
            ChatOptions = chatOptions
        };
    }

    public static ChatClientAgentRunOptions? CreateRunOptions(
        string? compatibilityMode,
        string? requiredToolName = null)
    {
        if (!string.Equals(compatibilityMode, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(requiredToolName))
        {
            return null;
        }

        var chatOptions = new ChatOptions();
        if (string.Equals(compatibilityMode, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase))
        {
            chatOptions.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.None
            };
        }

        if (!string.IsNullOrWhiteSpace(requiredToolName))
            chatOptions.ToolMode = ChatToolMode.RequireSpecific(requiredToolName);

        return new ChatClientAgentRunOptions(chatOptions);
    }

    public static ChatClientAgentRunOptions CreateVisibleResponseRunOptions(string? compatibilityMode)
    {
        var chatOptions = new ChatOptions
        {
            ToolMode = ChatToolMode.None
        };
        if (string.Equals(compatibilityMode, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase))
        {
            chatOptions.Reasoning = new ReasoningOptions
            {
                Effort = ReasoningEffort.High,
                Output = ReasoningOutput.None
            };
        }

        return new ChatClientAgentRunOptions(chatOptions);
    }

    public static ChatClientAgent CreateAgent(
        IChatClient chatClient,
        string instructions,
        string name,
        string description,
        IList<AITool> tools,
        string? compatibilityMode)
    {
        return new ChatClientAgent(
            chatClient,
            CreateAgentOptions(instructions, name, description, tools, compatibilityMode));
    }

    private static IChatClient CreateAnthropicClient(AiProviderConnectionSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("Anthropic requires an API key.");

        var client = new AnthropicClient
        {
            ApiKey = settings.ApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? "https://api.anthropic.com"
                : settings.BaseUrl.Trim().TrimEnd('/'),
            HttpClient = new HttpClient
            {
                Timeout = OpenAiCompatibleNetworkTimeout
            },
            MaxRetries = OpenAiCompatibleMaxRetries
        };

        return client.AsIChatClient(settings.ModelName);
    }
}
