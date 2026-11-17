using System.Collections.Concurrent;
using CodeMaster.Agent.Contracts;
using CodeMaster.Domain.Entities.Ai;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Decides whether the configured model can read images, so an image turn can either be sent
/// as multimodal content or explained to the user instead of failing with an upstream error.
///
/// There is no reliable "vision" flag on an OpenAI-compatible endpoint, so the answer comes
/// from a real probe: one tiny PNG with a two-word instruction. The result is cached per
/// connection fingerprint, so a conversation pays for the probe at most once per process.
///
/// The probe deliberately distinguishes two failure modes:
/// <list type="bullet">
/// <item>the endpoint answered but rejected the image — the model has no vision, and the turn
/// continues without images plus an explicit explanation;</item>
/// <item>anything else (auth, network, rate limit, 5xx) — that is a real problem the turn must
/// report as itself, so the probe returns "assume supported" and stays uncached.</item>
/// </list>
/// Guessing "no vision" from a network blip would silently drop the user's images and blame
/// the model for it.
/// </summary>
public static class AiVisionCapabilityProbe
{
    /// <summary>An 8x8 white PNG. Small enough to be free, real enough to be accepted.</summary>
    private const string ProbePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAD0lEQVR4nGP4jwMwDC0JALoev0Ewkwr8AAAAAElFTkSuQmCC";

    private static readonly ConcurrentDictionary<string, bool> Cache = new(StringComparer.Ordinal);

    /// <summary>Message shown when the model turned out not to support images.</summary>
    public const string UnsupportedMessage =
        "当前模型不支持图片识别，这条消息里的图片没有发给模型，只按文字内容处理。";

    /// <summary>Actionable follow-up shown next to <see cref="UnsupportedMessage"/>.</summary>
    public const string UnsupportedSuggestion =
        "可以改用支持视觉的模型（如 gpt-4o、qwen-vl-max、glm-4v、doubao-vision），或者把图片内容用文字描述一下。";

    public static void Reset() => Cache.Clear();

    public static async Task<bool> SupportsVisionAsync(
        IAiProviderFactory providerFactory,
        SysAiProvider provider,
        CancellationToken cancellationToken = default)
    {
        var key = AiProviderService.DescribeConnection(provider);
        if (Cache.TryGetValue(key, out var known))
            return known;

        try
        {
            using var client = providerFactory.CreateChatClient(provider);
            var message = new ChatMessage(ChatRole.User,
            [
                new TextContent("Reply with OK."),
                new DataContent(Convert.FromBase64String(ProbePngBase64), "image/png")
            ]);
            await client.GetResponseAsync(
                [message],
                new ChatOptions { MaxOutputTokens = 8 },
                cancellationToken);

            Cache[key] = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            // The user cancelled. Do not cache a verdict derived from it.
            return true;
        }
        catch (Exception exception) when (AiRequestFailure.LooksLikeVisionRejection(exception))
        {
            Cache[key] = false;
            return false;
        }
        catch (Exception)
        {
            // Auth, network, quota, 5xx: not a statement about vision support. Let the real
            // turn run and report its own error.
            return true;
        }
    }
}
