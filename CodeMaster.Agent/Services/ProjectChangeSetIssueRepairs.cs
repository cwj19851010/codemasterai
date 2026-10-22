using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Deterministic fix-ups applied to a model-authored change set before it is validated or executed.
/// </summary>
/// <remarks>
/// Every repair here rewrites something the model got structurally wrong but whose correct shape is
/// unambiguous, so fixing it beats failing the task. Both entry points into a change set — the
/// standalone approval tool and the work-batch adapter — must run these, otherwise a repair only
/// half-exists: the Agent's planned tasks go through the batch adapter, so a repair wired only into
/// the tool never runs during a plan at all.
/// </remarks>
internal static class ProjectChangeSetIssueRepairs
{
    /// <summary>
    /// Turns a module the model addressed only by name into a real create or update.
    /// <para>
    /// Models routinely describe a module that does not exist yet as a
    /// <c>moduleUpdates</c> entry with <c>moduleId: 0</c>, then reference it by name from every
    /// entity. The rules can only accept a module update that targets a real id, and an entity may
    /// only reference an existing module or one proposed in <c>modules</c>, so the whole change set
    /// dies with "Module update target '0' does not exist" plus one "must reference an existing or
    /// proposed module" per entity. The intent is unambiguous — the name either belongs to an
    /// existing module or has to be created — so resolve it instead of failing.
    /// </para>
    /// </summary>
    public static List<string> ResolveIdlessModuleUpdates(
        ProjectChangeSetProposal proposal,
        IEnumerable<AiModuleBlueprintDto> existingModules)
    {
        var repairs = new List<string>();
        if (proposal.ModuleUpdates.Count == 0)
            return repairs;

        var existingByName = existingModules
            .GroupBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var update in proposal.ModuleUpdates.Where(item => item.ModuleId <= 0).ToList())
        {
            var name = update.ModuleName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                proposal.ModuleUpdates.Remove(update);
                repairs.Add("Dropped a module update that named neither a module id nor a module name.");
                continue;
            }

            if (existingByName.TryGetValue(name, out var existing))
            {
                update.ModuleId = existing.Id;
                repairs.Add($"Resolved the module update '{name}' to existing module id {existing.Id}.");
                continue;
            }

            proposal.ModuleUpdates.Remove(update);
            var sameNameModule = proposal.Modules.FirstOrDefault(module =>
                string.Equals(module.ModuleName.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (sameNameModule != null)
            {
                // Keep the title the update carried: the create entry may name the module only.
                if (string.IsNullOrWhiteSpace(sameNameModule.ModuleDescription) &&
                    !string.IsNullOrWhiteSpace(update.ModuleDescription))
                {
                    sameNameModule.ModuleDescription = update.ModuleDescription!.Trim();
                }

                repairs.Add($"Merged the module update '{name}' into the new module of the same name.");
                continue;
            }

            proposal.Modules.Add(new CreateModuleProposal
            {
                ModuleName = name,
                // A module without a title cannot be created; the technical name is the only
                // remaining human-readable label, and the user can rename it later.
                ModuleDescription = string.IsNullOrWhiteSpace(update.ModuleDescription)
                    ? name
                    : update.ModuleDescription!.Trim(),
                Icon = update.Icon,
                OrderNum = update.OrderNum ?? 0,
                RoutePath = update.RoutePath,
                Remark = update.Remark
            });
            repairs.Add($"Created module '{name}' because the change set addressed it with module id 0.");
        }

        return repairs;
    }

