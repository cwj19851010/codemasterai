using CodeMaster.Agent.Contracts;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Services;

public static class AiConversationHistory
{
    public const int MaximumMessages = 8;
    public const int MaximumCharacters = 16000;

    public static List<ChatMessage> BuildBounded(IEnumerable<AiMessageDto> messages)
    {
        // Diagnostics are not user instructions and must not enter model history.
        var source = messages.Where(message => message.Role is "user" or "assistant" or "system").ToList();
        var selected = new List<AiMessageDto>();
        var characterCount = 0;
        for (var index = source.Count - 1; index >= 0 && selected.Count < MaximumMessages; index--)
        {
            var message = source[index];
            var contentLength = message.Content?.Length ?? 0;
            if (selected.Count > 0 && characterCount + contentLength > MaximumCharacters)
                break;

            selected.Add(message);
            characterCount += contentLength;
        }

        selected.Reverse();
        while (selected.Count > 1 && !string.Equals(selected[0].Role, "user", StringComparison.OrdinalIgnoreCase))
            selected.RemoveAt(0);

        return selected.Select(ToChatMessage).ToList();
    }

    private static ChatMessage ToChatMessage(AiMessageDto message)
    {
        var role = message.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            _ => ChatRole.User
        };
        return new ChatMessage(role, message.Content);
    }
}
