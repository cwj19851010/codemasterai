using CodeMaster.Core.Enums;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.Application.Dtos.CodeGen;

public sealed class SaveProjectViewDefinitionDto
{
    public long? Id { get; set; }
    public long ProjectId { get; set; }
    public long? ExpectedDefinitionVersion { get; set; }
    public string DefinitionKey { get; set; } = string.Empty;
    public string ViewName { get; set; } = string.Empty;
    public string? SchemaName { get; set; }
    public string? Sql { get; set; }
    public string? KeyColumn { get; set; }
    public ProjectViewOwnership Ownership { get; set; } = ProjectViewOwnership.Managed;
    public bool AllowReplace { get; set; }
    public List<ProjectViewDependencyInputDto> Dependencies { get; set; } = [];
    public List<ProjectViewFieldLineageInputDto> Fields { get; set; } = [];
}

public class ProjectViewDependencyInputDto
{
    public string DependencyKey { get; set; } = string.Empty;
    public ProjectViewDependencyKind Kind { get; set; }
    public string? PhysicalSchema { get; set; }
    public string PhysicalName { get; set; } = string.Empty;
    public string? SourceAlias { get; set; }
    public string? SourceRole { get; set; }
    public long? ModuleEntityId { get; set; }
    public long? SourceViewDefinitionId { get; set; }
}

public class ProjectViewFieldLineageInputDto
{
    public string OutputName { get; set; } = string.Empty;
    public string OutputColumn { get; set; } = string.Empty;
    public string DataType { get; set; } = "string";
    public bool IsNullable { get; set; }
    public int? Ordinal { get; set; }
    public ProjectViewLineageKind LineageKind { get; set; } = ProjectViewLineageKind.Unknown;
    public string? SourceDependencyKey { get; set; }
    public long? SourceEntityId { get; set; }
    public long? SourceFieldId { get; set; }
    public string? Expression { get; set; }
    public string? LineageNotes { get; set; }
}

public sealed class ProjectViewDefinitionDto
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string DefinitionKey { get; set; } = string.Empty;
    public string ViewName { get; set; } = string.Empty;
    public string? SchemaName { get; set; }
    public DatabaseType DatabaseType { get; set; }
    public string? Sql { get; set; }
    public string? SqlHash { get; set; }
    public long DefinitionVersion { get; set; }
    public string DefinitionHash { get; set; } = string.Empty;
    public string? KeyColumn { get; set; }
    public ProjectViewOwnership Ownership { get; set; }
    public ProjectViewDeploymentState DeploymentState { get; set; }
    public string DeclaredSchemaHash { get; set; } = string.Empty;
    public string? ActualSchemaHash { get; set; }
    public string? AppliedDefinitionHash { get; set; }
    public long? ModuleEntityId { get; set; }
    public long? ProjectDataSourceId { get; set; }
    public bool AllowReplace { get; set; }
    public DateTime? LastDeployedAt { get; set; }
    public DateTime? LastInspectedAt { get; set; }
    public string? LastError { get; set; }
    public List<ProjectViewDependencyDto> Dependencies { get; set; } = [];
    public List<ProjectViewFieldLineageDto> Fields { get; set; } = [];
}

public sealed class ProjectViewDependencyDto : ProjectViewDependencyInputDto
{
    public long Id { get; set; }
}

public sealed class ProjectViewFieldLineageDto : ProjectViewFieldLineageInputDto
{
    public long Id { get; set; }
    public long? SourceDependencyId { get; set; }
}

public sealed class DeployProjectViewDefinitionDto
{
    public long DefinitionId { get; set; }
    public long ExpectedDefinitionVersion { get; set; }
    public string ExpectedDefinitionHash { get; set; } = string.Empty;
}

public sealed class ProjectViewDeploymentResultDto
{
    public bool AlreadyApplied { get; set; }
    public ProjectViewDefinitionDto Definition { get; set; } = new();
    public ViewBuilderViewResultDto? DatabaseResult { get; set; }
}

public sealed class ProjectViewSchemaDiffDto
{
    public long DefinitionId { get; set; }
    public string DeclaredSchemaHash { get; set; } = string.Empty;
    public string? ActualSchemaHash { get; set; }
    public bool HasActualSchema { get; set; }
    public bool IsInSync { get; set; }
    public List<string> MissingColumns { get; set; } = [];
    public List<string> ExtraColumns { get; set; } = [];
}
