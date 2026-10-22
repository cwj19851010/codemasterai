using System.Text.Json;
using CodeMaster.Agent.Contracts;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Streaming progress event emitted while compiling a BusinessPlanDefinition into a standard
/// six-stage executable plan. The chat stream surfaces these to the client so the UI can
/// show "recognising modules...", "creating entities...", "compiling tasks..." incrementally
/// instead of waiting for the entire plan to land at once.
/// </summary>
public sealed class BusinessPlanCompileEvent
{
    public string Phase { get; }
    public string Stage { get; }
    public int? Percent { get; }
    public string Message { get; }
    public int? AddedEntities { get; }
    public int? AddedViews { get; }
    public int? TotalTasks { get; }

    public BusinessPlanCompileEvent(
        string phase,
        string stage,
        int? percent,
        string message,
        int? addedEntities = null,
        int? addedViews = null,
        int? totalTasks = null)
    {
        Phase = phase;
        Stage = stage;
        Percent = percent;
        Message = message;
        AddedEntities = addedEntities;
        AddedViews = addedViews;
        TotalTasks = totalTasks;
    }
}

public sealed class BusinessPlanCompileResult
{
    public required BusinessPlanDefinition Definition { get; init; }
    public required UpsertAiProjectWorkPlanRequest CompiledPlan { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required BusinessCompileReport Report { get; init; }
}

public sealed class BusinessCompileReport
{
    public int Modules { get; init; }
    public int Entities { get; init; }
    public int Relations { get; init; }
    public int Views { get; init; }
    public int Pages { get; init; }
    public int Tasks { get; init; }
}

/// <summary>
/// Compiles a BusinessPlanDefinition into a standard UpsertAiProjectWorkPlanRequest.
/// The compiler is the single source of truth for translating business intent into
/// CodeMaster metadata + the six-stage task plan. Replacing the model-direct
/// upsert_project_work_plan path with this layer means we no longer depend on the
/// chat model producing correctly-shaped 22-task plans; the model only emits
/// business intent and the server handles execution details deterministically.
/// </summary>
public static class BusinessPlanCompiler
{
    public static async Task<BusinessPlanCompileResult> CompileAsync(
        BusinessPlanDefinition definition,
        long projectId,
        long conversationId,
        ICodeMasterProjectBlueprintService blueprintService,
        Func<BusinessPlanCompileEvent, Task>? progressAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var warnings = new List<string>();
        var blueprint = await blueprintService.GetProjectOverviewAsync(projectId);

        // Phase 1: validate and normalise
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("validate", "Validating business plan", 5, "Cross-referencing modules, entities and fields against project blueprint"), cancellationToken);
        Normalise(definition);
        EnsureReferentialIntegrity(definition, warnings);

        var existingModules = blueprint.Modules
            .ToDictionary(item => item.ModuleName, item => item, StringComparer.OrdinalIgnoreCase);

        // Phase 2: resolve modules (matching by technicalName OR key against existing ModuleName)
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Resolving modules", 15, $"Found {definition.Modules.Count} module(s), {existingModules.Count} already exist"), cancellationToken);
        // Keep the existing blueprint in the compiler.  A business plan is an
        // upsert, not a create-only payload: existing modules/entities must be
        // emitted as updates so the change-set validator does not reject them
        // as duplicate creates.

