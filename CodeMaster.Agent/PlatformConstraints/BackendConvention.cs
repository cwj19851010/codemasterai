namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// CodeMaster backend stack conventions. Returned to the model so it does not
/// re-derive the runtime choice every turn.
/// </summary>
public sealed record BackendConvention
{
    public string Runtime { get; init; } = "ASP.NET Core 10.0 Web API";
    public string PrimaryOrm { get; init; } = "SqlSugar (snake_case tables/columns, pluralization)";
    public string MigrationOrm { get; init; } = "EF Core (used ONLY in the Migrator project for schema migrations; never in the main runtime)";
    public string Authentication { get; init; } = "JWT (config section JwtSettings, fallback to Jwt)";
    public string Authorization { get; init; } = "[Permission(\"system:user:list\")] attribute + PermissionPolicyProvider + PermissionAuthorizationHandler";
    public string DynamicApi { get; init; } = "Services annotated with [HttpMethod] are auto-exposed at api/{service}/{method}, lowercase routes, no service suffix";
    public string LongSerialization { get; init; } = "All long values serialized as strings via LongToStringConverter to prevent JavaScript precision loss";
    public string SnowflakeId { get; init; } = "Yitter.IdGenerator configured on startup in Program.cs";
    public string MultiTenancy { get; init; } = "ITenantContext (scoped) + TenantMiddleware extracts tenant from JWT claims or X-Tenant-Id header; TenantEntityBase provides automatic TenantId filtering";
    public string DataPermission { get; init; } = "Entities inherit DataPermissionEntityBase (implements IDept); DataPermissionMiddleware reads user's PostDataScope from JWT claims; admin and scope=3 bypass filtering";
    public string SoftDelete { get; init; } = "ISoftDelete interface triggers an automatic global filter that excludes deleted rows";
    public string SupportedDatabases { get; init; } = "SqlServer (default), MySQL, PostgreSQL, Sqlite, Oracle";
    public string DatabaseConfigKeys { get; init; } = "DbProvider key + ConnectionStrings:DefaultConnection in appsettings.json";

    public string RenderForPrompt() =>
        $"""
        Backend stack (must follow exactly):
        - Runtime: {Runtime}
        - Primary ORM: {PrimaryOrm}
        - Migration ORM: {MigrationOrm}
        - Authentication: {Authentication}
        - Authorization: {Authorization}
        - Dynamic API: {DynamicApi}
        - long serialization: {LongSerialization}
        - Snowflake ID: {SnowflakeId}
        - Multi-tenancy: {MultiTenancy}
        - Data permission: {DataPermission}
        - Soft delete: {SoftDelete}
        - Supported databases: {SupportedDatabases}
        - Database config: {DatabaseConfigKeys}
        - Generated project tables use snake_case names; entity class names stay PascalCase
        - All long values cross the wire as strings to avoid JavaScript precision loss
        """;
}