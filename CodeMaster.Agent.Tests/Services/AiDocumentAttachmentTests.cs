using System.Text;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// Cover for the document-reading feature: chat attachments (.md / .txt) and the
/// <c>read_project_file</c> tool.
///
/// The security-relevant rules pinned here are the ones a future refactor is most likely to
/// break silently:
/// <list type="bullet">
/// <item>a project path can never escape the project root;</item>
/// <item>an attachment token can never reach another conversation's folder;</item>
/// <item>only Markdown / plain text is readable — never source code;</item>
/// <item>the persisted message keeps the typed text while the model input carries the document.</item>
/// </list>
/// </summary>
public sealed class AiDocumentAttachmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "doc-attachment-tests",
        Guid.NewGuid().ToString("N"));

    public AiDocumentAttachmentTests()
        => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder must never fail a test run.
        }
    }

    private AiAttachmentStore CreateStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:AttachmentRoot"] = Path.Combine(_root, "attachments")
            })
            .Build();
        return new AiAttachmentStore(configuration: configuration);
    }

    private static Stream Text(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    // ---------------------------------------------------------------- decoding

    [Fact]
    public void DecodeText_strips_a_utf8_bom()
    {
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("# 需求"))
            .ToArray();

        Assert.Equal("# 需求", AiAttachmentStore.DecodeText(bytes));
    }

    [Fact]
    public void DecodeText_reads_legacy_gb18030_chinese_text_instead_of_mangling_it()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("GB18030").GetBytes("工程管理平台需求说明");

        Assert.Equal("工程管理平台需求说明", AiAttachmentStore.DecodeText(bytes));
    }

    [Fact]
    public void LooksBinary_rejects_a_file_that_is_not_text()
    {
        Assert.True(AiAttachmentStore.LooksBinary("\0\0\0binary"));
        Assert.True(AiAttachmentStore.LooksBinary(string.Empty));
        Assert.False(AiAttachmentStore.LooksBinary("# 需求\n\n- 合同\t金额\r\n"));
    }

    // ---------------------------------------------------------------- attachments

    [Fact]
    public async Task Attachment_round_trips_and_reports_its_character_count()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(1, "需求.md", Text("合同管理需求"), 18);

        Assert.EndsWith(".md", saved.Token);
        Assert.Equal("需求.md", saved.Name);
        Assert.Equal(6, saved.Chars);
        Assert.Equal("合同管理需求", await store.ReadAllTextAsync(1, saved.Token));
    }

    [Fact]
    public async Task Attachment_token_is_scoped_to_its_conversation()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(1, "需求.md", Text("秘密"), 6);

        Assert.NotNull(await store.ReadAllTextAsync(1, saved.Token));
        Assert.Null(await store.ReadAllTextAsync(2, saved.Token));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("..\\escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData("not-a-token.md")]
    [InlineData("0123456789abcdef0123456789abcdef.exe")]
    public void Attachment_token_must_match_the_generated_shape(string token)
    {
        var store = CreateStore();
        Assert.Null(store.ResolvePath(1, token));
    }

    [Fact]
    public async Task Attachment_upload_rejects_a_non_text_extension()
    {
        var store = CreateStore();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(1, "app.cs", Text("class A {}"), 11));

        Assert.Contains(".md", error.Message);
    }

    [Fact]
    public async Task Attachment_upload_rejects_an_oversized_file()
    {
        var store = CreateStore();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(1, "big.md", Text("x"), AiAttachmentLimits.MaxFileBytes + 1));

        Assert.Contains("KB", error.Message);
    }

    [Fact]
    public async Task Document_context_wraps_each_attachment_in_a_document_block()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(1, "需求.md", Text("合同管理需求"), 18);

        var context = await store.BuildDocumentContextAsync(1, new[] { saved });

        Assert.Contains("<document name=\"需求.md\"", context);
        Assert.Contains("合同管理需求", context);
        Assert.Contains("</document>", context);
    }

    // ---------------------------------------------------------------- images

    /// <summary>An 8x8 white PNG, the same probe image the capability check uses.</summary>
    private static byte[] SamplePng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAD0lEQVR4nGP4jwMwDC0JALoev0Ewkwr8AAAAAElFTkSuQmCC");

    [Fact]
    public async Task Image_upload_keeps_the_bytes_and_marks_the_attachment_as_an_image()
    {
        var store = CreateStore();
        var bytes = SamplePng();
        var saved = await store.SaveAsync(1, "原型图.png", new MemoryStream(bytes), bytes.Length);

        Assert.EndsWith(".png", saved.Token);
        Assert.True(saved.IsImage);
        Assert.Equal("image/png", saved.MediaType);
        Assert.Equal(0, saved.Chars);
        Assert.Equal(bytes, await store.ReadBytesAsync(1, saved.Token));
    }

    [Fact]
    public async Task Image_upload_rejects_a_file_whose_bytes_are_not_the_declared_format()
    {
        var store = CreateStore();
        var notAPng = Encoding.UTF8.GetBytes("this is plain text, not a png image at all");

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(1, "fake.png", new MemoryStream(notAPng), notAPng.Length));

        Assert.Contains("fake.png", error.Message);
    }

    [Fact]
    public async Task An_image_is_never_inlined_as_document_text()
    {
        var store = CreateStore();
        var bytes = SamplePng();
        var saved = await store.SaveAsync(1, "原型图.png", new MemoryStream(bytes), bytes.Length);

        Assert.Null(await store.ReadAllTextAsync(1, saved.Token));
        Assert.Empty(await store.BuildDocumentContextAsync(1, new[] { saved }));
    }

    [Fact]
    public async Task Image_upload_rejects_an_unsupported_extension_with_a_readable_reason()
    {
        var store = CreateStore();
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(1, "需求.pdf", new MemoryStream(Encoding.UTF8.GetBytes("x")), 1));

        Assert.Contains("暂不支持", error.Message);
        Assert.Contains(".md", error.Message);
        Assert.Contains(".png", error.Message);
    }

    [Fact]
    public void History_turns_image_attachments_into_multimodal_content()
    {
        var messages = new List<AiMessageDto>
        {
            new()
            {
                Role = "user",
                Content = "照着这张图生成",
                Attachments = [new AiAttachmentRef { Token = "bbb.png", Name = "原型图.png", Kind = AiAttachmentRef.ImageKind, MediaType = "image/png", Size = 4096 }],
                Images = [new AiImagePayload { Name = "原型图.png", MediaType = "image/png", Base64 = Convert.ToBase64String(SamplePng()) }]
            }
        };

        var history = AiConversationHistory.BuildBounded(messages);

        Assert.Contains(history[0].Contents, content => content is TextContent);
        Assert.Contains(history[0].Contents, content => content is DataContent data && data.MediaType == "image/png");
        // The trace mentions the image but never its bytes.
        Assert.Contains("原型图.png", history[0].Text);
        Assert.DoesNotContain("token=bbb.png", history[0].Text);
    }

    [Fact]
    public void A_vision_rejection_is_recognised_from_the_provider_message()
    {
        Assert.True(AiRequestFailure.LooksLikeVisionRejection(
            new InvalidOperationException("HTTP 400 invalid_request_error: this model does not support image input")));
        Assert.True(AiRequestFailure.LooksLikeVisionRejection(
            new InvalidOperationException("unsupported content type: image_url")));

        Assert.False(AiRequestFailure.LooksLikeVisionRejection(
            new InvalidOperationException("HTTP 401 Unauthorized: invalid api key")));
        Assert.False(AiRequestFailure.LooksLikeVisionRejection(new TimeoutException("The request timed out")));
        Assert.False(AiRequestFailure.LooksLikeVisionRejection(null));
    }

    // ---------------------------------------------------------------- project files

    [Fact]
    public void Project_path_cannot_escape_the_project_root()
    {
        Assert.False(AiProjectFileReader.TryResolve(_root, "../outside.md", out _, out var traversal));
        Assert.Contains("..", traversal);

        Assert.False(AiProjectFileReader.TryResolve(_root, "D:/elsewhere/readme.md", out _, out var absolute));
        Assert.Contains("相对路径", absolute);
    }

    [Fact]
    public void Project_path_rejects_source_files()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class A {}");

        Assert.False(AiProjectFileReader.TryResolve(_root, "Program.cs", out _, out var error));
        Assert.Contains("Program.cs", error);
    }

    [Fact]
    public void Project_path_resolves_a_markdown_document_inside_the_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "需求.md"), "# 需求");

        Assert.True(AiProjectFileReader.TryResolve(_root, "docs/需求.md", out var fullPath, out _));
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public void Project_path_reports_a_missing_file_with_a_next_step()
    {
        Assert.False(AiProjectFileReader.TryResolve(_root, "missing.md", out _, out var error));
        Assert.Contains("list_project_files", error);
    }

    [Fact]
    public void Listing_skips_build_output_and_non_text_files()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "generated.md"), "noise");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Program.cs"), "class A {}");

        var documents = AiProjectFileReader.EnumerateDocuments(_root);

        Assert.Single(documents);
        Assert.Equal("README.md", documents[0].RelativePath);
    }

    // ---------------------------------------------------------------- paging

    [Fact]
    public void SliceDocument_reports_where_the_next_window_starts()
    {
        var text = new string('a', 5000);

        var first = AiProjectFileManagerProbe.Slice(text, offset: null, maxChars: 1000);
        Assert.Equal(0, first.Offset);
        Assert.Equal(1000, first.Content.Length);
        Assert.Equal(1000, first.NextOffset);

        var last = AiProjectFileManagerProbe.Slice(text, offset: 4000, maxChars: 1000);
        Assert.Equal(1000, last.Content.Length);
        Assert.Null(last.NextOffset);
    }

    [Fact]
    public void SliceDocument_clamps_an_offset_past_the_end()
    {
        var slice = AiProjectFileManagerProbe.Slice("abc", offset: 999, maxChars: 1000);

        Assert.Equal(3, slice.Offset);
        Assert.Empty(slice.Content);
        Assert.Null(slice.NextOffset);
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public void History_keeps_a_one_line_trace_of_attached_documents()
    {
        var messages = new List<AiMessageDto>
        {
            new()
            {
                Role = "user",
                Content = "按这个文档生成",
                Attachments =
                [
                    new AiAttachmentRef { Token = "aaa.md", Name = "需求.md", Chars = 1200 }
                ]
            }
        };

        var history = AiConversationHistory.BuildBounded(messages);

        Assert.Contains("按这个文档生成", history[0].Text);
        Assert.Contains("需求.md", history[0].Text);
        Assert.Contains("token=aaa.md", history[0].Text);
    }

    [Fact]
    public void History_replaces_the_current_turn_with_the_inlined_document_text()
    {
        var messages = new List<AiMessageDto>
        {
            new() { Role = "user", Content = "按这个文档生成", Attachments = [new AiAttachmentRef { Token = "aaa.md", Name = "需求.md", Chars = 1200 }] }
        };

        var history = AiConversationHistory.BuildBounded(
            messages,
            new ChatMessage(ChatRole.User, "按这个文档生成\n\n<document name=\"需求.md\">合同管理需求</document>"));

        Assert.Contains("<document", history[0].Text);
        Assert.DoesNotContain("token=aaa.md", history[0].Text);
    }

    // ---------------------------------------------------------------- tool surface

    [Fact]
    public void Document_tools_are_available_in_requirements_mode()
    {
        Assert.True(AiTurnToolSelector.IsToolAllowedInMode(
            AiAgentModes.Requirements, CodeMasterAgentToolCatalog.ReadProjectFile));
        Assert.True(AiTurnToolSelector.IsToolAllowedInMode(
            AiAgentModes.Requirements, CodeMasterAgentToolCatalog.ListProjectFiles));
        Assert.True(AiTurnToolSelector.IsToolAllowedInMode(
            AiAgentModes.Requirements, CodeMasterAgentToolCatalog.ReadAttachment));
    }

    [Fact]
    public void New_plan_turn_keeps_the_document_tools_but_not_the_proposal_tools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.SubmitBusinessPlan),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.ReadProjectFile),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.ListProjectFiles),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.ReadAttachment),
            AIFunctionFactory.Create(() => "ok", name: CodeMasterAgentToolCatalog.ProposeProjectChangeSet)
        };

        var names = AiTurnToolSelector.SelectForNewPlanRequest(tools, "按项目里的需求.md 开始生成")
            .OfType<AIFunction>()
            .Select(item => item.Name)
            .ToList();

        Assert.Contains(CodeMasterAgentToolCatalog.ReadProjectFile, names);
        Assert.Contains(CodeMasterAgentToolCatalog.ListProjectFiles, names);
        Assert.Contains(CodeMasterAgentToolCatalog.ReadAttachment, names);
        Assert.DoesNotContain(CodeMasterAgentToolCatalog.ProposeProjectChangeSet, names);
    }
}

/// <summary>
/// <see cref="CodeMasterAgentToolFactory.SliceDocument"/> is internal to the tool factory;
/// this shim keeps the paging assertions readable without exposing the whole factory.
/// </summary>
internal static class AiProjectFileManagerProbe
{
    internal static (int Offset, string Content, int? NextOffset) Slice(string text, int? offset, int? maxChars)
        => CodeMasterAgentToolFactory.SliceDocument(text, offset, maxChars);
}
