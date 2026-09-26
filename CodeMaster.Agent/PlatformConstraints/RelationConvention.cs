namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// Conventions for CodeMaster one-to-many and view consumer relations.
/// </summary>
public sealed record RelationConvention
{
    public string StorageTable { get; init; } = "sys_one_to_many_relation";
    public string Cardinality { get; init; } = "One parent entity owns zero or more child entity rows";
    public string BackRefRule { get; init; } = "Child entity stores a foreign key to the parent; parent collects children via a navigation collection";

    public string RenderForPrompt() =>
        $"""
        Relation conventions:
        - One-to-many relations are persisted in `{StorageTable}`
        - {Cardinality}
        - {BackRefRule}
        - View entities can act as parents too; their consumer fields follow the same foreign-key rules
        - Prefer declaring relations in the `EntityFields` stage so the `Relations` stage only validates type finalization
        """;
}