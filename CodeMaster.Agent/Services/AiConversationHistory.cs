using System.Text;
using CodeMaster.Agent.Contracts;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Services;

public static class AiConversationHistory
{
    public const int MaximumMessages = 8;
    public const int MaximumCharacters = 16000;

    /// <summary>
    /// Builds the bounded model history.
    /// <para>
    /// <paramref name="currentUserMessage"/> replaces the trailing user message. The stored copy
    /// only holds what the user typed, while the model input for the current turn also carries
    /// the inlined text of any attached document and, when the model can read them, the images.
    /// Without that replacement both would be missing exactly on the turn they matter most.
    /// </para>
    /// </summary>
    public static List<ChatMessage> BuildBounded(
        IEnumerable<AiMessageDto> messages,
        ChatMessage? currentUserMessage = null)
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

        var history = selected.Select(ToChatMessage).ToList();
        if (currentUserMessage != null &&
            history.Count > 0 &&
            history[^1].Role == ChatRole.User)
        {
            history[^1] = currentUserMessage;
        }

        return history;
    }

    /// <summary>
    /// Converts one stored message into the model-facing form: text plus, when the message
    /// carries them, images as multimodal content. Public so the local execution client can
    /// build the current turn from the same rules instead of re-implementing them.
    /// </summary>
    public static ChatMessage ToChatMessage(AiMessageDto message)
    {
        var role = message.Role switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            _ => ChatRole.User
        };

        var text = WithAttachmentTrace(message);
        if (message.Images.Count == 0)
            return new ChatMessage(role, text);

        // Multimodal message: the text part first, then the images it refers to.
        var contents = new List<AIContent> { new TextContent(text) };
        foreach (var image in message.Images)
        {
            if (string.IsNullOrWhiteSpace(image.Base64))
                continue;

            try
            {
                contents.Add(new DataContent(Convert.FromBase64String(image.Base64), image.MediaType));
            }
            catch (FormatException)
            {
                // A corrupt payload must not take the whole turn down.
            }
        }

        return new ChatMessage(role, contents);
    }

    /// <summary>
    /// Keeps a one-line record of the attachments a message carried, so a later turn knows they
    /// exist and can page through them with <c>read_attachment</c>. The body is not repeated
    /// here — that is what the token is for.
    /// </summary>
    private static string WithAttachmentTrace(AiMessageDto message)
    {
        if (message.Attachments.Count == 0)
            return message.Content;

        var builder = new StringBuilder(message.Content ?? string.Empty);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("[本条消息附加的附件，可用 read_attachment 按 token 重新读取文本内容]");
        foreach (var attachment in message.Attachments)
        {
            builder.AppendLine(attachment.IsImage
                ? $"- {attachment.Name}（图片，{attachment.Size / 1024} KB）"
                : $"- {attachment.Name}（{attachment.Chars} 字，token={attachment.Token}）");
        }

        return builder.ToString().TrimEnd();
    }
}