        // Phase 3: stage EntityFields with one task per module grouping all its entities
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Compiling EntityFields stage", 30,
            $"Generating one project_change_set task per module ({definition.Entities.Count} entity/entities across {definition.Modules.Count} module(s))"),
            cancellationToken);
        var entityFieldsTasks = CompileEntityFieldsTasks(definition, blueprint);

        // Phase 4: stage Views (depends on entity existence)
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Compiling Views stage", 50,
            $"Generating {definition.Views.Count} view_definition task(s)",
            addedViews: definition.Views.Count),
            cancellationToken);
        var viewsTasks = CompileViewsTasks(definition, entityFieldsTasks);

        // Phase 5: stage Relations (depends on entity + view consumers)
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Compiling Relations stage", 60,
            $"Generating relations task with {definition.Relations.Count} relation(s)"),
            cancellationToken);
        var relationsTasks = CompileRelationsTasks(definition, entityFieldsTasks, viewsTasks);

        // Phase 6: stage FieldControls (depends on relations)
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Compiling FieldControls stage", 70, "Generating control bindings for index/add/edit/detail"), cancellationToken);
        var fieldControlsTasks = CompileFieldControlsTasks(definition, entityFieldsTasks, viewsTasks, relationsTasks);

        // Phase 7: stage ComplexPages (depends on field controls)
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Compiling ComplexPages stage", 80, $"Generating {definition.Pages.Count} complex page task(s)"), cancellationToken);
        var pagesTasks = CompileComplexPagesTasks(definition, entityFieldsTasks, viewsTasks, relationsTasks, fieldControlsTasks);

        // Phase 8: ConsistencyCheck (depends on complex pages)
        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Compiling ConsistencyCheck stage", 90, "Validating final metadata"), cancellationToken);
        var consistencyTask = CompileConsistencyCheckTask(entityFieldsTasks, viewsTasks, relationsTasks, fieldControlsTasks, pagesTasks);

        // Phase 9: assemble final plan.
        // The plan stops at metadata. Code generation is a manual action the user triggers,
        // so the agent must not schedule it: everything here only writes sys_* metadata rows.
        var stages = new List<AiProjectWorkStageInput>
        {
            NewStage("EntityFields", "EntityFields", "实体和字段", 1, entityFieldsTasks),
            NewStage("Views", "Views", "视图", 2, viewsTasks),
            NewStage("Relations", "Relations", "关系", 3, relationsTasks),
            NewStage("FieldControls", "FieldControls", "字段控件", 4, fieldControlsTasks),
            NewStage("ComplexPages", "ComplexPages", "复杂页面", 5, pagesTasks),
            NewStage("ConsistencyCheck", "ConsistencyCheck", "一致性检查", 6, new List<AiProjectWorkTaskInput> { consistencyTask })
        };

        var totalTasks = stages.Sum(s => s.Tasks.Count);

        await EmitAsync(progressAsync, new BusinessPlanCompileEvent("compile", "Plan compiled", 100,
            $"Total: {totalTasks} task(s) across {stages.Count} stage(s). Saving plan.",
            totalTasks: totalTasks), cancellationToken);

        var compiled = new UpsertAiProjectWorkPlanRequest
        {
            ProjectId = projectId,
            ConversationId = conversationId,
            Title = ComposeTitle(definition),
            Summary = "业务计划编译产物(由 BusinessPlanCompiler 生成)",
            Stages = stages
        };

        var report = new BusinessCompileReport
        {
            Modules = definition.Modules.Count,
            Entities = definition.Entities.Count,
            Relations = definition.Relations.Count,
            Views = definition.Views.Count,
            Pages = definition.Pages.Count,
            Tasks = totalTasks
        };

        return new BusinessPlanCompileResult
        {
            Definition = definition,
            CompiledPlan = compiled,
            Warnings = warnings,
            Report = report
        };
    }

    private static async Task EmitAsync(
        Func<BusinessPlanCompileEvent, Task>? progressAsync,
        BusinessPlanCompileEvent evt,
        CancellationToken cancellationToken)
    {
        if (progressAsync == null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await progressAsync(evt);
    }

    private static void Normalise(BusinessPlanDefinition def)
    {
        // Trim and lower-case keys to make references case-insensitive.
        foreach (var m in def.Modules) m.Key = m.Key?.Trim() ?? string.Empty;
        foreach (var e in def.Entities)
        {
            e.Key = e.Key?.Trim() ?? string.Empty;
            e.ModuleKey = e.ModuleKey?.Trim() ?? string.Empty;
            foreach (var f in e.Fields) f.TechnicalName = f.TechnicalName?.Trim() ?? string.Empty;
        }
        foreach (var r in def.Relations)
        {
            r.Key = r.Key?.Trim() ?? string.Empty;
            r.SourceEntityKey = r.SourceEntityKey?.Trim() ?? string.Empty;
            r.TargetEntityKey = r.TargetEntityKey?.Trim() ?? string.Empty;
        }
        foreach (var v in def.Views)
        {
            v.Key = v.Key?.Trim() ?? string.Empty;
            v.EntityKey = v.EntityKey?.Trim() ?? string.Empty;
        }
        foreach (var p in def.Pages)
        {
            p.Key = p.Key?.Trim() ?? string.Empty;
            p.EntityKey = p.EntityKey?.Trim() ?? string.Empty;
        }
    }

    private static void EnsureReferentialIntegrity(BusinessPlanDefinition def, List<string> warnings)
    {
        var moduleKeys = new HashSet<string>(def.Modules.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
        var entityKeys = new HashSet<string>(def.Entities.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var e in def.Entities)
        {
            if (!moduleKeys.Contains(e.ModuleKey))
                throw new ArgumentException(
                    $"Entity '{e.Key}' references unknown moduleKey '{e.ModuleKey}'. " +
                    $"Available module keys: {string.Join(", ", moduleKeys)}");
        }
        foreach (var r in def.Relations)
        {
            if (!entityKeys.Contains(r.SourceEntityKey))
                throw new ArgumentException($"Relation '{r.Key}' source entity '{r.SourceEntityKey}' is not declared.");
            if (!entityKeys.Contains(r.TargetEntityKey))
                throw new ArgumentException($"Relation '{r.Key}' target entity '{r.TargetEntityKey}' is not declared.");
        }
        foreach (var v in def.Views)
        {
            if (!entityKeys.Contains(v.EntityKey))
                throw new ArgumentException($"View '{v.Key}' references unknown entityKey '{v.EntityKey}'.");
        }
        foreach (var p in def.Pages)
        {
            if (!entityKeys.Contains(p.EntityKey))
                throw new ArgumentException($"Page '{p.Key}' references unknown entityKey '{p.EntityKey}'.");
        }
    }

    private static List<AiProjectWorkTaskInput> CompileEntityFieldsTasks(
        BusinessPlanDefinition def,
        AiProjectOverviewDto blueprint)
    {
        var tasks = new List<AiProjectWorkTaskInput>();
        var entitiesByModule = def.Entities
            .GroupBy(e => e.ModuleKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in entitiesByModule)
        {
            var module = def.Modules.First(m => string.Equals(m.Key, group.Key, StringComparison.OrdinalIgnoreCase));
            var moduleTechName = string.IsNullOrWhiteSpace(module.TechnicalName)
                ? PascalCase(module.Key)
                : module.TechnicalName.Trim();

            var creates = new List<Dictionary<string, object?>>();
            var updates = new List<Dictionary<string, object?>>();
            foreach (var e in group)
            {
                var existing = blueprint.Modules
                    .FirstOrDefault(item => string.Equals(item.ModuleName, moduleTechName, StringComparison.OrdinalIgnoreCase))?
                    .Entities.FirstOrDefault(item => string.Equals(item.Name, PascalCase(e.TechnicalName), StringComparison.OrdinalIgnoreCase));
                var fields = e.Fields.Select(BuildFieldForEntity).ToList();
                if (existing == null)
                {
                    creates.Add(new Dictionary<string, object?>
                    {
                        ["moduleName"] = moduleTechName,
                        ["name"] = PascalCase(e.TechnicalName),
                        ["description"] = e.DisplayName?.Trim() ?? PascalCase(e.Key),
                        ["businessDescription"] = e.BusinessDescription,
                        ["tableName"] = (e.TableName ?? moduleTechName + "_" + PascalCase(e.Key)).Trim(),
                        ["isTree"] = e.IsTree ?? false,
                        ["isReadOnly"] = e.IsReadOnly ?? false,
                        ["hasTenant"] = e.HasTenant ?? true,
                        ["hasAudit"] = e.HasAudit ?? true,
                        ["hasSoftDelete"] = e.HasSoftDelete ?? true,
                        ["fields"] = fields.Cast<object>().ToList()
                    });
                }
                else
                {
                    var existingFields = existing.Fields.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);
                    updates.Add(new Dictionary<string, object?>
                    {
                        ["entityId"] = existing.Id,
                        ["description"] = e.DisplayName?.Trim(),
                        ["businessDescription"] = e.BusinessDescription,
                        ["newFields"] = fields
                            .Where(field => field.TryGetValue("name", out var name) &&
                                            name is string fieldName && !existingFields.ContainsKey(fieldName))
                            .Cast<object>().ToList()
                    });
                }
            }

            var existingModule = blueprint.Modules.FirstOrDefault(item =>
                string.Equals(item.ModuleName, moduleTechName, StringComparison.OrdinalIgnoreCase));
            var moduleDef = new Dictionary<string, object?>
            {
                ["moduleName"] = moduleTechName,
                ["moduleDescription"] = module.DisplayName?.Trim() ?? moduleTechName,
                ["icon"] = module.Icon ?? "",
                ["routePath"] = module.RoutePath ?? "",
                ["orderNum"] = module.OrderNum ?? 3
            };

            var definition = JsonSerializer.SerializeToElement(new
            {
                summary = $"创建 {module.DisplayName} 模块下的 {group.Count()} 个实体",
                modules = existingModule == null ? new[] { moduleDef } : Array.Empty<Dictionary<string, object?>>(),
                moduleUpdates = existingModule == null
                    ? Array.Empty<object>()
                    : new[]
                    {
                        new
                        {
                            moduleId = existingModule.Id,
                            moduleDescription = module.DisplayName?.Trim(),
                            icon = module.Icon,
                            routePath = module.RoutePath,
                            orderNum = module.OrderNum
                        }
                    },
                entities = creates,
                entityUpdates = updates
            });

            tasks.Add(new AiProjectWorkTaskInput
            {
                TaskKey = $"create_{moduleTechName.ToLowerInvariant()}_entities",
                Title = $"创建或更新 {module.DisplayName} 模块的实体",
                Summary = $"为 {module.DisplayName} 模块创建或更新 {group.Count()} 个实体",
                OperationKind = "project_change_set",
                ExecutionLocation = "Server",
                SafetyClass = "Metadata",
                SemanticReferences = new List<string> { $"module:{moduleTechName}" },
                DependsOnTaskKeys = new List<string>(),
                SortOrder = tasks.Count + 1,
                RequiresGeneration = false,
                RequiresBuild = false,
                Definition = definition
            });
        }

        return tasks;
    }

    private static Dictionary<string, object?> BuildFieldForEntity(BusinessFieldDefinition f)
    {
        var dataType = (f.DataType ?? "string").Trim().ToLowerInvariant();
        var control = InferControlType(dataType, f.Dictionary);
        return new Dictionary<string, object?>
        {
            ["name"] = PascalCase(f.TechnicalName),
            ["dataType"] = dataType,
            ["description"] = f.DisplayName?.Trim() ?? PascalCase(f.TechnicalName),
            ["businessDescription"] = null,
            ["isNullable"] = !(f.Required ?? false),
            ["maxLength"] = f.MaxLength,
            ["formControlType"] = control,
            ["selectDataSource"] = string.IsNullOrWhiteSpace(f.Dictionary) ? null : "dict",
            ["selectOptions"] = f.Dictionary,
            ["fieldCategory"] = "Normal",
            ["dictType"] = f.Dictionary
        };
    }

    private static string InferControlType(string dataType, string? dictionary)
    {
        if (!string.IsNullOrWhiteSpace(dictionary)) return "select";
        return dataType switch
        {
            "bool" or "boolean" => "switch",
            "date" or "datetime" or "datetimeoffset" => dataType == "date" ? "date" : "datetime",
            "int" or "integer" or "long" or "short" or "decimal" or "double" or "float" => "number",
            _ => "input"
        };
    }

    private static Dictionary<string, object?> BuildField(BusinessFieldDefinition f)
    {
        var dataType = (f.DataType ?? "string").Trim().ToLowerInvariant();
        return new Dictionary<string, object?>
        {
            ["name"] = PascalCase(f.TechnicalName),
            ["type"] = dataType,
            ["displayName"] = f.DisplayName?.Trim() ?? PascalCase(f.TechnicalName),
            ["required"] = f.Required ?? false,
            ["isNullable"] = !(f.Required ?? false),
            ["maxLength"] = f.MaxLength,
            ["dictionary"] = f.Dictionary,
            ["role"] = f.Role
        };
    }

    private static List<AiProjectWorkTaskInput> CompileViewsTasks(
        BusinessPlanDefinition def,
        List<AiProjectWorkTaskInput> entityTasks)
    {
        var entityTaskKeyByKey = def.Entities
            .GroupBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => $"create_{PascalCase(def.Modules.First(m => string.Equals(m.Key, g.First().ModuleKey, StringComparison.OrdinalIgnoreCase)).TechnicalName ?? PascalCase(g.First().ModuleKey)).ToLowerInvariant()}_entities", StringComparer.OrdinalIgnoreCase);

        var tasks = new List<AiProjectWorkTaskInput>();
        foreach (var view in def.Views)
        {
            var entityTaskKey = entityTaskKeyByKey.GetValueOrDefault(view.EntityKey);
            if (entityTaskKey == null) continue;

            var viewEntity = def.Entities.FirstOrDefault(entity =>
                string.Equals(entity.Key, view.EntityKey, StringComparison.OrdinalIgnoreCase));
            var viewModule = viewEntity == null
                ? null
                : def.Modules.FirstOrDefault(module => string.Equals(module.Key, viewEntity.ModuleKey, StringComparison.OrdinalIgnoreCase));
            var viewModuleName = string.IsNullOrWhiteSpace(viewModule?.TechnicalName)
                ? PascalCase(viewModule?.Key)
                : viewModule!.TechnicalName.Trim();

            var definition = JsonSerializer.SerializeToElement(new
            {
                summary = view.DisplayName,
                reason = $"为 {view.DisplayName} 保存受管只读视图定义和消费者元数据。",
                query = new
                {
                    projectId = 0,
                    sql = view.Sql,
                    entities = Array.Empty<object>(),
                    joins = Array.Empty<object>(),
                    includeInferredJoins = false
                },
                entityFields = Array.Empty<object>(),
                viewName = view.ViewName,
                replaceExistingView = true,
                moduleId = 0,
                moduleName = viewModuleName,
                existingEntityId = (long?)null,
                entityName = PascalCase(viewEntity?.TechnicalName),
                displayName = view.DisplayName,
                keyColumn = "Id",
                generateFrontend = true,
                definitionOnly = true,
                generateAfterApproval = false,
                incremental = true,
                buildAfterGeneration = false
            });

            tasks.Add(new AiProjectWorkTaskInput
            {
                TaskKey = $"create_{view.ViewName}_view",
                Title = $"视图 {view.DisplayName}",
                Summary = view.DisplayName,
                OperationKind = "view_definition",
                ExecutionLocation = "Server",
                SafetyClass = "Metadata",
                SemanticReferences = new List<string> { $"module:{viewModuleName}" },
                DependsOnTaskKeys = new List<string> { entityTaskKey },
                SortOrder = tasks.Count + 1,
                RequiresGeneration = false,
                RequiresBuild = false,
                Definition = definition
            });
        }
        return tasks;
    }

    private static List<AiProjectWorkTaskInput> CompileRelationsTasks(
        BusinessPlanDefinition def,
        List<AiProjectWorkTaskInput> entityTasks,
        List<AiProjectWorkTaskInput> viewTasks)
    {
        if (def.Relations.Count == 0)
            return [];

        var relationDefinitions = def.Relations.Select(relation =>
        {
            var source = def.Entities.First(item => string.Equals(item.Key, relation.SourceEntityKey, StringComparison.OrdinalIgnoreCase));
            var target = def.Entities.First(item => string.Equals(item.Key, relation.TargetEntityKey, StringComparison.OrdinalIgnoreCase));
            var kind = relation.Kind?.Trim() ?? string.Empty;
            var owned = kind.Contains("Owned", StringComparison.OrdinalIgnoreCase);
            var cardinality = kind.Contains("OneToOne", StringComparison.OrdinalIgnoreCase)
                ? "OneToOne"
                : kind.Contains("ManyToOne", StringComparison.OrdinalIgnoreCase)
                    ? "ManyToOne"
                    : "OneToMany";
            return new
            {
                sourceEntity = PascalCase(source.TechnicalName),
                sourceField = string.IsNullOrWhiteSpace(relation.SourceField) ? "Id" : relation.SourceField,
                targetEntityName = PascalCase(target.TechnicalName),
                targetField = string.IsNullOrWhiteSpace(relation.TargetField) ? "Id" : relation.TargetField,
                relationName = string.IsNullOrWhiteSpace(relation.DisplayName) ? PascalCase(relation.Key) : relation.DisplayName,
                cardinality,
                ownership = owned ? "Owned" : "Reference",
                isRequired = false,
                deleteBehavior = owned ? "Delete" : "Restrict",
                orderNum = 0
            };
        }).ToList();

        var relationTaskKey = def.Modules.Count == 1
            ? $"setup_{(string.IsNullOrWhiteSpace(def.Modules[0].TechnicalName) ? PascalCase(def.Modules[0].Key) : def.Modules[0].TechnicalName.Trim()).ToLowerInvariant()}_relations"
            : "setup_relations";

        return
        [
            new AiProjectWorkTaskInput
            {
                TaskKey = relationTaskKey,
                Title = "建立主外键关系",
                Summary = $"建立 {relationDefinitions.Count} 个主外键或对象关系",
                OperationKind = "project_change_set",
                ExecutionLocation = "Server",
                SafetyClass = "Metadata",
                SemanticReferences = def.Entities.Select(entity => $"entity:{PascalCase(entity.TechnicalName)}").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                DependsOnTaskKeys = entityTasks.Select(task => task.TaskKey).Concat(viewTasks.Select(task => task.TaskKey)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                SortOrder = 1,
                RequiresGeneration = false,
                RequiresBuild = false,
                Definition = JsonSerializer.SerializeToElement(new
                {
                    summary = "建立主外键关系",
                    relations = relationDefinitions,
                    generationMode = "None"
                })
            }
        ];
    }

    private static List<AiProjectWorkTaskInput> CompileFieldControlsTasks(
        BusinessPlanDefinition def,
        List<AiProjectWorkTaskInput> entityTasks,
        List<AiProjectWorkTaskInput> viewTasks,
        List<AiProjectWorkTaskInput> relationTasks)
    {
        var bindings = def.Entities
            .SelectMany(entity => entity.Fields
                .Where(field => !string.IsNullOrWhiteSpace(field.Dictionary) ||
                                !string.Equals(InferControlType((field.DataType ?? "string").Trim().ToLowerInvariant(), field.Dictionary), "input", StringComparison.OrdinalIgnoreCase))
                .Select(field => new
                {
                    entity = PascalCase(entity.TechnicalName),
                    field = PascalCase(field.TechnicalName),
                    formControlType = InferControlType((field.DataType ?? "string").Trim().ToLowerInvariant(), field.Dictionary),
                    selectDataSource = string.IsNullOrWhiteSpace(field.Dictionary) ? null : "dict",
                    selectOptions = field.Dictionary
                }))
            .ToList();
        if (bindings.Count == 0)
            return [];

        var controlTaskKey = def.Modules.Count == 1
            ? $"setup_{(string.IsNullOrWhiteSpace(def.Modules[0].TechnicalName) ? PascalCase(def.Modules[0].Key) : def.Modules[0].TechnicalName.Trim()).ToLowerInvariant()}_controls"
            : "setup_field_controls";

        return
        [
            new AiProjectWorkTaskInput
            {
                TaskKey = controlTaskKey,
                Title = "配置字段控件",
                Summary = $"配置 {bindings.Count} 个字段控件、字典和展示规则",
                OperationKind = "project_change_set",
                ExecutionLocation = "Server",
                SafetyClass = "Metadata",
                SemanticReferences = def.Entities.Select(entity => $"entity:{PascalCase(entity.TechnicalName)}").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                DependsOnTaskKeys = (relationTasks.Count > 0 ? relationTasks : entityTasks.Concat(viewTasks)).Select(task => task.TaskKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                SortOrder = 1,
                RequiresGeneration = false,
                RequiresBuild = false,
                Definition = JsonSerializer.SerializeToElement(new
                {
                    summary = "配置字段控件",
                    controlBindings = bindings,
                    generationMode = "None"
                })
            }
        ];
    }

    private static List<AiProjectWorkTaskInput> CompileComplexPagesTasks(
        BusinessPlanDefinition def,
        List<AiProjectWorkTaskInput> entityTasks,
        List<AiProjectWorkTaskInput> viewTasks,
        List<AiProjectWorkTaskInput> relationTasks,
        List<AiProjectWorkTaskInput> fieldControlTasks)
    {
        var tasks = new List<AiProjectWorkTaskInput>();
        foreach (var page in def.Pages)
        {
            var entity = def.Entities.First(item => string.Equals(item.Key, page.EntityKey, StringComparison.OrdinalIgnoreCase));
            var module = def.Modules.First(item => string.Equals(item.Key, entity.ModuleKey, StringComparison.OrdinalIgnoreCase));
            var moduleTechName = string.IsNullOrWhiteSpace(module.TechnicalName)
                ? PascalCase(module.Key)
                : module.TechnicalName.Trim();
            var pageKey = string.IsNullOrWhiteSpace(page.Key) ? PascalCase(page.EntityKey).ToLowerInvariant() : page.Key.Trim();
            var taskKey = def.Pages.Count == 1 && def.Modules.Count == 1
                ? $"setup_{moduleTechName.ToLowerInvariant()}_pages"
                : $"create_{pageKey.ToLowerInvariant()}_page";
            var pageType = page.PageType?.Trim().ToLowerInvariant() switch
            {
                "index" or "list" => "List",
                "detail" => "Detail",
                "add" or "edit" or "form" => "Form",
                _ => "Custom"
            };
            tasks.Add(new AiProjectWorkTaskInput
            {
                TaskKey = taskKey,
                Title = $"配置 {page.DisplayName} 复杂页面",
                OperationKind = "complex_page",
                ExecutionLocation = "Server",
                SafetyClass = "Metadata",
                SemanticReferences = new List<string> { $"module:{moduleTechName}" },
                DependsOnTaskKeys = (fieldControlTasks.Count > 0 ? fieldControlTasks : relationTasks.Count > 0 ? relationTasks : entityTasks.Concat(viewTasks)).Select(task => task.TaskKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                SortOrder = tasks.Count + 1,
                RequiresGeneration = false,
                RequiresBuild = false,
                Definition = JsonSerializer.SerializeToElement(new
                {
                    summary = page.DisplayName,
                    moduleName = moduleTechName,
                    definition = new
                    {
                        projectId = 0,
                        moduleId = 0,
                        moduleName = moduleTechName,
                        pageKey,
                        pageName = page.DisplayName,
                        description = page.DisplayName,
                        routePath = $"/{moduleTechName.ToLowerInvariant()}/{pageKey.ToLowerInvariant()}",
                        pageType,
                        layoutJson = "{\"version\":1,\"root\":{\"type\":\"page\",\"componentId\":\"page_root\",\"children\":[]}}",
                        syncMode = "Auto",
                        isMenuSynced = false,
                        dataSources = new[]
                        {
                            new
                            {
                                sourceKey = "main",
                                sourceType = "EntityService",
                                entityName = PascalCase(entity.TechnicalName),
                                serviceName = $"{PascalCase(entity.TechnicalName)}Service",
                                methodName = "GetListAsync",
                                dtoName = $"{PascalCase(entity.TechnicalName)}Dto",
                                dataShape = "Collection",
                                syncMode = "Auto",
                                paramsJson = "{}",
                                orderNum = 1,
                                isEnabled = true
                            }
                        },
                        bindings = Array.Empty<object>()
                    },
                    decisions = new[]
                    {
                        new { target = "dataSource:main", reason = "使用工程实体的列表服务作为页面主数据源，保持服务契约和集合数据形态。" }
                    },
                    backendChanges = Array.Empty<object>(),
                    generateAfterApproval = false,
                    incremental = true,
                    buildAfterGeneration = false
                })
            });
        }
        return tasks;
    }

    private static AiProjectWorkTaskInput CompileConsistencyCheckTask(
        List<AiProjectWorkTaskInput> entityTasks,
        List<AiProjectWorkTaskInput> viewTasks,
        List<AiProjectWorkTaskInput> relationTasks,
        List<AiProjectWorkTaskInput> fieldControlTasks,
        List<AiProjectWorkTaskInput> pageTasks)
        => new AiProjectWorkTaskInput
        {
            TaskKey = "consistency_check",
            Title = "校验一致性",
            OperationKind = "project_consistency_check",
            ExecutionLocation = "Server",
            SafetyClass = "Metadata",
            SemanticReferences = new List<string>(),
            DependsOnTaskKeys = entityTasks
                .Concat(viewTasks)
                .Concat(relationTasks)
                .Concat(fieldControlTasks)
                .Concat(pageTasks)
                .Select(t => t.TaskKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SortOrder = 1,
            RequiresGeneration = false,
            RequiresBuild = false,
            Definition = JsonSerializer.SerializeToElement(new
            {
                kind = "project_consistency_check",
                summary = "校验元数据一致性",
                checks = Array.Empty<object>()
            })
        };


    private static AiProjectWorkStageInput NewStage(
        string stageKey,
        string kind,
        string title,
        int sortOrder,
        IEnumerable<AiProjectWorkTaskInput> tasks)
    {
        return new AiProjectWorkStageInput
        {
            StageKey = stageKey,
            Kind = kind,
            Title = title,
            GenerationMode = "None",
            SortOrder = sortOrder,
            BuildAfterStage = false,
            Tasks = tasks.ToList()
        };
    }

    private static string ComposeTitle(BusinessPlanDefinition def)
    {
        if (def.Modules.Count == 1)
            return $"{def.Modules[0].DisplayName} 实施计划";
        return "业务计划编译产物";
    }

    private static string PascalCase(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var trimmed = input.Trim();
        if (!trimmed.Contains('-') && !trimmed.Contains('_') && !trimmed.Contains(' '))
        {
            return char.ToUpperInvariant(trimmed[0]) + (trimmed.Length > 1 ? trimmed[1..] : string.Empty);
        }

        var parts = trimmed.Replace('-', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..].ToLowerInvariant() : "")));
    }
}
