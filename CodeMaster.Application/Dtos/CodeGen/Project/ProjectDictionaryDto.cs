namespace CodeMaster.Application.Dtos.CodeGen;

/// <summary>
/// Query options for dictionaries stored in a generated project's database.
/// </summary>
public sealed class ProjectDictionaryQueryDto
{
    public long TenantId { get; set; }

    public string? DictType { get; set; }

    public string? Keyword { get; set; }

    public bool IncludeItems { get; set; } = true;

    public bool IncludeDisabled { get; set; } = true;
}

/// <summary>
/// A dictionary type and its optional entries from a generated project.
/// </summary>
public sealed class ProjectDictionaryDto
{
    public long Id { get; set; }

    public long TenantId { get; set; }

    public string DictType { get; set; } = string.Empty;

    public string DictName { get; set; } = string.Empty;

    public string? LangKey { get; set; }

    public int Status { get; set; }

    public int Sort { get; set; }

    public string? Remark { get; set; }

    public int ItemCount { get; set; }

    public List<ProjectDictionaryItemDto>? Items { get; set; }
}

public sealed class ProjectDictionaryItemDto
{
    public long Id { get; set; }

    public long TenantId { get; set; }

    public string DictType { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? LangKey { get; set; }

    public int IsDefault { get; set; }

    public int Status { get; set; }

    public int Sort { get; set; }

    public string? Remark { get; set; }
}

/// <summary>
/// Idempotently creates or updates one dictionary in a generated project.
/// Omitted entries are preserved.
/// </summary>
public sealed class SaveProjectDictionaryDto
{
    public long TenantId { get; set; }

    public string DictType { get; set; } = string.Empty;

    public string DictName { get; set; } = string.Empty;

    public string? LangKey { get; set; }

    public int? Status { get; set; }

    public int? Sort { get; set; }

    public string? Remark { get; set; }

    public List<SaveProjectDictionaryItemDto> Items { get; set; } = [];
}

public sealed class SaveProjectDictionaryItemDto
{
    public string Label { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? LangKey { get; set; }

    public int? IsDefault { get; set; }

    public int? Status { get; set; }

    public int? Sort { get; set; }

    public string? Remark { get; set; }
}

public sealed class SaveProjectDictionaryResultDto
{
    public long Id { get; set; }

    public long TenantId { get; set; }

    public string DictType { get; set; } = string.Empty;

    public string DictName { get; set; } = string.Empty;

    public bool Created { get; set; }

    public bool Restored { get; set; }

    public int CreatedItemCount { get; set; }

    public int UpdatedItemCount { get; set; }

    public List<SaveProjectDictionaryItemResultDto> Items { get; set; } = [];
}

public sealed class SaveProjectDictionaryItemResultDto
{
    public long Id { get; set; }

    public string Value { get; set; } = string.Empty;

    public bool Created { get; set; }

    public bool Restored { get; set; }
}