    /// <summary>
    /// Assigns the only possible module to entities that name none.
    /// <para>
    /// When the change set leaves <c>moduleId</c> at 0 and <c>moduleName</c> empty, the entity is
    /// rejected with "must reference an existing or proposed module". If the project plus the
    /// change set together expose exactly one module, that module is the only possible answer and
    /// the assignment cannot be wrong; with more than one candidate the ambiguity is left alone so
    /// the model still has to state its intent.
    /// </para>
    /// </summary>
    public static List<string> AssignSingleCandidateModule(
        ProjectChangeSetProposal proposal,
        IEnumerable<AiModuleBlueprintDto> existingModules)
    {
        var repairs = new List<string>();
        var candidates = existingModules
            .Select(module => (module.Id, Name: module.ModuleName))
            .Concat(proposal.Modules.Select(module => (0L, Name: module.ModuleName)))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name))
            .GroupBy(candidate => candidate.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (candidates.Count != 1)
            return repairs;

        var (moduleId, moduleName) = candidates[0];
        foreach (var entity in proposal.Entities.Where(item => item.ModuleId <= 0 && string.IsNullOrWhiteSpace(item.ModuleName)))
        {
            entity.ModuleId = moduleId;
            entity.ModuleName = moduleName.Trim();
            repairs.Add($"Assigned entity '{entity.Name}' to the only available module '{moduleName.Trim()}'.");
        }

        return repairs;
    }

    /// <summary>
    /// Forces the ownership and delete behaviour a generated one-to-many aggregate always requires.
    /// The rules only accept a child-table relation when it is <c>Owned</c> and cascades with
    /// <c>Delete</c>, because generated aggregates own their child rows. Models regularly emit
    /// <c>Reference</c>/<c>Restrict</c> for the same intent, which fails the whole relations stage
    /// with "must use Owned ownership".
    /// </summary>
    public static List<string> NormalizeOneToManyRelations(ProjectChangeSetProposal proposal)
    {
        var repairs = new List<string>();

        foreach (var entity in proposal.Entities)
            repairs.AddRange(Normalize(entity.Name, entity.Relations));
        foreach (var update in proposal.EntityUpdates)
        {
            var label = string.IsNullOrWhiteSpace(update.Name)
                ? update.EntityId.ToString()
                : update.Name!.Trim();
            repairs.AddRange(Normalize(label, update.NewRelations));
        }

        return repairs;

        static List<string> Normalize(string entityName, List<CreateEntityRelationProposal> relations)
        {
            var fixes = new List<string>();
            foreach (var relation in relations)
            {
                if (!string.Equals(relation.Cardinality, "OneToMany", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(relation.Ownership, "Owned", StringComparison.OrdinalIgnoreCase))
                {
                    relation.Ownership = "Owned";
                    fixes.Add($"Set ownership of '{entityName}.{relation.RelationName}' to Owned so it can be generated as a child-table relation.");
                }

                if (!string.Equals(relation.DeleteBehavior, "Delete", StringComparison.OrdinalIgnoreCase))
                {
                    relation.DeleteBehavior = "Delete";
                    fixes.Add($"Set delete behavior of '{entityName}.{relation.RelationName}' to Delete because generated one-to-many aggregates own their child rows.");
                }

                if (string.IsNullOrWhiteSpace(relation.SourceField))
                {
                    relation.SourceField = "Id";
                    fixes.Add($"Defaulted the source field of '{entityName}.{relation.RelationName}' to Id.");
                }
            }

            return fixes;
        }
    }

    /// <summary>
    /// Drops related-entity metadata the change set cannot resolve yet.
    /// A plan is split into per-module entity tasks, so a field or relation frequently points at an
    /// entity a later task owns. Creating that field aborts the whole task with "Related entity 'X'
    /// does not exist." and blocks every downstream stage. Relations are a dedicated stage of the
    /// plan, so dropping the unresolved link costs nothing: the column keeps its own shape and the
    /// relation stage re-establishes the link once both ends exist.
    /// </summary>
    public static List<string> DeferUnresolvedRelatedTargets(
        ProjectChangeSetProposal proposal,
        IEnumerable<string> existingEntityNames)
    {
        var hasFieldLinks = proposal.Entities.Any(entity => entity.Fields.Any(field =>
                                !string.IsNullOrWhiteSpace(field.RelatedEntityName))) ||
                            proposal.EntityUpdates.Any(update => update.NewFields.Any(field =>
                                !string.IsNullOrWhiteSpace(field.RelatedEntityName)));
        var hasRelations = proposal.Entities.Any(entity => entity.Relations.Count > 0) ||
                           proposal.EntityUpdates.Any(update => update.NewRelations.Count > 0);
        if (!hasFieldLinks && !hasRelations)
            return new List<string>();

        var known = new HashSet<string>(existingEntityNames, StringComparer.OrdinalIgnoreCase);
        foreach (var update in proposal.EntityUpdates.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
            known.Add(update.Name!.Trim());

        // Relations are applied after every entity of the change set has been created, so they may
        // point at any entity the change set itself creates. Fields are bound while their entity is
        // being created, so they may only point at what already exists: entities of this change set
        // that come later in the creation order are not in the database yet and fail with
        // "Related entity 'X' does not exist.".
        var allProposed = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (var entity in proposal.Entities)
            allProposed.Add(entity.Name.Trim());

        var availableNow = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var repairs = new List<string>();

        foreach (var entity in proposal.Entities)
        {
            repairs.AddRange(DeferFieldLinks(entity.Name, entity.Fields, availableNow));
            repairs.AddRange(DeferRelations(entity.Name, entity.Relations, allProposed));
            availableNow.Add(entity.Name.Trim());
        }
        foreach (var update in proposal.EntityUpdates)
        {
            var label = string.IsNullOrWhiteSpace(update.Name)
                ? update.EntityId.ToString()
                : update.Name!.Trim();
            repairs.AddRange(DeferFieldLinks(label, update.NewFields, known));
            repairs.AddRange(DeferRelations(label, update.NewRelations, allProposed));
        }

        return repairs;
    }

    private static List<string> DeferFieldLinks(
        string entityName,
        List<CreateEntityFieldProposal> fields,
        IReadOnlySet<string> knownEntityNames)
    {
        var repairs = new List<string>();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.RelatedEntityName))
                continue;
            if (knownEntityNames.Contains(field.RelatedEntityName.Trim()))
                continue;

            var target = field.RelatedEntityName.Trim();
            field.RelatedEntityName = null;
            field.RelatedEntityIdField = null;
            field.RelatedEntityDisplayFields = null;
            // A plain select is not a safe landing spot: with SelectDataSource "api" it still
            // validates the related entity, and without it the rules demand static option data.
            // Fall all the way back to an input, which carries no such requirement — the dedicated
            // field-control stage of the plan picks the real control once both ends exist.
            field.SelectDataSource = null;
            field.SelectOptions = null;
            field.IsMultiple = false;
            field.FormControlType = "input";

            repairs.Add(
                $"Deferred the '{field.Name}' link to '{target}' on {entityName}: '{target}' is not available yet, so the control fell back to a plain input until the field-control stage restores it.");
        }

        return repairs;
    }

    private static List<string> DeferRelations(
        string entityName,
        List<CreateEntityRelationProposal> relations,
        IReadOnlySet<string> knownEntityNames)
    {
        var repairs = new List<string>();
        var removed = relations.RemoveAll(relation =>
            relation.TargetEntityId <= 0 &&
            !string.IsNullOrWhiteSpace(relation.TargetEntityName) &&
            !knownEntityNames.Contains(relation.TargetEntityName!.Trim()));
        if (removed > 0)
        {
            repairs.Add(
                $"Deferred {removed} relation(s) on {entityName} whose target entity is created by another task; the relation stage re-creates them.");
        }

        return repairs;
    }
}
