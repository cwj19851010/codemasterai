using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Services;

// Keep text in memory for completion checks; persisted diagnostics contain only
// lengths, finish reasons and tool names, never provider credentials or payloads.
public sealed class AiResponseTraceChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private readonly List<AiModelReply> _replies = [];
    public IReadOnlyList<AiModelReply> Replies => _replies;
    public AiModelReply? LastReply => _replies.LastOrDefault();

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        _replies.Add(new(
            response.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant)?.Text ?? string.Empty,
            response.FinishReason?.ToString(),
            response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
                .Select(call => call.Name).Distinct(StringComparer.Ordinal).ToArray(),
            timer.ElapsedMilliseconds,
            true));
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var text = new StringBuilder();
        var tools = new HashSet<string>(StringComparer.Ordinal);
        string? finishReason = null;
        var completed = false;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                text.Append(update.Text);
                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                    tools.Add(call.Name);
                finishReason = update.FinishReason?.ToString() ?? finishReason;
                yield return update;
            }
            completed = true;
        }
        finally
        {
            _replies.Add(new(text.ToString(), finishReason, tools.ToArray(), timer.ElapsedMilliseconds, completed));
        }
    }

    public object[] BuildDiagnostics() => _replies.Select(reply => (object)new
    {
        finishReason = reply.FinishReason,
        textLength = reply.Text.Length,
        toolCalls = reply.ToolCalls,
        durationMs = reply.DurationMs,
        completed = reply.Completed
    }).ToArray();
}

public sealed record AiModelReply(string Text, string? FinishReason, string[] ToolCalls, long DurationMs, bool Completed);
