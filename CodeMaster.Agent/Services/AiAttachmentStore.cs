using System.Text;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Stores the documents and images a user attaches to an Agent message.
/// </summary>
public interface IAiAttachmentStore
{
    /// <summary>
    /// Persists one uploaded attachment. Throws <see cref="ArgumentException"/> with a
    /// user-facing Chinese message when the file is rejected.
    /// </summary>
    Task<AiAttachmentRef> SaveAsync(
        long conversationId,
        string fileName,
        Stream content,
        long length,
        CancellationToken cancellationToken = default);

    /// <summary>True when the token resolves to a stored file of this conversation.</summary>
    bool Exists(long conversationId, string? token);

    /// <summary>
    /// Reads a stored document back in full. Returns null when the token does not belong to
    /// the conversation, so a guessed token can never reach another user's file.
    /// </summary>
    Task<string?> ReadAllTextAsync(
        long conversationId,
        string? token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a slice of a stored document, used by the prompt builder and by the
    /// <c>read_attachment</c> tool when the model pages through a long file.
    /// </summary>
    Task<string?> ReadTextAsync(
        long conversationId,
        string? token,
        int maxChars = AiAttachmentLimits.MaxCharsPerDocument,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>Raw bytes of a stored attachment, or null when it is not reachable.</summary>
    Task<byte[]?> ReadBytesAsync(
        long conversationId,
        string? token,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the <c>&lt;document&gt;</c> blocks injected into the model input for the turn the
    /// documents were attached to. Returns an empty string when nothing could be read.
    /// </summary>
    Task<string> BuildDocumentContextAsync(
        long conversationId,
        IReadOnlyList<AiAttachmentRef>? attachments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the images of a message so they can be sent as multimodal content. The total is
    /// capped; images past the cap are dropped rather than failing the turn.
    /// </summary>
    Task<IReadOnlyList<(byte[] Bytes, string MediaType, string Name)>> ReadImageContentsAsync(
        long conversationId,
        IReadOnlyList<AiAttachmentRef>? attachments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// File-system backed attachment store.
///
/// Attachments live under <c>{ContentRoot}/App_Data/agent-attachments/{conversationId}/</c>.
/// The conversation folder is part of the address, so the token alone is never enough to
/// read a file — every read is scoped by the conversation the caller already proved it owns.
/// </summary>
public sealed partial class AiAttachmentStore : IAiAttachmentStore
{
    private const string DefaultFolderName = "agent-attachments";

    private readonly string _root;

    public AiAttachmentStore(
        IWebHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        var configured = configuration?["Agent:AttachmentRoot"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _root = configured.Trim();
            return;
        }

        var contentRoot = environment?.ContentRootPath;
        if (string.IsNullOrWhiteSpace(contentRoot))
            contentRoot = AppContext.BaseDirectory;

        _root = Path.Combine(contentRoot, "App_Data", DefaultFolderName);
    }

    public string RootPath => _root;

    [GeneratedRegex(@"^[a-f0-9]{32}\.(md|markdown|txt|text|png|jpg|jpeg|webp|gif)$", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    public async Task<AiAttachmentRef> SaveAsync(
        long conversationId,
        string fileName,
        Stream content,
        long length,
        CancellationToken cancellationToken = default)
    {
        var displayName = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("文件名为空，无法识别附件。");

        if (!AiAttachmentLimits.IsAllowedAttachmentFileName(displayName))
        {
            throw new ArgumentException(
                $"暂不支持「{Path.GetExtension(displayName)}」这类文件。可以附加 {string.Join(" / ", AiAttachmentLimits.AllowedExtensions)} 文档，" +
                $"或 {string.Join(" / ", AiAttachmentLimits.AllowedImageExtensions.Keys)} 图片。");
        }

        if (length <= 0)
            throw new ArgumentException($"「{displayName}」是空文件。");

        var isImage = AiAttachmentLimits.IsAllowedImageFileName(displayName);
        var maxBytes = isImage ? AiAttachmentLimits.MaxImageBytes : AiAttachmentLimits.MaxFileBytes;
        if (length > maxBytes)
        {
            throw new ArgumentException(
                $"「{displayName}」超过 {maxBytes / 1024} KB 上限，请压缩或拆分后再上传。");
        }

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await content.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
            throw new ArgumentException($"「{displayName}」是空文件。");

        var mediaType = AiAttachmentLimits.ResolveMediaType(displayName);
        int chars;
        if (isImage)
        {
            if (!LooksLikeImage(bytes, mediaType))
            {
                throw new ArgumentException(
                    $"「{displayName}」的内容和扩展名不一致，无法作为图片识别。请确认文件没有损坏或改名。");
            }

            chars = 0;
        }
        else
        {
            var text = DecodeText(bytes);
            if (LooksBinary(text))
                throw new ArgumentException($"「{displayName}」不是纯文本文件，无法作为文档读取。");

            chars = text.Length;
        }

        var extension = Path.GetExtension(displayName).ToLowerInvariant();
        var token = $"{Guid.NewGuid():N}{extension}";
        var directory = GetConversationDirectory(conversationId);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, token);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);

        return new AiAttachmentRef
        {
            Token = token,
            Name = displayName,
            Size = bytes.Length,
            Chars = chars,
            Kind = isImage ? AiAttachmentRef.ImageKind : AiAttachmentRef.TextKind,
            MediaType = mediaType
        };
    }

    public bool Exists(long conversationId, string? token)
    {
        var path = ResolvePath(conversationId, token);
        return path != null && File.Exists(path);
    }

    public async Task<string?> ReadAllTextAsync(
        long conversationId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(conversationId, token);
        if (path == null || !File.Exists(path))
            return null;

        // An image has no text form. Returning bytes decoded as text would inject noise.
        if (AiAttachmentLimits.IsAllowedImageFileName(path))
            return null;

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return DecodeText(bytes);
    }

    public async Task<string?> ReadTextAsync(
        long conversationId,
        string? token,
        int maxChars = AiAttachmentLimits.MaxCharsPerDocument,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var text = await ReadAllTextAsync(conversationId, token, cancellationToken);
        if (text == null)
            return null;

        var start = Math.Clamp(offset, 0, text.Length);
        var length = maxChars > 0 ? Math.Min(maxChars, text.Length - start) : text.Length - start;
        return text.Substring(start, length);
    }

    public async Task<byte[]?> ReadBytesAsync(
        long conversationId,
        string? token,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(conversationId, token);
        if (path == null || !File.Exists(path))
            return null;

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public async Task<string> BuildDocumentContextAsync(
        long conversationId,
        IReadOnlyList<AiAttachmentRef>? attachments,
        CancellationToken cancellationToken = default)
    {
        if (attachments == null || attachments.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        var budget = AiAttachmentLimits.MaxTotalChars;
        foreach (var attachment in attachments.Take(AiAttachmentLimits.MaxFilesPerMessage))
        {
            if (budget <= 0)
                break;

            // Images travel as multimodal content, never as text.
            if (attachment.IsImage)
                continue;

            var limit = Math.Min(AiAttachmentLimits.MaxCharsPerDocument, budget);
            var text = await ReadTextAsync(conversationId, attachment.Token, limit, 0, cancellationToken);
            if (text == null)
                continue;

            var truncated = attachment.Chars > text.Length;
            builder.AppendLine($"<document name=\"{Escape(attachment.Name)}\" token=\"{attachment.Token}\">");
            builder.AppendLine(text);
            if (truncated)
                builder.AppendLine($"[文档较长，此处只包含前 {text.Length} 个字符，完整内容请调用 read_attachment 分段读取]");
            builder.AppendLine("</document>");
            budget -= text.Length;
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<IReadOnlyList<(byte[] Bytes, string MediaType, string Name)>> ReadImageContentsAsync(
        long conversationId,
        IReadOnlyList<AiAttachmentRef>? attachments,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(byte[], string, string)>();
        if (attachments == null || attachments.Count == 0)
            return results;

        long total = 0;
        foreach (var attachment in attachments.Where(item => item.IsImage).Take(AiAttachmentLimits.MaxFilesPerMessage))
        {
            var bytes = await ReadBytesAsync(conversationId, attachment.Token, cancellationToken);
            if (bytes == null)
                continue;

            if (total + bytes.Length > AiAttachmentLimits.MaxTotalImageBytes)
                break;

            total += bytes.Length;
            var mediaType = string.IsNullOrWhiteSpace(attachment.MediaType)
                ? AiAttachmentLimits.ResolveMediaType(attachment.Name)
                : attachment.MediaType;
            results.Add((bytes, mediaType, attachment.Name));
        }

        return results;
    }

    /// <summary>
    /// Resolves a token to a physical path inside the conversation folder.
    /// A token is validated by shape first, so path separators and <c>..</c> cannot reach
    /// this method at all; the containment check afterwards is belt and braces.
    /// </summary>
    public string? ResolvePath(long conversationId, string? token)
    {
        var trimmed = token?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !TokenPattern().IsMatch(trimmed))
            return null;

        var directory = GetConversationDirectory(conversationId);
        var path = Path.GetFullPath(Path.Combine(directory, trimmed));
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private string GetConversationDirectory(long conversationId)
        => Path.Combine(_root, conversationId.ToString());

    /// <summary>
    /// Decodes a text document. UTF-8 (with or without BOM) is the norm, but a .txt saved by
    /// an older Chinese Windows editor is GB18030, and decoding that as UTF-8 produces a wall
    /// of replacement characters that silently poisons the prompt. The fallback keeps those
    /// files readable instead of quietly corrupting them.
    /// </summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8. Fall through to the legacy code page below.
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// NUL bytes and a high share of control characters mean the file is not text at all.
    /// Accepting it would inject binary noise into the prompt.
    /// </summary>
    public static bool LooksBinary(string text)
    {
        if (text.Length == 0)
            return true;

        var sample = text.Length > 4096 ? text[..4096] : text;
        if (sample.Contains('\0'))
            return true;

        var control = sample.Count(character =>
            char.IsControl(character) && character is not ('\r' or '\n' or '\t'));
        return control * 100 / sample.Length > 5;
    }

    /// <summary>
    /// Checks the magic bytes against the declared MIME type. A .png that is really a ZIP
    /// would otherwise be sent to the model and rejected upstream with an opaque error.
    /// </summary>
    public static bool LooksLikeImage(byte[] bytes, string mediaType)
    {
        if (bytes.Length < 12)
            return false;

        return mediaType switch
        {
            "image/png" => bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
            "image/jpeg" => bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/gif" => bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38,
            "image/webp" => bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,
            _ => false
        };
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
