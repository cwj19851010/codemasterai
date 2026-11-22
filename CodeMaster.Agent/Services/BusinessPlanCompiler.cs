using System.Text.Json;
using System.Text.Json.Serialization;
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
    public int Dictionaries { get; init; }
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
        RepairDictionaries(definition, warnings);
        EnsureReferentialIntegrity(definition, warnings);
        // 关系引用的外键列必须由 EntityFields 阶段先建出来。Relations 阶段排在它后面，
        // 校验器会在目标实体字段表里查 targetField，缺列就整批报 "references missing target field"。
        EnsureRelationForeignKeyFields(definition, warnings);

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

        // Dictionaries are written into the generated project's database, so they ride the same
        // project_change_set task kind as entities. The task sits first inside the EntityFields
        // stage: a field that references a dictionary code must never exist before the dictionary
        // itself, otherwise the generated select has nothing to render.
        var dictionaryTask = CompileDictionaryTask(definition);
        if (dictionaryTask != null)
            entityFieldsTasks.Insert(0, dictionaryTask);

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
            Dictionaries = definition.Dictionaries.Count,
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
        foreach (var d in def.Dictionaries)
        {
            d.DictType = d.DictType?.Trim() ?? string.Empty;
            d.DictName = string.IsNullOrWhiteSpace(d.DictName) ? d.DictType : d.DictName.Trim();
            foreach (var item in d.Items)
            {
                item.Label = item.Label?.Trim() ?? string.Empty;
                item.Value = item.Value?.Trim() ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// The chat model regularly swaps dictType and dictName — it reads dictType as the field's
    /// data type and dictName as the dictionary code. The field reference is authoritative: the
    /// generator writes the field's dictionary value into SelectOptions, so a declared dictionary
    /// whose name matches a referenced code (while its own code does not) is swapped instead of
    /// being rejected or silently merged with a sibling that shares the bogus code.
    /// </summary>
    private static void RepairDictionaries(BusinessPlanDefinition def, List<string> warnings)
    {
        if (def.Dictionaries.Count == 0)
            return;

        var referencedCodes = new HashSet<string>(
            def.Entities
                .SelectMany(entity => entity.Fields)
                .Select(field => field.Dictionary?.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!),
            StringComparer.OrdinalIgnoreCase);
        if (referencedCodes.Count == 0)
            return;

        foreach (var dictionary in def.Dictionaries)
        {
            var code = dictionary.DictType?.Trim() ?? string.Empty;
            var name = dictionary.DictName?.Trim() ?? string.Empty;
            if (referencedCodes.Contains(code) || !referencedCodes.Contains(name))
                continue;

            dictionary.DictType = name;
            dictionary.DictName = string.IsNullOrWhiteSpace(code) || IsDataTypeKeyword(code)
                ? (string.IsNullOrWhiteSpace(dictionary.Remark) ? name : dictionary.Remark.Trim())
                : code;
            warnings.Add($"字典 '{name}' 的 dictType/dictName 写反了，已自动纠正为 dictType={name}。");
        }
    }

    private static bool IsDataTypeKeyword(string value) => value.Trim().ToLowerInvariant() switch
    {
        "string" or "text" or "int" or "integer" or "long" or "short" or "decimal" or "double"
            or "float" or "bool" or "boolean" or "date" or "datetime" or "datetimeoffset" => true,
        _ => false
    };

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

        // A field only stores the dictionary code; the dictionary itself has to be declared here.
        // Missing entries are a warning, not an error: the metadata is still valid, the select is
        // just empty until someone fills the dictionary in the generated project.
        var declaredDictTypes = new HashSet<string>(
            def.Dictionaries.Select(d => d.DictType).Where(t => !string.IsNullOrWhiteSpace(t)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entity in def.Entities)
        {
            foreach (var field in entity.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Dictionary))
                    continue;
                if (declaredDictTypes.Contains(field.Dictionary.Trim()))
                    continue;
                warnings.Add(
                    $"字段 {entity.Key}.{field.TechnicalName} 引用字典 '{field.Dictionary.Trim()}'，" +
                    "但计划未声明该字典；生成后下拉框会是空的。");
            }
        }
    }

    /// <summary>
    /// 把关系引用的外键列补进外键所属实体的字段列表，让 EntityFields 阶段先于 Relations 阶段建列。
    /// <para>
    /// 模型习惯只在 relations 里写出 <c>targetField: "ProjectId"</c>，却忘了在"多"方实体的
    /// fields 里声明同名列。计划编译后 EntityFields 先执行（建实体，无外键列），Relations 后执行，
    /// change-set 校验器在目标实体字段表里找不到 targetField，于是整批报
    /// "references missing target field 'ProjectId'" / "uses removed or renamed target field"，
    /// 关系一条都写不进去，只能靠失败后重新编译一次再补列。外键归属由关系方向唯一确定，
    /// 所以这里确定性补齐，不依赖模型或重试。
    /// </para>
    /// <para>
    /// 顺带把 relation 引用的字段名规整成 PascalCase，与 <see cref="BuildFieldForEntity"/> 写出的
    /// 列名保持一致；否则补了 <c>ProjectId</c> 而关系仍引用 <c>project_id</c>，照样对不上。
    /// 补建后 <see cref="ApplyReferenceControls"/> 的前置检查（字段必须存在）也会通过，
    /// 外键自动绑定为 select-table 下拉，而不是退化成纯数字输入框。
    /// </para>
    /// </summary>
    private static void EnsureRelationForeignKeyFields(BusinessPlanDefinition def, List<string> warnings)
    {
        if (def.Relations.Count == 0)
            return;

        var entitiesByKey = def.Entities
            .Where(entity => !string.IsNullOrWhiteSpace(entity.Key))
            .GroupBy(entity => entity.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var relation in def.Relations)
        {
            var kind = relation.Kind?.Trim() ?? string.Empty;
            // Owned 聚合的子表是内联网格，父子关联由聚合机制管理，不走独立外键列；
            // ApplyReferenceControls 也跳过它们。只有 Reference 类关系需要这里补列。
            if (kind.Contains("Owned", StringComparison.OrdinalIgnoreCase))
                continue;

            var manyToOne = kind.Contains("ManyToOne", StringComparison.OrdinalIgnoreCase);

            // OneToMany / OneToOne：外键在"多"方（target entity），名字取 targetField；
            // ManyToOne Reference：外键在 dependent（source entity），名字取 sourceField。
            var ownerKey = (manyToOne ? relation.SourceEntityKey : relation.TargetEntityKey)?.Trim() ?? string.Empty;
            var rawField = (manyToOne ? relation.SourceField : relation.TargetField)?.Trim() ?? string.Empty;

            // 空值走编译器原有的 "Id" 兜底；Id 是主键，不需要补建。
            if (string.IsNullOrWhiteSpace(rawField) ||
                string.Equals(rawField, "Id", StringComparison.OrdinalIgnoreCase))
                continue;

            var canonical = PascalCase(rawField);
            if (manyToOne)
                relation.SourceField = canonical;
            else
                relation.TargetField = canonical;

            // EnsureReferentialIntegrity 已为未声明的实体抛错，这里不再重复报错。
            if (!entitiesByKey.TryGetValue(ownerKey, out var owner))
                continue;

            if (owner.Fields.Any(field =>
                    string.Equals(PascalCase(field.TechnicalName), canonical, StringComparison.OrdinalIgnoreCase)))
                continue;

            owner.Fields.Add(new BusinessFieldDefinition
            {
                TechnicalName = canonical,
                DisplayName = canonical,
                DataType = "long",
                Required = false,
                Role = "foreign key"
            });
            warnings.Add(
                $"关系 '{relation.Key}' 引用外键 {PascalCase(owner.TechnicalName)}.{canonical}，" +
                "已自动在外键所属实体补建该 long 字段，避免 Relations 阶段引用不存在的列。");
        }
    }

    /// <summary>
    /// Builds the single dictionary task for the plan, or null when the business plan declares no
    /// dictionaries. Entries are optional: creating the type alone already makes the generated
    /// dictionary page non-empty and lets the user fill entries in the generated project.
    /// </summary>
    private static AiProjectWorkTaskInput? CompileDictionaryTask(BusinessPlanDefinition def)
    {
        var dictionaries = def.Dictionaries
            .Where(item => !string.IsNullOrWhiteSpace(item.DictType))
            .GroupBy(item => item.DictType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (dictionaries.Count == 0)
            return null;

        var definition = JsonSerializer.SerializeToElement(new
        {
            summary = $"创建或更新 {dictionaries.Count} 个数据字典",
            dictionaries = dictionaries.Select(dictionary => new
            {
                // Every dictionary entry needs its own summary: the change-set validator treats
                // each item as a standalone dictionary change.
                summary = $"创建或更新字典 {(string.IsNullOrWhiteSpace(dictionary.DictName) ? dictionary.DictType.Trim() : dictionary.DictName.Trim())}",
                tenantId = 0,
                dictType = dictionary.DictType.Trim(),
                dictName = string.IsNullOrWhiteSpace(dictionary.DictName)
                    ? dictionary.DictType.Trim()
                    : dictionary.DictName.Trim(),
                remark = dictionary.Remark,
                items = dictionary.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                    .Select((item, index) => new
                    {
                        label = string.IsNullOrWhiteSpace(item.Label) ? item.Value.Trim() : item.Label.Trim(),
                        value = item.Value.Trim(),
                        isDefault = item.IsDefault == true ? 1 : (int?)null,
                        sort = item.Sort ?? index + 1
                    })
                    .ToList()
            }).ToList()
        });

        return new AiProjectWorkTaskInput
        {
            TaskKey = "create_business_dictionaries",
            Title = "创建数据字典",
            Summary = $"创建或更新 {dictionaries.Count} 个数据字典",
            OperationKind = "project_change_set",
            ExecutionLocation = "Server",
            SafetyClass = "Metadata",
            SemanticReferences = dictionaries.Select(item => $"dictionary:{item.DictType.Trim()}").ToList(),
            DependsOnTaskKeys = new List<string>(),
            SortOrder = 0,
            RequiresGeneration = false,
            RequiresBuild = false,
            Definition = definition
        };
    }

    private static List<AiProjectWorkTaskInput> CompileEntityFieldsTasks(
        BusinessPlanDefinition def,
        AiProjectOverviewDto blueprint)
    {        var tasks = new List<AiProjectWorkTaskInput>();
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
        // A read-only entity is a reporting projection: it has no add/edit form, and when it is backed
        // by a view its field list is rebuilt from the SELECT's result columns — the view's key column
        // is renamed to Id, so a binding computed from the declared list can name a column that no
        // longer exists. Emitting bindings for it produces noise at best and, before the normalizer
        // learned to drop them, failed the whole stage.
        var formlessEntityKeys = def.Entities
            .Where(entity => entity.IsReadOnly == true)
            .Select(entity => entity.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bindings = def.Entities
            .Where(entity => !formlessEntityKeys.Contains(entity.Key))
            .SelectMany(entity => entity.Fields
                .Where(field => !string.IsNullOrWhiteSpace(field.Dictionary) ||
                                !string.Equals(InferControlType((field.DataType ?? "string").Trim().ToLowerInvariant(), field.Dictionary), "input", StringComparison.OrdinalIgnoreCase))
                .Select(field => new ControlBindingSpec
                {
                    Entity = PascalCase(entity.TechnicalName),
                    Field = PascalCase(field.TechnicalName),
                    FormControlType = InferControlType((field.DataType ?? "string").Trim().ToLowerInvariant(), field.Dictionary),
                    SelectDataSource = string.IsNullOrWhiteSpace(field.Dictionary) ? null : "dict",
                    SelectOptions = field.Dictionary
                }))
            .ToList();

        ApplyReferenceControls(def, bindings, formlessEntityKeys);

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

    /// <summary>
    /// A field that points at another entity must be edited with a picker, not a number box.
    /// </summary>
    /// <remarks>
    /// <see cref="InferControlType"/> only looks at the C# type, so every <c>long</c> foreign key came out
    /// as <c>number</c>: the generated add/edit form asked the user to type a raw id, and the relation that
    /// already said which entity the field points at was ignored. The relation definition is the only place
    /// that knows the target, so the control is materialized from it here instead of hoping the model sets it.
    ///
    /// Both reference shapes are handled, because models use them interchangeably for the same fact:
    /// <list type="bullet">
    /// <item><c>ManyToOne</c> from the child — the key is <c>sourceField</c> and the picker points at the target.</item>
    /// <item><c>OneToMany</c> from the parent — the key is <c>targetField</c> and the picker points at the source.</item>
    /// </list>
    /// Owned relations are skipped: their child is an inline grid, not a lookup.
    /// </remarks>
    private static void ApplyReferenceControls(
        BusinessPlanDefinition def,
        List<ControlBindingSpec> bindings,
        IReadOnlySet<string> formlessEntityKeys)
    {
        if (def.Relations.Count == 0)
            return;

        var entityByKey = def.Entities
            .GroupBy(entity => entity.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var relation in def.Relations)
        {
            var kind = relation.Kind?.Trim() ?? string.Empty;
            if (kind.Contains("Owned", StringComparison.OrdinalIgnoreCase))
                continue;

            var manyToOne = kind.Contains("ManyToOne", StringComparison.OrdinalIgnoreCase);
            var oneToMany = !manyToOne && kind.Contains("OneToMany", StringComparison.OrdinalIgnoreCase);
            if (!manyToOne && !oneToMany)
                continue;

            // The foreign key and the entity a picker must list sit on opposite ends of the relation.
            var holderKey = manyToOne ? relation.SourceEntityKey : relation.TargetEntityKey;
            var holderField = manyToOne ? relation.SourceField : relation.TargetField;
            var relatedKey = manyToOne ? relation.TargetEntityKey : relation.SourceEntityKey;

            if (string.IsNullOrWhiteSpace(holderField) ||
                !entityByKey.TryGetValue(holderKey ?? string.Empty, out var holder) ||
                !entityByKey.TryGetValue(relatedKey ?? string.Empty, out var related) ||
                string.Equals(holder.Key, related.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A read-only projection carries no form, and its field list is rebuilt from the SELECT when
            // the view is created, so a control derived from the declared list would point at nothing.
            if (formlessEntityKeys.Contains(holder.Key))
                continue;

            var entityName = PascalCase(holder.TechnicalName);
            var fieldName = PascalCase(holderField);
            if (!holder.Fields.Any(field => string.Equals(PascalCase(field.TechnicalName), fieldName, StringComparison.OrdinalIgnoreCase)))
            {
                // The relation names a field the entity never declares. Binding it anyway used to
                // produce a controlBinding for a field that cannot exist, which failed the whole stage
                // when the change set was normalized.
                continue;
            }

            var binding = bindings.FirstOrDefault(item =>
                string.Equals(item.Entity, entityName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Field, fieldName, StringComparison.OrdinalIgnoreCase));
            if (binding == null)
            {
                // Defensive: the projection above drops plain `input` fields, and a foreign key is never one
                // of those. Adding the binding anyway keeps this derivation independent of that filter.
                binding = new ControlBindingSpec { Entity = entityName, Field = fieldName };
                bindings.Add(binding);
            }

            binding.FormControlType = "select-table";
            binding.SelectDataSource = null;
            binding.SelectOptions = null;
            binding.RelatedEntityName = PascalCase(related.TechnicalName);
            binding.RelatedEntityIdField = "Id";
            binding.RelatedEntityDisplayFields = [ResolveDisplayField(related)];
        }
    }

    /// <summary>
    /// Picks the label a picker shows for a row of <paramref name="entity"/>.
    /// </summary>
    /// <remarks>
    /// "First text field" is not good enough: models emit fields in business order, so a project's
    /// <c>Location</c> can precede its <c>ProjectName</c> and the picker ends up listing cities. The name
    /// suffix is the reliable signal — <c>ProjectName</c>, <c>SupplierName</c>, <c>MaterialName</c> — so it
    /// is preferred, then a code-like column, then declaration order. Falls back to <c>Name</c> when the
    /// target has no text field at all, so the picker still loads instead of rendering an empty column.
    /// </remarks>
    private static string ResolveDisplayField(BusinessEntityDefinition entity)
    {
        var text = entity.Fields
            .Where(field => string.Equals((field.DataType ?? string.Empty).Trim(), "string", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(field.Role?.Trim(), "primary key", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidate = text.FirstOrDefault(field => string.Equals(field.TechnicalName?.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
                        ?? text.FirstOrDefault(field => (field.TechnicalName ?? string.Empty).Trim().EndsWith("Name", StringComparison.OrdinalIgnoreCase))
                        ?? text.FirstOrDefault(field => (field.TechnicalName ?? string.Empty).Trim().EndsWith("Title", StringComparison.OrdinalIgnoreCase))
                        ?? text.FirstOrDefault(field => (field.TechnicalName ?? string.Empty).Trim().EndsWith("Code", StringComparison.OrdinalIgnoreCase) ||
                                                        (field.TechnicalName ?? string.Empty).Trim().EndsWith("No", StringComparison.OrdinalIgnoreCase))
                        ?? text.FirstOrDefault();

        return PascalCase(candidate?.TechnicalName ?? "Name");
    }

    /// <summary>
    /// Mutable shape for one entry of <c>controlBindings</c>. The property names are exactly the ones
    /// <see cref="ProjectChangeSetDefinitionNormalizer"/> forwards to <c>UpdateEntityFieldProposal</c>, so a
    /// binding can carry the whole select-table configuration and not just the control id.
    /// </summary>
    private sealed class ControlBindingSpec
    {
        [JsonPropertyName("entity")] public string Entity { get; set; } = string.Empty;
        [JsonPropertyName("field")] public string Field { get; set; } = string.Empty;
        [JsonPropertyName("formControlType")] public string FormControlType { get; set; } = "input";
        [JsonPropertyName("selectDataSource")] public string? SelectDataSource { get; set; }
        [JsonPropertyName("selectOptions")] public string? SelectOptions { get; set; }
        [JsonPropertyName("relatedEntityName")] public string? RelatedEntityName { get; set; }
        [JsonPropertyName("relatedEntityIdField")] public string? RelatedEntityIdField { get; set; }
        [JsonPropertyName("relatedEntityDisplayFields")] public string[]? RelatedEntityDisplayFields { get; set; }
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
