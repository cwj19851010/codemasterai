using System.Reflection;
using System.Text.Json;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Agent.Tools;
using CodeMaster.UiComposition.Dtos;
using Microsoft.Extensions.AI;

namespace CodeMaster.UiComposition.Tests;

public sealed class OptionalContextQueryTests
{
    private const long ProjectId = 2100250881390940160;

    [Theory]
    [InlineData("{}", null)]
    [InlineData("{\"query\":{}}", null)]
    [InlineData("{\"query\":null}", null)]
    [InlineData("{\"query\":{\"pageId\":123}}", 123L)]
    public async Task Complex_page_context_accepts_optional_query_and_preserves_page_selection(string json, long? expectedPage)
    {
        var calls = 0;
        var planning = Stub<IComplexPageAgentPlanningService>((method, args) =>
        {
            Assert.Equal("GetContextAsync", method.Name);
            Assert.Equal(ProjectId, args![0]);
            Assert.Equal(expectedPage, args[1]);
            calls++;
            return Task.FromResult(new ComplexPageAgentContextDto { ProjectId = ProjectId });
        });
        var tools = await ToolsAsync(planning: planning);
        var function = tools.Single(tool => tool.Name == UiCompositionAgentToolExtension.GetContextTool);
        AssertQueryOptional(function);
        await function.InvokeAsync(Arguments(json));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{}", true, true, true)]
    [InlineData("{\"query\":{}}", true, true, true)]
    [InlineData("{\"query\":null}", true, true, true)]
    [InlineData("{\"query\":{\"includeDataSources\":false,\"includeOpenApiDocuments\":false,\"includeComplexPages\":false}}", false, false, false)]
    [InlineData("{\"query\":{\"includeOpenApiDocuments\":false}}", true, false, true)]
    public async Task Composition_inventory_applies_defaults_and_explicit_flags(string json, bool sources, bool documents, bool pages)
    {
        var sourceCalls = 0;
        var documentCalls = 0;
        var pageCalls = 0;
        var dataService = Stub<IProjectDataSourceService>((method, args) =>
        {
            Assert.Equal("GetListAsync", method.Name);
            Assert.Equal(ProjectId, Assert.IsType<DataSourceQueryDto>(args![0]).ProjectId);
            sourceCalls++;
            return Task.FromResult(new List<ProjectDataSourceDto>());
        });
        var importService = Stub<IOpenApiImportService>((method, args) =>
        {
            Assert.Equal("GetDocumentsAsync", method.Name);
            Assert.Equal(ProjectId, args![0]);
            documentCalls++;
            return Task.FromResult(new List<OpenApiDocumentDto>());
        });
        var pageService = Stub<IProjectPageService>((method, args) =>
        {
            Assert.Equal("GetListAsync", method.Name);
            Assert.Equal(ProjectId, Assert.IsType<ProjectPageQueryDto>(args![0]).ProjectId);
            pageCalls++;
            return Task.FromResult(new List<ProjectPageDto>());
        });
        var function = (await ToolsAsync(pages: pageService, sources: dataService, imports: importService))
            .Single(tool => tool.Name == UiCompositionAgentToolExtension.GetUiCompositionContextTool);
        AssertQueryOptional(function);
        await function.InvokeAsync(Arguments(json));
        Assert.Equal(sources ? 1 : 0, sourceCalls);
        Assert.Equal(documents ? 1 : 0, documentCalls);
        Assert.Equal(pages ? 1 : 0, pageCalls);
    }

    [Fact]
    public async Task Read_permission_still_controls_tool_availability()
    {
        Assert.Empty(await ToolsAsync(allowRead: false));
    }

    [Theory]
    [InlineData("get_complex_page_context")]
    [InlineData("get_ui_composition_context")]
    public async Task Invalid_query_type_is_rejected_before_calling_services(string name)
    {
        var function = (await ToolsAsync()).Single(tool => tool.Name == name);
        await Assert.ThrowsAnyAsync<Exception>(() => function.InvokeAsync(Arguments("{\"query\":[]}")).AsTask());
    }

    private static async Task<List<AIFunction>> ToolsAsync(
        IComplexPageAgentPlanningService? planning = null, IProjectPageService? pages = null,
        IProjectDataSourceService? sources = null, IOpenApiImportService? imports = null, bool allowRead = true)
    {
        var authorization = Stub<IAiAuthorizationService>((method, args) =>
        {
            Assert.Equal("HasAnyAsync", method.Name);
            var permissions = Assert.IsType<string[]>(args![0]);
            return Task.FromResult(allowRead && permissions.Contains("codegen:projectpage:view"));
        });
        var extension = new UiCompositionAgentToolExtension(
            planning ?? Stub<IComplexPageAgentPlanningService>(), pages ?? Stub<IProjectPageService>(),
            sources ?? Stub<IProjectDataSourceService>(), Stub<IProjectViewBuilderService>(),
            imports ?? Stub<IOpenApiImportService>(), Stub<IProjectModuleService>(), Stub<IModuleEntityService>(),
            authorization, Stub<IProjectViewDefinitionService>());
        return (await extension.CreateToolsAsync(new CodeMasterAgentExtensionContext
        {
            ProjectId = ProjectId, ConversationId = 1, RequestId = "optional-query-contract-test",
            CreatePendingApprovalAsync = (_, _) => throw new InvalidOperationException("Read must not create approvals.")
        })).OfType<AIFunction>().ToList();
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var service = DispatchProxy.Create<T, ReadServiceProxy>();
        ((ReadServiceProxy)(object)service).Handler = handler;
        return service;
    }

    public class ReadServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler?.Invoke(targetMethod!, args) ?? throw new InvalidOperationException($"Unexpected service call: {targetMethod?.Name}");
    }

    private static void AssertQueryOptional(AIFunction function) =>
        Assert.False(function.JsonSchema.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(item => item.GetString() == "query"));

    private static AIFunctionArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));
    }
}
