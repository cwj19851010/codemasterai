using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.Ai;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Dtos;

namespace CodeMaster.UiComposition.Agent;

/// <summary>
/// Bridges the durable Views work-plan stage to the existing SQL-first view builder.
/// The public Agent runtime owns batching and approval; this optional module owns the
/// private view definition contract and therefore supplies the adapter here.
/// </summary>
public sealed class ViewDefinitionBatchOperationAdapter : ICodeMasterAgentBatchOperationAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(AgentJsonSerializer.Options);
    private static readonly Regex SafeIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex PascalIdentifier = new("^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private readonly IProjectViewBuilderService _viewBuilderService;
    private readonly IProjectModuleService _moduleService;
    private readonly IModuleEntityService _entityService;
    private readonly IAiAuthorizationService _authorizationService;
    private readonly IProjectViewDefinitionService _definitionService;

    public ViewDefinitionBatchOperationAdapter(
        IProjectViewBuilderService viewBuilderService,
        IProjectModuleService moduleService,
        IModuleEntityService entityService,
        IAiAuthorizationService authorizationService,
        IProjectViewDefinitionService definitionService)
    {
        _viewBuilderService = viewBuilderService;
        _moduleService = moduleService;
        _entityService = entityService;
        _authorizationService = authorizationService;
        _definitionService = definitionService;
    }

    public string OperationKind => "view_definition";

    public async Task<AiProjectWorkBatchOperationDisclosure> ValidateAsync(
        long projectId,
        AiProjectWorkBatchOperation operation)
    {
        // Disclosure runs while the plan is being approved, which is before any task in the same
        // batch has executed. A view that names a module created by an earlier task therefore has
        // nothing to resolve against yet, so the module lookup is deferred to execution.
        var plan = await ResolveAsync(projectId, Deserialize(operation), requireModule: false);
        var fieldCount = plan.EntityFields.Count > 0
            ? plan.EntityFields.Count
            : (await _viewBuilderService.PreviewSqlAsync(plan.Query)).SelectedFields.Count;
        return new AiProjectWorkBatchOperationDisclosure
        {
            OperationKind = OperationKind,
            Summary = plan.Summary,
            EntityCount = 1,
            FieldCount = fieldCount,
            Warnings = [
                "The view is read-only and is generated separately from normal entity CRUD output.",
                "Declared field mappings are authoritative; database column differences remain warnings until the real view is created."
            ]
        };
    }

    public async Task DemandPermissionAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var plan = Deserialize(operation);
        await _authorizationService.DemandAnyAsync("codegen:projectpage:update");
        await _authorizationService.DemandAnyAsync("codegen:moduleEntity:create");
    }

    /// <summary>
    /// Reads the read-only consumer's fields out of a hand-written SELECT projection.
    /// </summary>
    /// <remarks>
    /// Only the top-level projection is considered. Parenthesis depth is tracked so that a function
    /// argument list stays inside one field: <c>COALESCE(r.TotalReceived, 0) AS TotalIncome</c> yields
    /// a single field named <c>TotalIncome</c> rather than three.
    /// </remarks>
    private static List<ViewEntityFieldDefinitionDto> DeriveFieldsFromManualSql(string? sql, string keyColumn)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return new List<ViewEntityFieldDefinitionDto>();

        var selectAt = IndexOfKeyword(sql!, "select");
        if (selectAt < 0)
            return new List<ViewEntityFieldDefinitionDto>();
        var fromAt = IndexOfKeyword(sql!, "from", selectAt + "select".Length);
        if (fromAt < 0)
            return new List<ViewEntityFieldDefinitionDto>();

        var fields = new List<ViewEntityFieldDefinitionDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projection in SplitTopLevel(sql![(selectAt + "select".Length)..fromAt]))
        {
            var alias = ResolveProjectionAlias(projection);
            if (alias.Length == 0 || !seen.Add(alias))
                continue;

            var isKey = string.Equals(alias, keyColumn, StringComparison.OrdinalIgnoreCase);
            fields.Add(new ViewEntityFieldDefinitionDto
            {
                Name = ToPascalIdentifier(alias),
                DisplayName = alias,
                ColumnName = alias,
                DataType = isKey ? "long" : "string",
                IsNullable = !isKey,
                ShowInList = true,
                ShowInDetail = true,
                ShowInSearch = false,
                IsSortable = true,
                OrderNum = fields.Count + 1
            });
        }

        return fields;
    }

    /// <summary>
    /// Finds a whole-word keyword outside any parentheses, so a subquery never masquerades as the
    /// outer SELECT or FROM.
    /// </summary>
    private static int IndexOfKeyword(string text, string keyword, int startIndex = 0)
    {
        var depth = 0;
        for (var i = startIndex; i <= text.Length - keyword.Length; i++)
        {
            var c = text[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth != 0)
                continue;
            if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
                continue;
            if (string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            var after = i + keyword.Length;
            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
                continue;
            return i;
        }

        return -1;
    }

    /// <summary>
    /// True when the statement combines several row sources with a set operator.
    /// </summary>
    /// <remarks>
    /// A column that is unique inside one branch of a <c>UNION</c> is not unique across the result:
    /// supplier 1 and labor company 1 both project their own <c>Id</c> under the same alias. Reading
    /// the first branch therefore proves nothing about the key of the combined view, so a set
    /// operation has to be treated as "no provable key" rather than as a single-table projection.
    /// </remarks>
    private static bool HasSetOperation(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;
        return IndexOfKeyword(sql!, "union") >= 0 ||
               IndexOfKeyword(sql!, "intersect") >= 0 ||
               IndexOfKeyword(sql!, "except") >= 0;
    }

    /// <summary>Splits a projection on the commas that sit outside every parenthesis.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>Resolves the output name of one projection item: its AS alias, or the trailing column identifier.</summary>
    private static string ResolveProjectionAlias(string projection)
    {
        var item = projection.Trim();
        if (item.Length == 0)
            return string.Empty;

        var asAt = IndexOfKeyword(item, "as");
        if (asAt >= 0)
            return TrimIdentifier(item[(asAt + 2)..]);

        var separator = item.LastIndexOf('.');
        return TrimIdentifier(separator >= 0 ? item[(separator + 1)..] : item);
    }

    /// <summary>Reduces a projection fragment to a bare identifier, tolerating quoting and trailing punctuation.</summary>
    private static string TrimIdentifier(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') || (text[0] == '`' && text[^1] == '`') || (text[0] == '[' && text[^1] == ']')))
            return text[1..^1].Trim();

        var end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;

        return end == 0 ? string.Empty : text[..end];
    }

    /// <summary>
    /// Maps a source column name onto the alias the SQL exposes it under.
    /// </summary>
    /// <remarks>
    /// Returns the key column unchanged when the SQL already projects it under that exact name, the
    /// alias when the projection renames it, and null when the column is not selected at all — in
    /// which case the plan is genuinely unresolvable and the caller must still fail.
    /// </remarks>
    private static string? ResolveKeyColumnAlias(string? sql, string keyColumn)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        var selectAt = IndexOfKeyword(sql!, "select");
        if (selectAt < 0)
            return null;
        var fromAt = IndexOfKeyword(sql!, "from", selectAt + "select".Length);
        if (fromAt < 0)
            return null;

        foreach (var projection in SplitTopLevel(sql![(selectAt + "select".Length)..fromAt]))
        {
            var alias = ResolveProjectionAlias(projection);
            if (alias.Length == 0)
                continue;
            if (string.Equals(alias, keyColumn, StringComparison.OrdinalIgnoreCase))
                return keyColumn;

            var source = ResolveProjectionSourceColumn(projection);
            if (source.Length > 0 && string.Equals(source, keyColumn, StringComparison.OrdinalIgnoreCase))
                return alias;
        }

        return null;
    }

    /// <summary>Reads the column a projection item selects, ignoring any alias it is given.</summary>
    private static string ResolveProjectionSourceColumn(string projection)
    {
        var asAt = IndexOfKeyword(projection, "as");
        var expression = (asAt >= 0 ? projection[..asAt] : projection).TrimEnd();
        // A trailing ')' means the item is a bare function call or parenthesised expression, so it
        // has no source column to speak of.
        if (expression.Length == 0 || expression[^1] == ')')
            return string.Empty;

        var start = expression.Length;
        while (start > 0 && (char.IsLetterOrDigit(expression[start - 1]) || expression[start - 1] == '_'))
            start--;

        return TrimIdentifier(expression[start..]);
    }

    /// <summary>
    /// Loads the project's entities through the same catalog the registration step reads.
    /// </summary>
    /// <remarks>
    /// Registering the read-only consumer matches an existing entity by name across the whole
    /// project, so a module-scoped check here is simply a different question and answers it wrongly.
    /// Collecting entities module by module is no better: a reset soft-deletes modules, which drops
    /// them from the module list while leaving their entity rows alive, so an entity filed under a
    /// reset module is invisible to a module walk but perfectly visible to registration. Reading the
    /// catalog removes the discrepancy instead of narrowing it.
    /// </remarks>
    private async Task<List<ViewBuilderEntityDto>> LoadProjectEntitiesAsync(long projectId)
        => (await _viewBuilderService.GetEntityCatalogAsync(projectId)).Entities;

    /// <summary>
    /// Finds a PascalCase consumer name that no entity in the project uses yet.
    /// </summary>
    private async Task<string> ResolveFreeConsumerNameAsync(long projectId, string entityName)
    {
        var taken = (await LoadProjectEntitiesAsync(projectId))
            .Select(item => item.EntityName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baseName = entityName + "View";
        if (!taken.Contains(baseName))
            return baseName;

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = baseName + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException(
            $"The planned view consumer '{entityName}' collides with an existing entity and every '{baseName}*' fallback name is taken too.");
    }

    public async Task<object> ExecuteAsync(long projectId, AiProjectWorkBatchOperation operation)
    {
        var plan = await ResolveAsync(projectId, Deserialize(operation));
        var persisted = await ViewDefinitionPersistence.SaveDeployAndRegisterAsync(
            _definitionService, _viewBuilderService, projectId, plan);
        var registered = persisted.Registered;
        var result = new ViewAgentExecutionResult
        {
            ProjectId = projectId,
            EntityId = registered.EntityId,
            DataSourceId = registered.DataSourceId,
            ViewName = registered.ViewName,
            Summary = plan.Summary,
            Warnings = persisted.Deployment.DatabaseResult?.Warnings ?? []
        };
        result.Warnings.AddRange(plan.AutomaticRepairs);
        return result;
    }

    private async Task<ViewAgentApprovalPlanDto> ResolveAsync(
        long projectId,
        ViewAgentPlanDto plan,
        bool requireModule = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // Work-plan view tasks prepare the durable definition and read-only
        // consumer first. The physical view is applied later by the single
        // project materialization migration after entity tables are generated.
        plan.DefinitionOnly = true;
        plan.GenerateAfterApproval = false;
        plan.BuildAfterGeneration = false;
        var summary = plan.Summary?.Trim() ?? string.Empty;
        var reason = plan.Reason?.Trim() ?? string.Empty;
        var viewName = plan.ViewName?.Trim() ?? string.Empty;
        var entityName = plan.EntityName?.Trim() ?? string.Empty;
        var keyColumn = plan.KeyColumn?.Trim() ?? string.Empty;
        if (summary.Length == 0)
            throw new ArgumentException("The view plan summary is required.");
        if (reason.Length == 0)
            throw new ArgumentException("The view plan requires a concrete reason.");
        if (!SafeIdentifier.IsMatch(viewName))
            throw new ArgumentException("ViewName must be a safe database identifier.");
        if (!PascalIdentifier.IsMatch(entityName))
            throw new ArgumentException("EntityName must be an ASCII PascalCase identifier.");
        if (keyColumn.Length == 0)
        {
            // A plan that names no key is asking for the platform convention, not for a keyless
            // consumer: a view over one managed entity almost always projects its Id. Resolution
            // below still registers it keyless when the projection turns out to have no Id at all,
            // so defaulting here costs nothing and keeps GetById for the ordinary single-table view.
            keyColumn = "Id";
            plan.AutomaticRepairs.Add("Defaulted the view key column to 'Id': the plan named none.");
        }
        if (plan.Query == null)
            throw new ArgumentException("A view query is required.");
        if (plan.BuildAfterGeneration && !plan.GenerateAfterApproval)
            throw new ArgumentException("BuildAfterGeneration requires GenerateAfterApproval.");
        if (plan.ModuleId <= 0 && !string.IsNullOrWhiteSpace(plan.ModuleName))
        {
            var module = (await _moduleService.GetByProjectIdAsync(projectId))
                .FirstOrDefault(item => string.Equals(item.ModuleName, plan.ModuleName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (module != null)
                plan.ModuleId = module.Id;
        }
        if (plan.ModuleId <= 0)
        {
            if (requireModule)
                throw new ArgumentException("ModuleId or ModuleName is required.");
        }
        else if (!(await _moduleService.GetByProjectIdAsync(projectId)).Any(item => item.Id == plan.ModuleId))
        {
            if (requireModule)
                throw new InvalidOperationException($"Module {plan.ModuleId} does not belong to project {projectId}.");
        }

        var declaredFields = NormalizeFields(plan.EntityFields);

        var existingEntities = await LoadProjectEntitiesAsync(projectId);
        // A read-only entity whose table is exactly this view is the consumer an earlier attempt
        // of the same task already registered — whatever name it ended up with. It has to win over
        // every other match: a writable entity that merely owns the planned consumer name matches
        // the same FirstOrDefault first (entities load in creation order, and the writable entity
        // is created before the view), which throws the plan into the renaming branch below and
        // produces a second consumer that registration then refuses on every retry with
        // "实体 X 或视图 Y 已注册。".
        var registeredConsumer = existingEntities.FirstOrDefault(item =>
            item.IsReadOnly &&
            string.Equals(item.TableName, viewName, StringComparison.OrdinalIgnoreCase));
        var existingConsumer = registeredConsumer ?? existingEntities.FirstOrDefault(item =>
                string.Equals(item.EntityName, entityName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.TableName, viewName, StringComparison.OrdinalIgnoreCase));
        if (existingConsumer != null)
        {
            // Adopt the already-registered consumer under its own name.
            if (existingConsumer.IsReadOnly &&
                string.Equals(existingConsumer.TableName, viewName, StringComparison.OrdinalIgnoreCase))
            {
                plan.ExistingEntityId = existingConsumer.EntityId;
                var reusedName = existingConsumer.EntityName.Trim();
                if (!string.Equals(reusedName, entityName, StringComparison.OrdinalIgnoreCase))
                {
                    plan.EntityName = reusedName;
                    entityName = reusedName;
                }

                plan.AutomaticRepairs.Add(
                    $"Reused the read-only consumer '{reusedName}' already registered for view '{viewName}'.");
            }
            // A read-only entity carrying exactly the planned consumer name is the same view
            // consumer left behind by an earlier attempt, so reuse it instead of failing.
            else if (existingConsumer.IsReadOnly &&
                string.Equals(existingConsumer.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            {
                plan.ExistingEntityId = existingConsumer.EntityId;
            }
            else if (string.Equals(existingConsumer.EntityName, entityName, StringComparison.OrdinalIgnoreCase))
            {
                // The only problem is the name: the plan's read-only consumer is called exactly
                // like an entity that already exists as a writable one. Rejecting the plan costs
                // far more than the collision does — the view stage runs late, so everything queued
                // behind it (pages, field controls, relations, generation) is skipped as well.
                // Appending "View" is the rename the model would have made anyway, so make it here.
                var renamed = await ResolveFreeConsumerNameAsync(projectId, entityName);
                plan.EntityName = renamed;
                // The plan handed back to the caller is built from this local, not from plan.EntityName,
                // so the rename has to be written to both — updating only the plan silently changed
                // nothing and the collision simply resurfaced deeper inside registration.
                entityName = renamed;
                plan.AutomaticRepairs.Add(
                    $"Renamed the read-only view consumer to '{renamed}', because '{existingConsumer.EntityName}' is already a writable entity in this project.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"The planned view consumer '{entityName}' collides with the {(existingConsumer.IsReadOnly ? "read-only" : "writable")} entity '{existingConsumer.EntityName}' in this project. " +
                    $"Rename the read-only consumer (for example '{entityName}View') and resubmit the view plan.");
            }
        }
        else if (plan.ExistingEntityId.HasValue)
        {
            throw new InvalidOperationException($"Existing view entity {plan.ExistingEntityId.Value} was not found with the planned name and table mapping.");
        }

        var existingDefinition = ViewPlanMetadataRules.FindDefinition(
            await _definitionService.GetListAsync(projectId),
            viewName);
        ViewPlanMetadataRules.EnsureCanCreateOrReplace(existingDefinition, plan.ReplaceExistingView);

        // The model names a master table's primary key after the child's foreign key often enough
        // that refusing the plan would simply block this stage forever — the task replays the same
        // definition on every retry. Rewrite the reference from the catalog instead, before the SQL
        // is resolved, derived from, or persisted, so every reader below sees the repaired text.
        var columnRepair = ViewSqlColumnRepair.Repair(plan.Query.Sql, existingEntities);
        if (columnRepair.Repairs.Count > 0)
        {
            plan.Query.Sql = columnRepair.Sql;
            foreach (var repair in columnRepair.Repairs)
                plan.AutomaticRepairs.Add(repair);
        }

        var query = new PreviewViewSqlDto
        {
            ProjectId = projectId,
            Sql = plan.Query.Sql,
            Entities = plan.Query.Entities,
            Joins = plan.Query.Joins,
            IncludeInferredJoins = plan.Query.IncludeInferredJoins
        };
        var preview = await _viewBuilderService.PreviewSqlAsync(query);
        // A key column that is not among the selected aliases used to end the task here. Resolution
        // below already answers that case — follow the SQL alias, or register the consumer keyless —
        // so rejecting it early would only take that decision away from the code that can make it.

        var projection = ResolveProjection(
            query.Sql,
            existingEntities,
            declaredFields,
            keyColumn,
            preview.SelectedFields.Select(item => item.Alias).ToList(),
            plan.AutomaticRepairs);
        declaredFields = projection.Fields;
        keyColumn = projection.KeyColumn;

        var expected = declaredFields.Select(item => item.ColumnName).ToList();
        return new ViewAgentApprovalPlanDto
        {
            Summary = summary,
            Reason = reason,
            Query = query,
            EntityFields = declaredFields,
            ViewName = viewName,
            ReplaceExistingView = plan.ReplaceExistingView,
            ModuleId = plan.ModuleId,
            ExistingEntityId = plan.ExistingEntityId,
            EntityName = entityName,
            DisplayName = string.IsNullOrWhiteSpace(plan.DisplayName) ? entityName : plan.DisplayName.Trim(),
            KeyColumn = keyColumn,
            GenerateFrontend = plan.GenerateFrontend,
            DefinitionOnly = true,
            GenerateAfterApproval = plan.GenerateAfterApproval,
            Incremental = plan.Incremental,
            BuildAfterGeneration = plan.BuildAfterGeneration,
            ResolvedSql = preview.Sql,
            ExpectedColumns = expected
        };
    }

    /// <summary>
    /// Turns a view plan into the field list and key column that registration will consume.
    /// </summary>
    /// <remarks>
    /// Every rejection here is permanent for the task that raised it: the work-plan task stores the
    /// model's plan verbatim and a retry replays that same plan, so a plan the model cannot fix by
    /// resubmitting has to be repaired instead of refused. The key is resolved from the SQL the
    /// model actually wrote, which is the only authority on what the view exposes; when no output
    /// can be proven to be the key, the consumer is registered keyless rather than given a
    /// substitute that would make GetById answer with the wrong row.
    /// </remarks>
    internal static ViewProjectionResolution ResolveProjection(
        string? sql,
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        IReadOnlyList<ViewEntityFieldDefinitionDto> declaredFields,
        string keyColumn,
        IReadOnlyList<string> previewAliases,
        ICollection<string> repairs)
    {
        // A projection frequently renames the key column — "SELECT p.Id AS ProjectId, ...". The view
        // then exposes the alias, not the source column, so a KeyColumn of "Id" cannot match any view
        // column no matter how many times the plan is resubmitted. Follow the SQL's own naming, and
        // do it before the field list is built so the key field gets the integral type the check needs.
        //
        // Only for a single row source: with a set operator the alias is read from the first branch,
        // which says nothing about uniqueness across the combined rows.
        if (!declaredFields.Any(field => string.Equals(field.ColumnName, keyColumn, StringComparison.OrdinalIgnoreCase)) &&
            !HasSetOperation(sql))
        {
            var keyAlias = ResolveKeyColumnAlias(sql, keyColumn);
            if (keyAlias != null && !string.Equals(keyAlias, keyColumn, StringComparison.OrdinalIgnoreCase))
            {
                repairs.Add(
                    $"Keyed the read-only consumer on '{keyAlias}': the view projects '{keyColumn}' under that alias, so '{keyColumn}' cannot appear as a view column.");
                keyColumn = keyAlias;
            }
        }

        var fields = declaredFields.ToList();
        if (fields.Count == 0)
        {
            // PreviewSqlAsync resolves the column list from structured entity selections only; for a
            // hand-written SELECT it deliberately returns nothing (the preview tests pin that). A
            // plan that supplies raw SQL therefore arrives here with no fields at all, and the
            // KeyColumn check below would reject every such plan outright. Read the projection out
            // of the SQL instead — the model has already told us the columns.
            fields = previewAliases.Count > 0
                ? previewAliases.Select((alias, index) => new ViewEntityFieldDefinitionDto
                {
                    Name = ToPascalIdentifier(alias),
                    DisplayName = alias,
                    ColumnName = alias,
                    DataType = string.Equals(alias, keyColumn, StringComparison.OrdinalIgnoreCase) ? "long" : "string",
                    IsNullable = !string.Equals(alias, keyColumn, StringComparison.OrdinalIgnoreCase),
                    ShowInList = true,
                    ShowInDetail = true,
                    ShowInSearch = false,
                    IsSortable = true,
                    OrderNum = index + 1
                }).ToList()
                : DeriveFieldsFromManualSql(sql, keyColumn);
        }

        ApplyProjectionTypes(fields, sql, catalog, keyColumn, repairs);

        var keyField = keyColumn.Length == 0
            ? null
            : fields.FirstOrDefault(item =>
                string.Equals(item.ColumnName, keyColumn, StringComparison.OrdinalIgnoreCase));
        if (keyColumn.Length > 0 && keyField == null)
        {
            // A UNION ALL report view has no Id to project: it concatenates rows from unrelated
            // sources, so the model writes KeyColumn "Id" and a projection that only exposes the
            // branch columns. Refusing costs the whole plan — the Views stage runs early, so every
            // relation, control, page and generation task behind it is skipped — and a resubmission
            // produces the same SQL.
            //
            // Choosing a substitute key instead is worse than refusing: supplier 1 and labor
            // company 1 both project PartyId = 1, so a keyed consumer would publish a GetById that
            // answers with the wrong row. Read-only entities are allowed to have no primary key at
            // all (list and query only, no detail route, HasNoKey in EF), which is exactly what this
            // view is. Register it keyless.
            repairs.Add(
                $"Registered the read-only consumer without a primary key: the projection exposes no '{keyColumn}' column, " +
                "and no other output is proven unique. Keyless read-only consumers expose list and query only.");
            keyColumn = string.Empty;
        }

        if (keyField != null && !IsIntegralType(keyField.DataType))
        {
            // A text key cannot back the generated long Id, and the model has no way to correct a
            // replayed task definition. Register keyless rather than ending the whole plan here.
            repairs.Add(
                $"Registered the read-only consumer without a primary key: '{keyField.ColumnName}' is declared as " +
                $"{keyField.DataType}, but the generated Id must be integral.");
            keyColumn = string.Empty;
        }

        return new ViewProjectionResolution(fields, keyColumn);
    }

    /// <summary>
    /// Retypes view outputs that were declared with the untyped <c>string</c> placeholder while the
    /// SELECT projection proves a different type.
    /// </summary>
    /// <remarks>
    /// Every non-key view column arrives here as <c>string</c> unless the author was explicit: the
    /// model writes it that way, and so do both of this adapter's own projection readers. The type is
    /// not cosmetic — it becomes the generated property type, and the field-control stage is asked to
    /// bind a number control to those columns because they really are numbers. Leaving them as text
    /// therefore does not just produce a text grid: the control binding contradicts the declared type
    /// and validation aborts the whole work plan at that stage.
    ///
    /// An explicitly declared non-string type is never overridden; this only replaces the placeholder.
    /// </remarks>
    private static void ApplyProjectionTypes(
        List<ViewEntityFieldDefinitionDto> fields,
        string? sql,
        IReadOnlyList<ViewBuilderEntityDto> catalog,
        string keyColumn,
        ICollection<string> repairs)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return;

        foreach (var field in fields)
        {
            if (string.Equals(field.ColumnName, keyColumn, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(CSharpDataTypeNormalizer.Normalize(field.DataType), "string", StringComparison.OrdinalIgnoreCase))
                continue;

            var inferred = ViewProjectionTypeInference.Infer(sql, catalog, field.ColumnName);
            if (inferred == null || string.Equals(inferred, "string", StringComparison.OrdinalIgnoreCase))
                continue;

            repairs.Add(
                $"Typed view column '{field.ColumnName}' as {inferred}: the projection computes a {inferred}, " +
                $"so a declared string would disagree with the field control the later stages bind to it.");
            field.DataType = inferred;
        }
    }

    private static ViewAgentPlanDto Deserialize(AiProjectWorkBatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.DefinitionJson))
            throw new InvalidOperationException($"Task '{operation.TaskKey}' has no view-definition payload.");
        return JsonSerializer.Deserialize<ViewAgentPlanDto>(operation.DefinitionJson, JsonOptions)
            ?? throw new InvalidOperationException($"Task '{operation.TaskKey}' has an invalid view-definition payload.");
    }

    private static List<ViewEntityFieldDefinitionDto> NormalizeFields(IEnumerable<ViewEntityFieldDefinitionDto>? fields)
    {
        var result = (fields ?? []).Select((field, index) =>
        {
            var name = field.Name?.Trim() ?? string.Empty;
            if (!PascalIdentifier.IsMatch(name))
                throw new ArgumentException($"Entity field name '{name}' must be an ASCII PascalCase identifier.");
            var columnName = string.IsNullOrWhiteSpace(field.ColumnName) ? name : field.ColumnName.Trim();
            var dataType = CSharpDataTypeNormalizer.Normalize(field.DataType);
            return new ViewEntityFieldDefinitionDto
            {
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(field.DisplayName) ? name : field.DisplayName.Trim(),
                ColumnName = columnName,
                DataType = dataType,
                IsNullable = field.IsNullable,
                MaxLength = field.MaxLength,
                Precision = field.Precision,
                Scale = field.Scale,
                ShowInList = field.ShowInList,
                ShowInDetail = field.ShowInDetail,
                ShowInSearch = field.ShowInSearch,
                IsSortable = field.IsSortable,
                FormControlType = string.IsNullOrWhiteSpace(field.FormControlType) ? null : field.FormControlType.Trim(),
                OrderNum = field.OrderNum == 0 ? index : field.OrderNum
            };
        }).ToList();
        var duplicateName = result.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateName != null) throw new ArgumentException($"Duplicate entity field name: {duplicateName.Key}.");
        var duplicateColumn = result.GroupBy(item => item.ColumnName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateColumn != null) throw new ArgumentException($"Duplicate SQL output column mapping: {duplicateColumn.Key}.");
        return result;
    }

    private static string ToPascalIdentifier(string value)
    {
        var parts = Regex.Split(value.Trim(), "[^A-Za-z0-9]+")
            .Where(item => item.Length > 0)
            .Select(item => char.ToUpperInvariant(item[0]) + (item.Length > 1 ? item[1..] : string.Empty));
        var result = string.Concat(parts);
        return PascalIdentifier.IsMatch(result) ? result : "Column";
    }

    private static bool IsIntegralType(string? value) => (value ?? string.Empty).Trim().TrimEnd('?').ToLowerInvariant() is
        "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "int16" or "int32" or "int64" or "integer" or "smallint" or "bigint" or "tinyint";
}

/// <summary>The field list and key column a view plan resolves to before registration.</summary>
internal sealed record ViewProjectionResolution(
    List<ViewEntityFieldDefinitionDto> Fields,
    string KeyColumn);

