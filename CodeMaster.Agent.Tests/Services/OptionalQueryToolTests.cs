using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Tools;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tests.Services;

public sealed class OptionalQueryToolTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":{}}")]
    [InlineData("{\"query\":null}")]
    public async Task Dictionary_query_can_be_omitted_even_with_a_required_forwarding_lambda(string json)
    {
        ProjectDictionaryQuery? captured = null;
        var function = CreateTools(query =>
        {
            captured = query;
            return Task.FromResult("dictionaries");
        }).Single(tool => tool.Name == CodeMasterAgentToolCatalog.GetProjectDictionaries);

        Assert.False(function.JsonSchema.TryGetProperty("required", out var required) &&
            required.EnumerateArray().Any(value => value.GetString() == "query"));
        Assert.Equal("dictionaries", (await function.InvokeAsync(Arguments(json)))?.ToString());
        Assert.NotNull(captured);
        Assert.Equal(0, captured.TenantId);
        Assert.Null(captured.DictType);
        Assert.Null(captured.Keyword);
        Assert.True(captured.IncludeItems);
        Assert.True(captured.IncludeDisabled);
    }

    [Fact]
    public async Task Dictionary_query_preserves_explicit_filters()
    {
        ProjectDictionaryQuery? captured = null;
        var function = CreateTools(query => { captured = query; return Task.FromResult("filtered"); })
            .Single(tool => tool.Name == CodeMasterAgentToolCatalog.GetProjectDictionaries);
        await function.InvokeAsync(Arguments("""
            {"query":{"tenantId":42,"dictType":"project_status","keyword":"open","includeItems":false,"includeDisabled":false}}
            """));
        Assert.NotNull(captured);
        Assert.Equal(42, captured.TenantId);
        Assert.Equal("project_status", captured.DictType);
        Assert.Equal("open", captured.Keyword);
        Assert.False(captured.IncludeItems);
        Assert.False(captured.IncludeDisabled);
    }

    [Theory]
    [InlineData("get_entity_blueprint", "query")]
    [InlineData("get_ui_page_blueprint", "query")]
    [InlineData("get_project_work_task", "query")]
    [InlineData("propose_project_change_set", "proposal")]
    public async Task Targeted_queries_and_writes_still_require_their_arguments(string name, string parameter)
    {
        var function = CreateTools(_ => Task.FromResult("read")).Single(tool => tool.Name == name);
        Assert.Contains(function.JsonSchema.GetProperty("required").EnumerateArray(), item => item.GetString() == parameter);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => function.InvokeAsync(Arguments("{}")).AsTask());
        Assert.Contains($"required parameter '{parameter}'", error.Message);
    }

    [Theory]
    [InlineData("{\"query\":[]}")]
    [InlineData("{\"query\":\"invalid\"}")]
    public async Task Malformed_queries_are_not_replaced_with_default_filters(string json)
    {
        var calls = 0;
        var function = CreateTools(_ => { calls++; return Task.FromResult("read"); })
            .Single(tool => tool.Name == CodeMasterAgentToolCatalog.GetProjectDictionaries);
        await Assert.ThrowsAnyAsync<Exception>(() => function.InvokeAsync(Arguments(json)).AsTask());
        Assert.Equal(0, calls);
    }

    private static IEnumerable<AIFunction> CreateTools(Func<ProjectDictionaryQuery?, Task<string>> dictionaries)
    {
        static Task<string> Unexpected() => throw new InvalidOperationException("Targeted handler must not run without arguments.");
        return CodeMasterAgentToolCatalog.Create(
            false, Unexpected, false, Unexpected, false, Unexpected,
            true, query => Unexpected(), true, query => Unexpected(), true, dictionaries,
            true, proposal => Unexpected(), proposal => Unexpected(), false, proposal => Unexpected(),
            includeWorkPlan: true, getProjectWorkPlan: Unexpected,
            getProjectWorkTask: query => Unexpected(), upsertProjectWorkPlan: input => Unexpected())
            .OfType<AIFunction>();
    }

    private static AIFunctionArguments Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));
    }
}
