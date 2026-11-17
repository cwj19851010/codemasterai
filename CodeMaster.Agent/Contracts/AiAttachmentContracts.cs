namespace CodeMaster.Agent.Contracts;

/// <summary>
/// Something the user attached to one chat message: either a text document or an image.
///
/// Only the descriptor travels with the message. The body stays on disk and is injected into
/// the model input for the turn it was attached to, so a 40 KB requirements document does not
/// have to live inside <c>sys_ai_message.content</c> (which the Agent truncates at 8000
/// characters for every other message).
/// </summary>
public sealed class AiAttachmentRef
{
    /// <summary>Attachment kinds. Kept as strings so the value survives JSON round trips.</summary>
    public const string TextKind = "text";
    public const string ImageKind = "image";

    /// <summary>Opaque, conversation-scoped file name. Never a path.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Original file name shown to the user and to the model.</summary>
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>Character count after decoding. Zero for images.</summary>
    public int Chars { get; set; }

    /// <summary><see cref="TextKind"/> or <see cref="ImageKind"/>.</summary>
    public string Kind { get; set; } = TextKind;

    /// <summary>MIME type, for example <c>text/markdown</c> or <c>image/png</c>.</summary>
    public string MediaType { get; set; } = "text/markdown";

    public bool IsImage => string.Equals(Kind, ImageKind, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One image payload shipped to the local execution client, which builds the multimodal
/// message itself. Only used by <c>BeginLocalAiChatResult</c>; the server path reads the bytes
/// from disk directly and never serialises them.
/// </summary>
public sealed class AiImagePayload
{
    public string Name { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/png";
    public string Base64 { get; set; } = string.Empty;
}

/// <summary>
/// Hard limits shared by the upload endpoint, the prompt builder and the read tool.
/// Keeping them in one place is what makes the paths agree on what "too large" means.
/// </summary>
public static class AiAttachmentLimits
{
    public const int MaxFilesPerMessage = 5;

    /// <summary>1 MB. A text requirement document is normally far below this.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>Per-document cap when injected into the prompt.</summary>
    public const int MaxCharsPerDocument = 40000;

    /// <summary>Cap for all documents attached to one message.</summary>
    public const int MaxTotalChars = 80000;

    /// <summary>4 MB per image. A screenshot or mockup is normally well below this.</summary>
    public const long MaxImageBytes = 4 * 1024 * 1024;

    /// <summary>Cap for all images attached to one message.</summary>
    public const long MaxTotalImageBytes = 8 * 1024 * 1024;

    /// <summary>Extensions accepted as text documents.</summary>
    public static readonly string[] AllowedExtensions = [".md", ".markdown", ".txt", ".text"];

    /// <summary>Extensions accepted as images, mapped to the MIME type sent to the model.</summary>
    public static readonly IReadOnlyDictionary<string, string> AllowedImageExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif"
        };

    public static bool IsAllowedFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var extension = Path.GetExtension(fileName.Trim());
        return AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAllowedImageFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return AllowedImageExtensions.ContainsKey(Path.GetExtension(fileName.Trim()));
    }

    /// <summary>Every extension the upload endpoint accepts, text and image alike.</summary>
    public static bool IsAllowedAttachmentFileName(string? fileName)
        => IsAllowedFileName(fileName) || IsAllowedImageFileName(fileName);

    public static string ResolveMediaType(string? fileName)
    {
        var extension = Path.GetExtension(fileName?.Trim() ?? string.Empty);
        if (AllowedImageExtensions.TryGetValue(extension, out var imageType))
            return imageType;

        return extension.ToLowerInvariant() switch
        {
            ".md" or ".markdown" => "text/markdown",
            _ => "text/plain"
        };
    }
}
