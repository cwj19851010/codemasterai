using System.ComponentModel;

namespace CodeMaster.Agent.Contracts;

/// <summary>Arguments for <c>read_project_file</c>.</summary>
public sealed class AiProjectFileReadQuery
{
    [Description("Project-relative path of a Markdown or plain-text document, for example README.md or docs/需求说明.md. Absolute paths and .. are rejected.")]
    public string Path { get; set; } = string.Empty;

    [Description("Optional character offset to continue reading a long document. Defaults to 0.")]
    public int? Offset { get; set; }

    [Description("Maximum characters to return, defaults to 40000 and is capped at 40000.")]
    public int? MaxChars { get; set; }
}

/// <summary>Arguments for <c>list_project_files</c>.</summary>
public sealed class AiProjectFileListQuery
{
    [Description("Optional project-relative sub directory to list, for example docs. Omit to list the whole project.")]
    public string? Directory { get; set; }

    [Description("Maximum number of files to list, defaults to 200.")]
    public int? MaxResults { get; set; }
}

/// <summary>Arguments for <c>read_attachment</c>.</summary>
public sealed class AiAttachmentReadQuery
{
    [Description("Attachment token from the message metadata, for example 4f1c9a7d2b8e4c6fa1d3e5b7c9a2f4e6.md.")]
    public string Token { get; set; } = string.Empty;

    [Description("Optional character offset to continue reading a long document. Defaults to 0.")]
    public int? Offset { get; set; }

    [Description("Maximum characters to return, defaults to 40000 and is capped at 40000.")]
    public int? MaxChars { get; set; }
}
