using System.Text.Json;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.McpServer.Services;

namespace CodeMaster.McpServer.Tools;

/// <summary>
/// MCP tools for reading and idempotently saving dictionaries in generated projects.
/// </summary>
public sealed class DictionaryTool
{
    private readonly CodeMasterApiClient _apiClient;
    private readonly ProjectContextResolver _contextResolver;

    public DictionaryTool(CodeMasterApiClient apiClient, ProjectContextResolver contextResolver)
    {
        _apiClient = apiClient;
        _contextResolver = contextResolver;
    }

    public static McpTool QueryDefinition => new()
    {
        Name = "query_dictionaries",
        Description = "Read dictionary types and entries from the generated project's database through the authenticated CodeMaster WebApi.",
        InputType = typeof(QueryDictionariesInput),
        InputSchema = JsonSerializer.SerializeToNode(new
        {
            type = "object",
            properties = new
            {
                projectId = new { oneOf = new object[] { new { type = "integer" }, new { type = "string" } } },
                tenantId = new { oneOf = new object[] { new { type = "integer" }, new { type = "string" } }, defaultValue = "0" },
                dictType = new { type = "string" },
                keyword = new { type = "string" },
                includeItems = new { type = "boolean", defaultValue = true },
                includeDisabled = new { type = "boolean", defaultValue = true },
                workspacePath = new { type = "string" }
            }
        })!
    };

    public static McpTool SaveDefinition => new()
    {
        Name = "save_dictionary",
        Description = "Idempotently create, restore, or update one dictionary and its entries in a generated project's database through the authenticated CodeMaster WebApi. Omitted entries are preserved.",
        InputType = typeof(SaveDictionaryInput),
        InputSchema = JsonSerializer.SerializeToNode(new
        {
            type = "object",
            properties = new
            {
                projectId = new { oneOf = new object[] { new { type = "integer" }, new { type = "string" } } },
                tenantId = new { oneOf = new object[] { new { type = "integer" }, new { type = "string" } }, defaultValue = "0" },
                dictType = new { type = "string" },
                dictName = new { type = "string" },
                langKey = new { type = "string" },
                status = new { type = "integer", description = "0 enabled, 1 disabled." },
                sort = new { type = "integer" },
                remark = new { type = "string" },
                items = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            label = new { type = "string" },
                            value = new { type = "string" },
                            langKey = new { type = "string" },
                            isDefault = new { type = "integer", description = "0 no, 1 yes." },
                            status = new { type = "integer", description = "0 enabled, 1 disabled." },
                            sort = new { type = "integer" },
                            remark = new { type = "string" }
                        },
                        required = new[] { "label", "value" }
                    }
                },
                workspacePath = new { type = "string" }
            },
            required = new[] { "dictType", "dictName" }
        })!
    };

    public async Task<object?> QueryAsync(QueryDictionariesInput input)
    {
        input.ProjectId = await McpProjectContextHelper.ResolveProjectIdAsync(
            _contextResolver,
            input.ProjectId,
            input.WorkspacePath);
        if (input.ProjectId <= 0)
            return new { success = false, message = "projectId is required." };
        if (input.TenantId < 0)
            return new { success = false, message = "tenantId cannot be negative." };

        var query = new List<string>
        {
            $"tenantId={input.TenantId}",
            $"includeItems={input.IncludeItems.ToString().ToLowerInvariant()}",
            $"includeDisabled={input.IncludeDisabled.ToString().ToLowerInvariant()}"
        };
        if (!string.IsNullOrWhiteSpace(input.DictType))
            query.Add("dictType=" + Uri.EscapeDataString(input.DictType.Trim()));
        if (!string.IsNullOrWhiteSpace(input.Keyword))
            query.Add("keyword=" + Uri.EscapeDataString(input.Keyword.Trim()));

        var dictionaries = await _apiClient.GetAsync<List<ProjectDictionaryDto>>(
            $"/api/codegen/project/{input.ProjectId}/dictionaries?{string.Join('&', query)}",
            input.WorkspacePath);
        return new
        {
            success = true,
            projectId = input.ProjectId.ToString(),
            tenantId = input.TenantId.ToString(),
            count = dictionaries.Count,
            dictionaries = dictionaries.Select(MapDictionary).ToList()
        };
    }

    public async Task<object?> SaveAsync(SaveDictionaryInput input)
    {
        input.ProjectId = await McpProjectContextHelper.ResolveProjectIdAsync(
            _contextResolver,
            input.ProjectId,
            input.WorkspacePath);
        if (input.ProjectId <= 0)
            return new { success = false, message = "projectId is required." };

        var result = await _apiClient.PostAsync<SaveProjectDictionaryResultDto>(
            $"/api/codegen/project/{input.ProjectId}/dictionaries",
            new SaveProjectDictionaryDto
            {
                TenantId = input.TenantId,
                DictType = input.DictType,
                DictName = input.DictName,
                LangKey = input.LangKey,
                Status = input.Status,
                Sort = input.Sort,
                Remark = input.Remark,
                Items = input.Items.Select(item => new SaveProjectDictionaryItemDto
                {
                    Label = item.Label,
                    Value = item.Value,
                    LangKey = item.LangKey,
                    IsDefault = item.IsDefault,
                    Status = item.Status,
                    Sort = item.Sort,
                    Remark = item.Remark
                }).ToList()
            },
            input.WorkspacePath);

        return new
        {
            success = true,
            projectId = input.ProjectId.ToString(),
            dictionaryId = result.Id.ToString(),
            tenantId = result.TenantId.ToString(),
            result.DictType,
            result.DictName,
            result.Created,
            result.Restored,
            result.CreatedItemCount,
            result.UpdatedItemCount,
            items = result.Items.Select(item => new
            {
                id = item.Id.ToString(),
                item.Value,
                item.Created,
                item.Restored
            }).ToList()
        };
    }

    private static object MapDictionary(ProjectDictionaryDto dictionary)
    {
        return new
        {
            id = dictionary.Id.ToString(),
            tenantId = dictionary.TenantId.ToString(),
            dictionary.DictType,
            dictionary.DictName,
            dictionary.LangKey,
            dictionary.Status,
            dictionary.Sort,
            dictionary.Remark,
            dictionary.ItemCount,
            items = dictionary.Items?.Select(item => new
            {
                id = item.Id.ToString(),
                tenantId = item.TenantId.ToString(),
                item.DictType,
                item.Label,
                item.Value,
                item.LangKey,
                item.IsDefault,
                item.Status,
                item.Sort,
                item.Remark
            }).ToList()
        };
    }
}

public sealed class QueryDictionariesInput
{
    public long? ProjectId { get; set; }
    public long TenantId { get; set; }
    public string? DictType { get; set; }
    public string? Keyword { get; set; }
    public bool IncludeItems { get; set; } = true;
    public bool IncludeDisabled { get; set; } = true;
    public string? WorkspacePath { get; set; }
}

public sealed class SaveDictionaryInput
{
    public long? ProjectId { get; set; }
    public long TenantId { get; set; }
    public string DictType { get; set; } = string.Empty;
    public string DictName { get; set; } = string.Empty;
    public string? LangKey { get; set; }
    public int? Status { get; set; }
    public int? Sort { get; set; }
    public string? Remark { get; set; }
    public List<DictionaryItemInput> Items { get; set; } = [];
    public string? WorkspacePath { get; set; }
}

public sealed class DictionaryItemInput
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? LangKey { get; set; }
    public int? IsDefault { get; set; }
    public int? Status { get; set; }
    public int? Sort { get; set; }
    public string? Remark { get; set; }
}
