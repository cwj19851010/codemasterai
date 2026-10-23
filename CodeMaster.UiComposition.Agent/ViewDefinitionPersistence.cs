using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Enums;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Dtos;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.UiComposition.Agent;

/// <summary>
/// Persists the durable view definition before registering its generated read-only
/// consumer. Keeping this in the approval execution path makes SQL and lineage
/// metadata survive independently of generated source files.
/// </summary>
internal static class ViewDefinitionPersistence
{
    private static readonly Regex FromOrJoin = new(
        @"\b(?:from|join)\s+([A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CteName = new(
        @"\bwith\s+([A-Za-z_][A-Za-z0-9_]*)\s+as\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<ViewDefinitionPersistenceResult> SaveDeployAndRegisterAsync(
        IProjectViewDefinitionService definitionService,
        IProjectViewBuilderService viewBuilderService,
        long projectId,
        ViewAgentApprovalPlanDto plan)
    {
        var definitionInput = await BuildDefinitionAsync(viewBuilderService, definitionService, projectId, plan);
        var saved = await definitionService.SaveAsync(definitionInput);
        var deployed = plan.DefinitionOnly
            ? new ProjectViewDeploymentResultDto { Definition = saved }
            : await definitionService.DeployAsync(new DeployProjectViewDefinitionDto
            {
                DefinitionId = saved.Id,
                ExpectedDefinitionVersion = saved.DefinitionVersion,
                ExpectedDefinitionHash = saved.DefinitionHash
            });
        var registered = await viewBuilderService.RegisterReadOnlyViewEntityAsync(new RegisterReadOnlyViewEntityDto
        {
            ViewDefinitionId = deployed.Definition.Id,
            ExistingEntityId = plan.ExistingEntityId,
            ProjectId = projectId,
            ModuleId = plan.ModuleId,
            ViewName = plan.ViewName,
            EntityName = plan.EntityName,
            DisplayName = plan.DisplayName,
            KeyColumn = plan.KeyColumn,
            Fields = plan.EntityFields,
            GenerateFrontend = plan.GenerateFrontend,
            AllowUnappliedDefinition = plan.DefinitionOnly,
            DataSourceType = PageDataSourceType.View
        });

        return new ViewDefinitionPersistenceResult(deployed, registered);
    }

    private static async Task<SaveProjectViewDefinitionDto> BuildDefinitionAsync(
        IProjectViewBuilderService viewBuilderService,
        IProjectViewDefinitionService definitionService,
        long projectId,
        ViewAgentApprovalPlanDto plan)
    {
        var catalog = await viewBuilderService.GetEntityCatalogAsync(projectId);
        var selectedEntityIds = plan.Query.Entities
            .Select(item => item.EntityId)
            .Where(item => item > 0)
            .Distinct()
            .ToHashSet();
        var sources = catalog.Entities
            .Where(item => selectedEntityIds.Contains(item.EntityId))
            .ToList();

        // For hand-written SQL, retain participating tables even when the model
        // did not provide structured entity selections. Known tables are linked
        // to CodeMaster entities; unknown names remain explicit external sources.
        if (sources.Count == 0 && !string.IsNullOrWhiteSpace(plan.ResolvedSql))
        {
            var ctes = CteName.Matches(plan.ResolvedSql)
                .Select(item => item.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in FromOrJoin.Matches(plan.ResolvedSql))
            {
                var physicalName = match.Groups[1].Value.Replace(" ", string.Empty);
                if (ctes.Contains(physicalName)) continue;
                var known = catalog.Entities.FirstOrDefault(item =>
                    string.Equals(item.TableName, physicalName, StringComparison.OrdinalIgnoreCase));
                if (known != null && sources.All(item => item.EntityId != known.EntityId))
                    sources.Add(known);
            }
        }

        var dependencies = new List<ProjectViewDependencyInputDto>();
        var dependencyByEntity = new Dictionary<long, string>();
        foreach (var source in sources)
        {
            var dependencyKey = $"entity_{source.EntityId}";
            dependencyByEntity[source.EntityId] = dependencyKey;
            dependencies.Add(new ProjectViewDependencyInputDto
            {
                DependencyKey = dependencyKey,
                Kind = ProjectViewDependencyKind.ManagedEntity,
                PhysicalName = source.TableName,
                SourceRole = "view-source",
                ModuleEntityId = source.EntityId
            });
        }

        if (!string.IsNullOrWhiteSpace(plan.ResolvedSql))
        {
            var ctes = CteName.Matches(plan.ResolvedSql)
                .Select(item => item.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in FromOrJoin.Matches(plan.ResolvedSql))
            {
                var physicalName = match.Groups[1].Value.Replace(" ", string.Empty);
                if (ctes.Contains(physicalName) || dependencies.Any(item =>
                        string.Equals(item.PhysicalName, physicalName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                dependencies.Add(new ProjectViewDependencyInputDto
                {
                    DependencyKey = $"external_{dependencies.Count + 1}",
                    Kind = ProjectViewDependencyKind.ExternalTable,
                    PhysicalName = physicalName,
                    SourceRole = "view-source"
                });
            }
        }

        var fields = new List<ProjectViewFieldLineageInputDto>();
        foreach (var (field, index) in plan.EntityFields.Select((item, index) => (item, index)))
        {
            var matches = sources
                .SelectMany(entity => entity.Fields
                    .Where(candidate =>
                        string.Equals(candidate.ColumnName, field.ColumnName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.Name, field.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(candidate => (Entity: entity, Field: candidate)))
                .ToList();
            var direct = matches.Count == 1 ? matches[0] : default;
            var isDirect = matches.Count == 1;
            fields.Add(new ProjectViewFieldLineageInputDto
            {
                OutputName = field.Name,
                OutputColumn = field.ColumnName,
                DataType = field.DataType,
                IsNullable = field.IsNullable,
                Ordinal = field.OrderNum == 0 ? index : field.OrderNum,
                LineageKind = isDirect ? ProjectViewLineageKind.Direct : ProjectViewLineageKind.Computed,
                SourceDependencyKey = isDirect && dependencyByEntity.TryGetValue(direct.Entity.EntityId, out var key) ? key : null,
                SourceEntityId = isDirect ? direct.Entity.EntityId : null,
                SourceFieldId = isDirect ? direct.Field.FieldId : null,
                Expression = isDirect ? null : field.ColumnName,
                LineageNotes = isDirect ? null : "Declared output has no unique source field in the CodeMaster catalog; treated as a computed or external expression."
            });
        }

        var existing = (await definitionService.GetListAsync(projectId))
            .FirstOrDefault(item => string.Equals(item.ViewName, plan.ViewName, StringComparison.OrdinalIgnoreCase));
        return new SaveProjectViewDefinitionDto
        {
            Id = existing?.Id,
            ExpectedDefinitionVersion = existing?.DefinitionVersion,
            ProjectId = projectId,
            DefinitionKey = plan.ViewName,
            ViewName = plan.ViewName,
            Sql = plan.ResolvedSql,
            KeyColumn = plan.KeyColumn,
            Ownership = ProjectViewOwnership.Managed,
            AllowReplace = plan.ReplaceExistingView,
            Dependencies = dependencies,
            Fields = fields
        };
    }
}

internal sealed record ViewDefinitionPersistenceResult(
    ProjectViewDeploymentResultDto Deployment,
    ReadOnlyViewEntityResultDto Registered);
