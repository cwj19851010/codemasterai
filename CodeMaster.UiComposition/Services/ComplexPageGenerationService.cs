using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.Authorization;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Infrastructure.VueParser;
using CodeMaster.Infrastructure.VueParser.Model;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// Generates the independent file bundle for a ProjectPage.  The service is intentionally
/// separate from ModuleEntityService so adding a page does not expand the legacy CRUD
/// page-type switch.
/// </summary>
[DynamicApiPermission(requirePermission: true)]
public sealed class ComplexPageGenerationService : IComplexPageGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ISqlSugarClient _db;
    private readonly IProjectPageService _pageService;
    private readonly IComponentTreeMergeService _treeMerge;
    private readonly IApplicationServiceCapabilityCatalog? _capabilityCatalog;

    public ComplexPageGenerationService(
        ISqlSugarClient db,
        IProjectPageService pageService,
        IComponentTreeMergeService? treeMerge = null,
        IApplicationServiceCapabilityCatalog? capabilityCatalog = null)
    {
        _db = db;
        _pageService = pageService;
        _treeMerge = treeMerge ?? new ComponentTreeMergeService();
        _capabilityCatalog = capabilityCatalog;
    }

    [Permission("codegen:projectpage:generate")]
    public async Task<ComplexPageGenerationResultDto> GenerateAsync(long pageId, bool incremental = true)
    {
        var page = await _pageService.GetByIdAsync(pageId)
            ?? throw new InvalidOperationException($"Project page {pageId} was not found.");
        var project = await _db.Queryable<Project>().Where(item => item.Id == page.ProjectId && !item.IsDeleted).FirstAsync()
            ?? throw new InvalidOperationException($"Project {page.ProjectId} was not found.");
        var module = await _db.Queryable<ProjectModule>().Where(item => item.Id == page.ModuleId && !item.IsDeleted).FirstAsync()
            ?? throw new InvalidOperationException($"Project module {page.ModuleId} was not found.");

        var result = new ComplexPageGenerationResultDto
        {
            PageId = page.Id,
            PageKey = page.PageKey
        };
        string? outputDirectory = null;
        try
        {
            outputDirectory = ResolveOutputDirectory(project, module, page.PageKey);
            result.OutputDirectory = outputDirectory;
            Directory.CreateDirectory(outputDirectory);

            var existingVuePath = Path.Combine(outputDirectory, "index.vue");

            var layout = PageLayoutTree.Parse(page.LayoutJson);
            var fieldIds = page.Bindings.Where(item => item.FieldId.HasValue).Select(item => item.FieldId!.Value).Distinct().ToList();
            var fields = fieldIds.Count == 0
                ? new List<EntityField>()
                : await _db.Queryable<EntityField>().Where(item => fieldIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
            var entityIds = page.DataSources.Where(item => item.EntityId.HasValue).Select(item => item.EntityId!.Value).Distinct().ToList();
            var entities = entityIds.Count == 0
                ? new List<ModuleEntity>()
                : await _db.Queryable<ModuleEntity>().Where(item => entityIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
            var moduleIds = entities.Select(item => item.ModuleId).Distinct().ToList();
            var sourceModules = moduleIds.Count == 0
                ? new List<ProjectModule>()
                : await _db.Queryable<ProjectModule>().Where(item => moduleIds.Contains(item.Id) && !item.IsDeleted).ToListAsync();
            var projectSourceIds = page.DataSources
                .Where(item => item.ProjectDataSourceId.HasValue)
                .Select(item => item.ProjectDataSourceId!.Value)
                .Distinct()
                .ToList();
            var projectSources = projectSourceIds.Count == 0
                ? new List<ProjectDataSource>()
                : await _db.Queryable<ProjectDataSource>()
                    .Where(item => projectSourceIds.Contains(item.Id) && item.ProjectId == page.ProjectId && !item.IsDeleted)
                    .ToListAsync();
            var apiDefinitions = page.DataSources.ToDictionary(
                item => item.Id,
                item => BuildApiDefinition(item, entities, sourceModules, projectSources, _capabilityCatalog));
            var templateIds = page.Bindings
                .Select(item => PageDisplayTemplateContract.TryGetTemplateId(item.PropsJson))
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .Distinct()
                .ToList();
            var displayTemplates = templateIds.Count == 0
                ? new List<PageDisplayTemplate>()
                : await _db.Queryable<PageDisplayTemplate>()
                    .Where(item => templateIds.Contains(item.Id) && !item.IsDeleted && item.IsEnabled)
                    .ToListAsync();
            var generatedComponents = BuildComponents(layout.Root, page, fields, displayTemplates.ToDictionary(item => item.Id));
            var newTreeJson = JsonSerializer.Serialize(generatedComponents, JsonOptions);
            var treePath = Path.Combine(outputDirectory, $"{page.PageKey}.tree.json");
            var oldTreeJson = File.Exists(treePath) ? await File.ReadAllTextAsync(treePath) : string.Empty;
            var treeJson = oldTreeJson.Length == 0
                ? newTreeJson
                : incremental
                    ? _treeMerge.MergeIncremental(newTreeJson, oldTreeJson, BuildChanges(generatedComponents))
                    : _treeMerge.MergeOrder(newTreeJson, oldTreeJson);
            var designPath = Path.Combine(outputDirectory, $"{page.PageKey}.design.json");
            if (File.Exists(designPath))
            {
                try
                {
                    var designDocument = JsonSerializer.Deserialize<ProjectUiDesignDocumentDto>(
                        await File.ReadAllTextAsync(designPath), JsonOptions);
                    if (designDocument is { Operations.Count: > 0 })
                        treeJson = ProjectUiDesignService.ApplyDesignDocument(treeJson, designDocument, out _);
                }
                catch (JsonException)
                {
                }
                catch (ArgumentException)
                {
                }
            }

            var tree = JsonSerializer.Deserialize<List<Component>>(treeJson, JsonOptions)
                ?? throw new InvalidOperationException("Generated component tree is invalid.");
            var template = new VueTemplateSerializer().SerializeWithWrapper(tree);
            var generatedVueContent = BuildVueContent(page, template);
            var regionProtector = new GeneratedVueRegionProtector();
            GeneratedVueRegionAnalysis regionAnalysis;
            if (incremental && File.Exists(existingVuePath))
            {
                var currentVueContent = await File.ReadAllTextAsync(existingVuePath);
                var sidecarPath = GeneratedVueRegionProtector.GetSidecarPath(existingVuePath);
                var sidecarJson = File.Exists(sidecarPath) ? await File.ReadAllTextAsync(sidecarPath) : null;
                regionAnalysis = regionProtector.Analyze(
                    generatedVueContent,
                    currentVueContent,
                    sidecarJson,
                    page.LastGeneratedVueHash);
            }
            else
            {
                regionAnalysis = regionProtector.Analyze(generatedVueContent);
            }
            var vueContent = regionAnalysis.Content;
            result.HasConflicts = regionAnalysis.HasConflicts;
            result.Conflicts = regionAnalysis.Conflicts.Select(item => new GeneratedRegionConflictDto
            {
                GenId = item.GenId,
                Reason = item.Reason
            }).ToList();
            var autoContent = BuildAutoScriptV2(page, apiDefinitions);
            var pageJson = JsonSerializer.Serialize(page, JsonOptions);
            var scriptJsonPath = Path.Combine(outputDirectory, $"{page.PageKey}.script.json");
            var fieldsJsonPath = Path.Combine(outputDirectory, $"{page.PageKey}.fields.json");
            var scriptJson = File.Exists(scriptJsonPath) ? await File.ReadAllTextAsync(scriptJsonPath) : "{\"version\":1,\"sections\":[]}";
            var fieldsJson = File.Exists(fieldsJsonPath) ? await File.ReadAllTextAsync(fieldsJsonPath) : "{}";
            var designJson = File.Exists(designPath) ? await File.ReadAllTextAsync(designPath) : "{\"version\":1,\"operations\":[]}";

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["index.vue"] = vueContent,
                ["index.vue.genhash.json"] = regionAnalysis.SidecarJson,
                [$"{page.PageKey}.auto.js"] = autoContent,
                [$"{page.PageKey}.page.json"] = pageJson,
                [$"{page.PageKey}.tree.json"] = treeJson,
                [$"{page.PageKey}.script.json"] = scriptJson,
                [$"{page.PageKey}.fields.json"] = fieldsJson,
                [$"{page.PageKey}.design.json"] = designJson
            };
            result.BackupDirectory = await WriteBundleAsync(outputDirectory, files, result);

            var vueHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(vueContent))).ToLowerInvariant();
            result.VueHash = vueHash;
            var conflictError = result.HasConflicts
                ? "Generated Vue regions contain external changes: " + string.Join(
                    "; ",
                    result.Conflicts.Select(item => $"{item.GenId} ({item.Reason})"))
                : null;
            await _db.Updateable<ProjectPage>()
                .SetColumns(item => new ProjectPage
                {
                    SyncStatus = result.HasConflicts ? PageSyncStatus.Conflict : PageSyncStatus.Generated,
                    LastGeneratedTime = DateTime.UtcNow,
                    LastGeneratedVueHash = result.HasConflicts ? page.LastGeneratedVueHash : vueHash,
                    LastError = conflictError,
                    TargetDirectory = outputDirectory
                })
                .Where(item => item.Id == page.Id)
                .ExecuteCommandAsync();

            result.Success = !result.HasConflicts;
            result.Error = conflictError;
            result.VuePath = Path.Combine(outputDirectory, "index.vue");
            result.TreePath = treePath;
            result.Files = files.Keys.Select(name => Path.Combine(outputDirectory, name)).ToList();
            if (page.IsMenuSynced)
            {
                try
                {
                    await SynchronizeMenuRouteAsync(page, outputDirectory);
                }
                catch (Exception routeEx)
                {
                    result.Error = $"Page generated, but route synchronization failed: {routeEx.Message}";
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            await MarkGenerationFailedAsync(page.Id, ex.Message);
            result.Error = ex.Message;
            result.Success = false;
            result.OutputDirectory = outputDirectory ?? string.Empty;
            return result;
        }
    }

    private static List<Component> BuildComponents(
        PageLayoutNode root,
        ProjectPageDto page,
        IReadOnlyList<EntityField> fields,
        IReadOnlyDictionary<long, PageDisplayTemplate> displayTemplates)
    {
        var bindingMap = page.Bindings
            .Where(item => item.BindingRole != PageBindingRole.Event)
            .ToDictionary(item => item.ComponentId, StringComparer.OrdinalIgnoreCase);
        var eventBindings = page.Bindings
            .Where(item => item.BindingRole == PageBindingRole.Event)
            .ToLookup(item => item.ComponentId, StringComparer.OrdinalIgnoreCase);
        var sourceMap = page.DataSources.ToDictionary(item => item.Id);
        var fieldMap = fields.ToDictionary(item => item.Id);
        var visibilityTargets = ComplexPageEventContract.GetVisibilityTargets(root);
        return new List<Component> { BuildComponent(root, bindingMap, eventBindings, sourceMap, fieldMap, displayTemplates, visibilityTargets, false) };
    }

    private static Component BuildComponent(
        PageLayoutNode node,
        IReadOnlyDictionary<string, PageComponentBindingDto> bindingMap,
        ILookup<string, PageComponentBindingDto> eventBindings,
        IReadOnlyDictionary<long, PageDataSourceDto> sourceMap,
        IReadOnlyDictionary<long, EntityField> fieldMap,
        IReadOnlyDictionary<long, PageDisplayTemplate> displayTemplates,
        IReadOnlySet<string> visibilityTargets,
        bool inSearchQuery)
    {
        var inQueryArea = inSearchQuery || node.Type.Equals("search", StringComparison.OrdinalIgnoreCase) ||
                          node.Type.Equals("query-form", StringComparison.OrdinalIgnoreCase);
        bindingMap.TryGetValue(node.ComponentId, out var binding);
        sourceMap.TryGetValue(binding?.DataSourceId ?? node.DataSourceId ?? 0, out var source);
        fieldMap.TryGetValue(binding?.FieldId ?? node.FieldId ?? 0, out var field);
        var component = new Component
        {
            Id = node.ComponentId,
            Tag = ResolveTag(node, binding),
            GenId = binding?.GeneratedGenId ?? node.GenId ?? $"gen_component_{node.ComponentId}",
            DataSourceId = node.DataSourceId ?? binding?.DataSourceId,
            BindingId = node.BindingId ?? binding?.BindingId,
            FieldId = node.FieldId ?? binding?.FieldId,
            Origin = node.Origin switch
            {
                PageNodeOrigin.External => ComponentOriginKind.External,
                PageNodeOrigin.User => ComponentOriginKind.User,
                _ => ComponentOriginKind.Generated
            },
            Children = node.Children.Count == 0
                ? null
                : node.Children.Select(child => BuildComponent(child, bindingMap, eventBindings, sourceMap, fieldMap, displayTemplates, visibilityTargets, inQueryArea)).ToList(),
            Props = new List<ComponentProp>(),
            Instructions = new List<ComponentInstruction>(),
            Events = new List<ComponentEvent>()
        };

        foreach (var prop in node.Props.Where(item => ShouldRenderNodeProp(node, item.Key)))
            component.Props.Add(new ComponentProp { Key = prop.Key, Value = prop.Value, IsBind = false, IsSingle = prop.Value == null });
        if (node.Type.Equals("row", StringComparison.OrdinalIgnoreCase) && !component.Props.Any(prop => prop.Key == "gutter"))
            component.Props.Add(new ComponentProp { Key = "gutter", Value = "16" });
        if (node.Type.Equals("query-form", StringComparison.OrdinalIgnoreCase))
            UpsertProp(component.Props, "inline", "true");
        if (node.Type.Equals("column", StringComparison.OrdinalIgnoreCase))
        {
            var grid = node.Grid ?? new PageGridSpec { Span = node.Span ?? 24 };
            UpsertProp(component.Props, "span", grid.Span.ToString());
            UpsertProp(component.Props, "xs", grid.Xs?.ToString());
            UpsertProp(component.Props, "sm", grid.Sm?.ToString());
            UpsertProp(component.Props, "md", grid.Md?.ToString());
            UpsertProp(component.Props, "lg", grid.Lg?.ToString());
        }

        ApplyBinding(component, node, binding, source, field, inQueryArea);
        ApplyNodePresentation(component, node, visibilityTargets);
        if (node.Type.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var eventBinding in eventBindings[node.ComponentId])
            {
                if (!sourceMap.TryGetValue(eventBinding.DataSourceId, out var eventSource)) continue;
                component.Events!.Add(new ComponentEvent
                {
                    Name = "row-click",
                    Body = $"reloadDataSource({ToJavaScriptString(eventSource.SourceKey)}, {{}}, {{ row }})"
                });
            }
        }
        if (node.Type.Equals("query-form", StringComparison.OrdinalIgnoreCase))
        {
            var pagedSource = bindingMap.Values
                .Where(item => item.BindingRole == PageBindingRole.Field && IsDescendant(node, item.ComponentId))
                .Select(item => sourceMap.TryGetValue(item.DataSourceId, out var querySource) ? querySource : null)
                .FirstOrDefault(item => item?.DataShape == PageDataShape.Paged);
            if (pagedSource != null)
            {
                component.Events!.Add(new ComponentEvent
                {
                    Name = "submit.prevent",
                    Body = $"() => reloadDataSource({ToJavaScriptString(pagedSource.SourceKey)}, {{ pageNum: 1 }})"
                });
            }
        }
        MergeRowClickEvents(component);
        ApplyConfiguredEvents(component, node);
        ApplyDisplayTemplate(component, node, binding, source, field, inQueryArea, displayTemplates);
        if (component.Props.Count == 0) component.Props = null;
        if (component.Instructions.Count == 0) component.Instructions = null;
        if (component.Events.Count == 0) component.Events = null;
        return component;
    }

    private static bool ShouldRenderNodeProp(PageLayoutNode node, string key)
        => !key.StartsWith('_') &&
           !(key.Equals("text", StringComparison.OrdinalIgnoreCase) &&
             node.Type is "button" or "text");

    private static void ApplyNodePresentation(Component component, PageLayoutNode node, IReadOnlySet<string> visibilityTargets)
    {
        if (node.Type.Equals("button", StringComparison.OrdinalIgnoreCase))
            component.Content = node.Props.GetValueOrDefault("text") ?? "按钮";
        else if (node.Type.Equals("text", StringComparison.OrdinalIgnoreCase))
            component.Content = node.Props.GetValueOrDefault("text") ?? "文本";

        if (node.Type.Equals("dialog", StringComparison.OrdinalIgnoreCase))
        {
            component.Instructions!.Add(new ComponentInstruction
            {
                Name = "v-model",
                Value = $"state.ui[{ToJavaScriptString(node.ComponentId)}]"
            });
            UpsertProp(component.Props!, "title", node.Props.GetValueOrDefault("title") ?? "对话框");
            UpsertProp(component.Props!, "width", node.Props.GetValueOrDefault("width") ?? "680px");
        }
        else if (visibilityTargets.Contains(node.ComponentId))
        {
            component.Instructions!.Add(new ComponentInstruction
            {
                Name = "v-show",
                Value = $"state.ui[{ToJavaScriptString(node.ComponentId)}] !== false"
            });
        }
    }

    private static void ApplyConfiguredEvents(Component component, PageLayoutNode node)
    {
        foreach (var definition in ComplexPageEventContract.Read(node))
        {
            var actionsJson = JsonSerializer.Serialize(definition.Actions, JsonOptions);
            var encodedActions = Convert.ToBase64String(Encoding.UTF8.GetBytes(actionsJson));
            var actionHandler = $"event => runPageActions(decodePageActions('{encodedActions}'), event)";
            var existing = component.Events!.FirstOrDefault(item =>
                string.Equals(item.Name, definition.EventName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                component.Events!.Add(new ComponentEvent { Name = definition.EventName, Body = actionHandler });
                continue;
            }

            var existingHandler = existing.Body ?? existing.Expression;
            existing.Expression = null;
            existing.Body = string.IsNullOrWhiteSpace(existingHandler)
                ? actionHandler
                : $"async event => {{ await ({existingHandler})(event); await runPageActions(decodePageActions('{encodedActions}'), event); }}";
        }
    }

    private static void ApplyDisplayTemplate(
        Component component,
        PageLayoutNode node,
        PageComponentBindingDto? binding,
        PageDataSourceDto? source,
        EntityField? field,
        bool inQueryArea,
        IReadOnlyDictionary<long, PageDisplayTemplate> displayTemplates)
    {
        var templateId = PageDisplayTemplateContract.TryGetTemplateId(binding?.PropsJson);
        if (!templateId.HasValue || !displayTemplates.TryGetValue(templateId.Value, out var template)) return;
        if (template.Scope == PageDisplayTemplateScope.Container)
        {
            PageDisplayTemplateRenderer.ApplyContainer(component, template);
            return;
        }
        if (binding == null || source == null || field == null)
            throw new InvalidOperationException($"字段显示模板 '{template.TemplateKey}' 需要字段绑定、数据源和字段元数据。");
        PageDisplayTemplateRenderer.ApplyField(component, binding, field, source, inQueryArea, template);
    }

    private static void ApplyBinding(
        Component component,
        PageLayoutNode node,
        PageComponentBindingDto? binding,
        PageDataSourceDto? source,
        EntityField? field,
        bool inSearchQuery)
    {
        if (binding?.BindingStatus == PageBindingStatus.Invalid)
        {
            component.Tag = "el-alert";
            component.Children = null;
            UpsertProp(component.Props!, "title", $"Invalid binding: {binding.BindingId}");
            UpsertProp(component.Props!, "type", "warning");
            return;
        }
        if (source == null) return;

        var sourceKeyLiteral = ToJavaScriptString(source.SourceKey);
        var sourceAccess = $"state.data[{sourceKeyLiteral}]";
        var fieldName = field == null ? null : ToCamelCase(field.Name);
        var label = binding?.LabelOverride ?? field?.Description ?? field?.Name;
        switch (binding?.BindingRole)
        {
            case PageBindingRole.Table:
                UpsertBoundProp(component.Props!, "data", source.DataShape == PageDataShape.Paged
                    ? $"{sourceAccess}?.items || []"
                    : $"{sourceAccess} || []");
                component.Instructions!.Add(new ComponentInstruction { Name = "v-loading", Value = $"state.sourceStatus[{sourceKeyLiteral}]?.loading" });
                break;
            case PageBindingRole.Column when fieldName != null:
                UpsertProp(component.Props!, "prop", fieldName);
                UpsertProp(component.Props!, "label", label ?? fieldName);
                break;
            case PageBindingRole.Form:
                UpsertBoundProp(component.Props!, "model", sourceAccess);
                break;
            case PageBindingRole.Field when fieldName != null:
                UpsertProp(component.Props!, "label", label ?? fieldName);
                if (inSearchQuery)
                {
                    component.Children ??= new List<Component>();
                    if (!HasFieldControl(component.Children))
                        component.Children.Add(BuildFieldControl(binding, field!, $"state.query[{ToJavaScriptString(fieldName)}]"));
                }
                else
                {
                    UpsertProp(component.Props!, "prop", fieldName);
                    component.Children ??= new List<Component>();
                    if (component.Children.Count == 0)
                        component.Children.Add(BuildFieldControl(binding, field!, $"{sourceAccess}.{fieldName}"));
                }
                break;
            case PageBindingRole.Detail when fieldName != null:
                UpsertProp(component.Props!, "label", label ?? fieldName);
                if (node.Type.Equals("detail-field", StringComparison.OrdinalIgnoreCase) ||
                    node.Type.Equals("description-field", StringComparison.OrdinalIgnoreCase))
                    component.Content = $"{{{{ {sourceAccess}?.{fieldName} ?? '-' }}}}";
                break;
        }

        if (node.Type.Equals("pagination", StringComparison.OrdinalIgnoreCase) && source.DataShape == PageDataShape.Paged)
        {
            UpsertBoundProp(component.Props!, "total", $"{sourceAccess}?.total || 0");
            UpsertBoundProp(component.Props!, "current-page", $"{sourceAccess}?.pageNum || 1");
            UpsertBoundProp(component.Props!, "page-size", $"{sourceAccess}?.pageSize || 20");
            UpsertBoundProp(component.Props!, "page-sizes", "[10, 20, 50, 100]");
            UpsertProp(component.Props!, "layout", "total, sizes, prev, pager, next, jumper");
            component.Events!.Add(new ComponentEvent
            {
                Name = "current-change",
                Body = $"page => reloadDataSource({sourceKeyLiteral}, {{ pageNum: page }})"
            });
            component.Events.Add(new ComponentEvent
            {
                Name = "size-change",
                Body = $"pageSize => reloadDataSource({sourceKeyLiteral}, {{ pageNum: 1, pageSize }})"
            });
        }

        if (binding != null)
            ApplyPropsJson(component.Props!, binding.PropsJson);
    }

    private static void ApplyPropsJson(List<ComponentProp> props, string? propsJson)
    {
        if (string.IsNullOrWhiteSpace(propsJson) || propsJson == "{}") return;
        using var document = JsonDocument.Parse(propsJson);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var prop = new ComponentProp { Key = property.Name };
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.True:
                    prop.IsSingle = true;
                    break;
                case JsonValueKind.False:
                case JsonValueKind.Number:
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                    prop.IsBind = true;
                    prop.Value = property.Value.GetRawText();
                    break;
                case JsonValueKind.String:
                    prop.Value = EscapeAttribute(property.Value.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Null:
                    prop.IsBind = true;
                    prop.Value = "null";
                    break;
                default:
                    continue;
            }
            var existing = props.FindIndex(item => string.Equals(item.Key, prop.Key, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) props[existing] = prop;
            else props.Add(prop);
        }
    }

    private static string EscapeAttribute(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static void MergeRowClickEvents(Component component)
    {
        var rowClicks = component.Events?
            .Where(evt => evt.Name.Equals("row-click", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rowClicks == null || rowClicks.Count == 0) return;
        if (rowClicks.Count == 1)
        {
            rowClicks[0].Body = $"row => {rowClicks[0].Body}";
            return;
        }

        var first = rowClicks[0];
        foreach (var extra in rowClicks.Skip(1))
            component.Events!.Remove(extra);
        first.Body = $"row => {{ {string.Join("; ", rowClicks.Select(evt => evt.Body))} }}";
    }

    private static bool HasFieldControl(IReadOnlyList<Component> children)
        => children.Any(child => child.Tag?.Equals("el-input", StringComparison.OrdinalIgnoreCase) == true
            || child.Tag?.Equals("el-input-number", StringComparison.OrdinalIgnoreCase) == true
            || child.Tag?.Equals("el-select", StringComparison.OrdinalIgnoreCase) == true
            || child.Tag?.Equals("el-switch", StringComparison.OrdinalIgnoreCase) == true
            || child.Tag?.Equals("el-date-picker", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsDescendant(PageLayoutNode root, string componentId)
        => root.Children.Any(child => string.Equals(child.ComponentId, componentId, StringComparison.OrdinalIgnoreCase) || IsDescendant(child, componentId));

    private static Component BuildFieldControl(PageComponentBindingDto binding, EntityField field, string modelExpression)
    {
        var semantic = FieldSemanticMapper.Map(field);
        var controlType = (binding.ControlType ?? semantic.ControlType ?? "input").ToLowerInvariant();
        var tag = controlType switch
        {
            "number" or "input-number" => "el-input-number",
            "switch" => "el-switch",
            "date" or "datetime" => "el-date-picker",
            "select" or "select-enum" or "select-table" => "el-select",
            _ => "el-input"
        };
        var control = new Component
        {
            Id = $"{binding.ComponentId}_control",
            Tag = tag,
            GenId = $"{binding.GeneratedGenId}_control",
            Origin = ComponentOriginKind.Generated,
            Instructions = new List<ComponentInstruction>
            {
                new() { Name = "v-model", Value = modelExpression }
            },
            Props = new List<ComponentProp>()
        };
        if (controlType == "datetime") UpsertProp(control.Props, "type", "datetime");
        return control;
    }

    private static ComponentTreeChangeSet BuildChanges(IEnumerable<Component> components)
    {
        var changes = new ComponentTreeChangeSet();
        foreach (var component in Enumerate(components))
        {
            if (!string.IsNullOrWhiteSpace(component.GenId))
                changes.ChangedGeneratedIds.Add(component.GenId!);
        }
        return changes;
    }

    private static IEnumerable<Component> Enumerate(IEnumerable<Component> components)
    {
        foreach (var component in components)
        {
            yield return component;
            if (component.Children != null)
                foreach (var child in Enumerate(component.Children)) yield return child;
            if (component.UseSlots != null)
                foreach (var slot in component.UseSlots)
                    if (slot.Components != null)
                        foreach (var child in Enumerate(slot.Components)) yield return child;
        }
    }

    private static string ResolveTag(PageLayoutNode node, PageComponentBindingDto? binding) => binding?.BindingRole switch
    {
        PageBindingRole.Column => "el-table-column",
        PageBindingRole.Field => "el-form-item",
        PageBindingRole.Detail when node.Type.Equals("detail-field", StringComparison.OrdinalIgnoreCase) => "el-descriptions-item",
        _ => node.Type.ToLowerInvariant() switch
        {
            "page" => "div",
            "row" => "el-row",
            "column" => "el-col",
            "search" => "el-card",
            "table" => "el-table",
            "table-column" => "el-table-column",
            "pagination" => "el-pagination",
            "form" => "el-form",
            "form-field" => "el-form-item",
            "detail" or "description" => "el-descriptions",
            "detail-field" => "el-descriptions-item",
            "description-field" => "el-descriptions-item",
            "edit-form" or "query-form" => "el-form",
            "card" => "el-card",
            "button" => "el-button",
            "dialog" => "el-dialog",
            "text" => "div",
            "component" => string.IsNullOrWhiteSpace(node.Tag) ? "div" : node.Tag!,
            _ => string.IsNullOrWhiteSpace(node.Tag) ? "div" : node.Tag!
        }
    };

    private static GeneratedApiDefinition BuildApiDefinition(
        PageDataSourceDto source,
        IReadOnlyList<ModuleEntity> entities,
        IReadOnlyList<ProjectModule> modules,
        IReadOnlyList<ProjectDataSource> projectSources,
        IApplicationServiceCapabilityCatalog? capabilityCatalog)
    {
        if (source.SourceType == PageDataSourceType.Api)
        {
            if (source.ProjectDataSourceId.HasValue)
            {
                var projectSource = projectSources.FirstOrDefault(item => item.Id == source.ProjectDataSourceId.Value)
                    ?? throw new InvalidOperationException(
                        $"Imported API data source {source.ProjectDataSourceId} for '{source.SourceKey}' was not found.");
                if (!projectSource.OpenApiDocumentId.HasValue || !projectSource.IsEnabled ||
                    projectSource.OpenApiSyncState is not (OpenApiSyncState.Active or OpenApiSyncState.Detached))
                    throw new InvalidOperationException(
                        $"Imported API data source '{projectSource.SourceKey}' is not active.");
                if (!string.Equals(projectSource.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Imported API data source '{projectSource.SourceKey}' uses unsupported HTTP method {projectSource.HttpMethod}.");
                if (string.IsNullOrWhiteSpace(projectSource.RoutePath))
                    throw new InvalidOperationException(
                        $"Imported API data source '{projectSource.SourceKey}' has no route path.");
                var importedRouteParameters = Regex.Matches(projectSource.RoutePath, "\\{(?<name>[^}/]+)\\}")
                    .Select(match => match.Groups["name"].Value)
                    .ToArray();
                if (importedRouteParameters.Length > 1)
                    throw new InvalidOperationException(
                        $"Imported API data source '{projectSource.SourceKey}' uses more than one route parameter.");
                if (importedRouteParameters.Length == 1 &&
                    !projectSource.RoutePath.EndsWith($"/{{{importedRouteParameters[0]}}}", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Imported API data source '{projectSource.SourceKey}' requires its route parameter to be the final path segment.");
                var importedRoutePath = importedRouteParameters.Length == 0
                    ? projectSource.RoutePath
                    : Regex.Replace(projectSource.RoutePath, "/\\{[^}]+\\}$", string.Empty, RegexOptions.IgnoreCase);
                return new GeneratedApiDefinition(importedRoutePath, projectSource.HttpMethod!, importedRouteParameters);
            }

            var method = capabilityCatalog?.FindMethod(source.ServiceName, source.MethodName)
                ?? throw new InvalidOperationException(
                    $"API data source '{source.SourceKey}' requires registered capability {source.ServiceName}.{source.MethodName}.");
            if (!string.Equals(method.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"API data source '{source.SourceKey}' uses unsupported HTTP method {method.HttpMethod}.");
            var routeParameters = method.Parameters.Where(item => item.IsRouteParameter).Select(item => item.Name).ToArray();
            if (routeParameters.Length > 1)
                throw new InvalidOperationException(
                    $"API data source '{source.SourceKey}' uses more than one route parameter; split it into a dedicated query API first.");
            if (routeParameters.Length == 1 &&
                !method.RoutePath.EndsWith($"/{{{routeParameters[0]}}}", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"API data source '{source.SourceKey}' requires its route parameter to be the final path segment.");
            var routePath = routeParameters.Length == 0
                ? method.RoutePath
                : Regex.Replace(method.RoutePath, "/\\{[^}]+\\}$", string.Empty, RegexOptions.IgnoreCase);
            return new GeneratedApiDefinition(
                routePath,
                method.HttpMethod,
                routeParameters);
        }

        var entity = entities.FirstOrDefault(item => item.Id == source.EntityId)
            ?? throw new InvalidOperationException($"Entity {source.EntityId} for data source '{source.SourceKey}' was not found.");
        var module = modules.FirstOrDefault(item => item.Id == entity.ModuleId)
            ?? throw new InvalidOperationException($"Module {entity.ModuleId} for entity '{entity.Name}' was not found.");
        var methodName = NormalizeMethodRoute(source.MethodName);
        var pathParameters = methodName.Equals("getbyid", StringComparison.OrdinalIgnoreCase)
            ? new[] { "id" }
            : Array.Empty<string>();
        return new GeneratedApiDefinition(
            $"/{CodeGenNaming.ToPathSegment(module.ModuleName)}/{CodeGenNaming.ToPathSegment(entity.Name)}/{methodName}",
            "GET",
            pathParameters);
    }

    private static string NormalizeMethodRoute(string methodName)
    {
        var route = methodName.EndsWith("Async", StringComparison.OrdinalIgnoreCase)
            ? methodName[..^5]
            : methodName;
        if (route.Equals("GetPageList", StringComparison.OrdinalIgnoreCase)) route = "GetPagedList";
        return route.ToLowerInvariant();
    }

    private static void UpsertProp(List<ComponentProp> props, string key, string? value)
    {
        if (value == null) return;
        var existing = props.FirstOrDefault(prop => string.Equals(prop.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null) props.Add(new ComponentProp { Key = key, Value = value });
        else existing.Value = value;
    }

    private static void UpsertBoundProp(List<ComponentProp> props, string key, string value)
    {
        var existing = props.FirstOrDefault(prop => string.Equals(prop.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            props.Add(new ComponentProp { Key = key, Value = value, IsBind = true });
        else
        {
            existing.Value = value;
            existing.IsBind = true;
            existing.IsSingle = false;
        }
    }

    private static string ToCamelCase(string value)
        => string.IsNullOrWhiteSpace(value) || char.IsLower(value[0]) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string ToJavaScriptString(string value)
        => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string BuildVueContent(ProjectPageDto page, string template)
    {
        var pageFile = CodeGenNaming.ToPathSegment(page.PageKey);
        var layout = PageLayoutTree.Parse(page.LayoutJson);
        var visibilityTargets = ComplexPageEventContract.GetVisibilityTargets(layout.Root);
        var initialUiState = EnumerateLayoutNodes(layout.Root)
            .Where(node => visibilityTargets.Contains(node.ComponentId) ||
                           node.Type.Equals("dialog", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                node => node.ComponentId,
                node => !node.Type.Equals("dialog", StringComparison.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        var initialUiJson = JsonSerializer.Serialize(initialUiState, JsonOptions);
        return $$"""
            {{template}}
            <script setup>
            import { reactive, onMounted } from 'vue'
            import { useRoute, useRouter } from 'vue-router'
            import { ElMessage } from 'element-plus'
            import { createComplexPageState, loadComplexPageData, reloadComplexPageDataSource } from './{{pageFile}}.auto.js'

            const route = useRoute()
            const router = useRouter()
            const state = reactive(createComplexPageState())
            Object.assign(state.ui, {{initialUiJson}})

            function buildDataContext(event = {}) {
              return {
                route: { ...route.params, ...route.query },
                page: state.params,
                query: state.query,
                event,
                source: state.data
              }
            }

            function getActionValue(value, event = {}) {
              if (typeof value !== 'string' || !value.startsWith('$')) return value
              const segments = value.split('.')
              const scope = segments.shift().slice(1)
              return segments.reduce((current, key) => current == null ? undefined : current[key], buildDataContext(event)[scope])
            }

            function decodePageActions(value) {
              const bytes = Uint8Array.from(atob(value), character => character.charCodeAt(0))
              return JSON.parse(new TextDecoder().decode(bytes))
            }

            async function loadData() {
              await loadComplexPageData(state, buildDataContext())
            }

            async function reloadDataSource(sourceKey, paging = {}, event = {}) {
              Object.assign(state.data[sourceKey] || {}, paging)
              await reloadComplexPageDataSource(state, sourceKey, buildDataContext(event))
            }

            async function runPageActions(actions, event = {}) {
              for (const action of actions || []) {
                if (action.type === 'stop') break
                if (action.type === 'refreshDataSource') await reloadDataSource(action.sourceKey, {}, event)
                else if (action.type === 'navigate') await router.push(getActionValue(action.path, event))
                else if (action.type === 'showMessage') (ElMessage[action.messageType || 'success'] || ElMessage.info)(getActionValue(action.message, event) || '')
                else if (action.type === 'setPageParam') state.params[action.key] = getActionValue(action.value, event)
                else if (action.type === 'setQueryParam') state.query[action.key] = getActionValue(action.value, event)
                else if (action.type === 'resetQuery') { Object.keys(state.query).forEach(key => delete state.query[key]); await loadData() }
                else if (action.type === 'openDialog') state.ui[action.targetComponentId] = true
                else if (action.type === 'closeDialog') state.ui[action.targetComponentId] = false
                else if (action.type === 'toggleComponent') state.ui[action.targetComponentId] = !(state.ui[action.targetComponentId] !== false)
              }
            }

            onMounted(loadData)
            </script>

            """;
    }

    private static IEnumerable<PageLayoutNode> EnumerateLayoutNodes(PageLayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in EnumerateLayoutNodes(child))
                yield return descendant;
    }

    private static string BuildAutoScript(ProjectPageDto page, IReadOnlyDictionary<long, GeneratedApiDefinition> apiDefinitions)
    {
        var sources = page.DataSources.Where(item => item.IsEnabled).OrderBy(item => item.OrderNum).Select(source =>
        {
            var api = apiDefinitions[source.Id];
            var loadOnInit = !ContainsEventReference(source.ParamsJson);
            return $"  {{ key: {JsonSerializer.Serialize(source.SourceKey)}, url: {JsonSerializer.Serialize(api.Url)}, method: {JsonSerializer.Serialize(api.HttpMethod.ToLowerInvariant())}, shape: {JsonSerializer.Serialize(source.DataShape.ToString())}, pathParameter: {JsonSerializer.Serialize(api.PathParameters.FirstOrDefault())}, loadOnInit: {loadOnInit.ToString().ToLowerInvariant()}, params: {NormalizeJson(source.ParamsJson)} }}";
        }).ToList();
        var sourceJson = string.Join(",\n", sources);
        return $"import request from '@/utils/request'\n\nconst dataSources = [\n{sourceJson}\n]\n\nfunction defaultValue(shape) {{\n  if (shape === 'Single') return {{}}\n  if (shape === 'Paged') return {{ items: [], total: 0, pageNum: 1, pageSize: 20 }}\n  if (shape === 'Aggregate') return {{}}\n  return []\n}}\n\nexport function createComplexPageState() {{\n  return {{\n    loading: false,\n    params: {{}},\n    query: {{}},\n    data: Object.fromEntries(dataSources.map(source => [source.key, defaultValue(source.shape)])),\n    sourceStatus: Object.fromEntries(dataSources.map(source => [source.key, {{ loading: false, error: null }}]))\n  }}\n}}\n\nfunction getPathValue(root, path) {{\n  return path.reduce((value, key) => value == null ? undefined : value[key], root)\n}}\n\nfunction resolveReference(reference, context) {{\n  const segments = reference.split('.')\n  const scope = segments.shift().slice(1)\n  return getPathValue(context[scope], segments)\n}}\n\nfunction resolveValue(value, context) {{\n  if (Array.isArray(value)) return value.map(item => resolveValue(item, context))\n  if (value && typeof value === 'object') {{\n    if (Object.keys(value).length === 1 && typeof value.$ref === 'string')\n      return resolveReference(value.$ref, context)\n    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, resolveValue(item, context)]))\n  }}\n  return value\n}}\n\nfunction normalizePayload(source, payload, previous) {{\n  const value = payload?.data ?? payload\n  if (source.shape === 'Single') return value ?? {{}}\n  if (source.shape === 'Collection' || source.shape === 'Options')\n    return Array.isArray(value) ? value : (value?.items ?? value?.records ?? value?.list ?? [])\n  if (source.shape === 'Paged') {{\n    return {{\n      items: value?.items ?? value?.records ?? value?.list ?? [],\n      total: value?.total ?? value?.totalCount ?? 0,\n      pageNum: value?.pageNum ?? value?.page ?? previous?.pageNum ?? 1,\n      pageSize: value?.pageSize ?? previous?.pageSize ?? 20\n    }}\n  }}\n  return value ?? {{}}\n}}\n\nasync function loadSource(state, source, context) {{\n  const status = state.sourceStatus[source.key] || (state.sourceStatus[source.key] = {{ loading: false, error: null }})\n  status.loading = true\n  status.error = null\n  try {{\n    const previous = state.data[source.key]\n    const resolved = resolveValue(source.params, {{ ...context, source: state.data }})\n    const params = {{ ...resolved, ...state.params, ...(context.query || {{}}) }}\n    if (source.shape === 'Paged') {{\n      params.pageNum = previous?.pageNum ?? params.pageNum ?? 1\n      params.pageSize = previous?.pageSize ?? params.pageSize ?? 20\n    }}\n    let url = source.url\n    if (source.pathParameter) {{\n      const pathValue = params[source.pathParameter]\n      if (pathValue === undefined || pathValue === null || pathValue === '')\n        throw new Error(`Data source ${{source.key}} requires parameter ${{source.pathParameter}}`)\n      url += `/${{encodeURIComponent(pathValue)}}`\n      delete params[source.pathParameter]\n    }}\n    const response = await request({{ url, method: source.method, params }})\n    state.data[source.key] = normalizePayload(source, response, previous)\n    return state.data[source.key]\n  }} catch (error) {{\n    status.error = error?.message || String(error)\n    throw error\n  }} finally {{\n    status.loading = false\n  }}\n}}\n\nexport async function loadComplexPageData(state, context = {{}}) {{\n  state.loading = true\n  try {{\n    for (const source of dataSources)\n      await loadSource(state, source, {{ ...context, source: state.data }})\n    return state.data\n  }} finally {{\n    state.loading = false\n  }}\n}}\n\nexport async function reloadComplexPageDataSource(state, sourceKey, context = {{}}) {{\n  const source = dataSources.find(item => item.key === sourceKey)\n  if (!source) throw new Error(`Unknown data source: ${{sourceKey}}`)\n  return loadSource(state, source, {{ ...context, source: state.data }})\n}}\n\nexport {{ dataSources }}\n";
    }

    private static string BuildAutoScriptV2(ProjectPageDto page, IReadOnlyDictionary<long, GeneratedApiDefinition> apiDefinitions)
    {
        var script = BuildAutoScript(page, apiDefinitions);
        script = script.Replace(
            "    query: {},\n    data:",
            "    query: {},\n    ui: {},\n    data:",
            StringComparison.Ordinal);
        return script.Replace(
            "    for (const source of dataSources)\n      await loadSource(state, source, { ...context, source: state.data })",
            "    for (const source of dataSources)\n      if (source.loadOnInit !== false)\n        await loadSource(state, source, { ...context, source: state.data })",
            StringComparison.Ordinal);
    }

    private static bool ContainsEventReference(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return ContainsEventReference(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsEventReference(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("$ref") &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    (property.Value.GetString() ?? string.Empty).StartsWith("$event.", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (ContainsEventReference(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsEventReference(item)) return true;
        }
        return false;
    }

    private sealed record GeneratedApiDefinition(
        string Url,
        string HttpMethod,
        IReadOnlyList<string> PathParameters);

    private static string NormalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static string ResolveOutputDirectory(Project project, ProjectModule module, string pageKey)
    {
        var root = ResolveProjectRootPath(project);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Project path is not configured.");
        var projectVue = Path.Combine(root, $"{project.ProjectName}.Vue");
        var moduleSegment = CodeGenNaming.ToPathSegment(module.ModuleName);
        var pageSegment = CodeGenNaming.ToPathSegment(pageKey);
        EnsureSafeSegment(moduleSegment, nameof(module.ModuleName));
        EnsureSafeSegment(pageSegment, nameof(pageKey));
        var output = Path.GetFullPath(Path.Combine(projectVue, "src", "views", moduleSegment, pageSegment));
        EnsureInside(projectVue, output);
        return output;
    }

    private static string ResolveProjectRootPath(Project project)
    {
        var projectPath = project.ProjectPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(projectPath)) return projectPath;
        if (projectPath.EndsWith(project.ProjectName, StringComparison.OrdinalIgnoreCase)) return projectPath;
        var nested = Path.Combine(projectPath, project.ProjectName);
        return Directory.Exists(nested) ? nested : projectPath;
    }

    private static void EnsureSafeSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidOperationException($"Unsafe path segment for {name}.");
    }

    private static void EnsureInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated output path escapes the project directory.");
    }

    private static async Task<string?> WriteBundleAsync(string outputDirectory, IReadOnlyDictionary<string, string> files, ComplexPageGenerationResultDto result)
    {
        var backupDirectory = files.Keys.Any(name => File.Exists(Path.Combine(outputDirectory, name)))
            ? Path.Combine(outputDirectory, ".codemaster-backup", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"))
            : null;
        if (backupDirectory != null) Directory.CreateDirectory(backupDirectory);
        var stagingDirectory = Path.Combine(outputDirectory, ".codemaster-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var file in files)
            {
                var stagingPath = Path.Combine(stagingDirectory, file.Key);
                await File.WriteAllTextAsync(stagingPath, file.Value, new UTF8Encoding(false));
                if (backupDirectory != null)
                {
                    var oldPath = Path.Combine(outputDirectory, file.Key);
                    if (File.Exists(oldPath)) await Task.Run(() => File.Copy(oldPath, Path.Combine(backupDirectory, file.Key), overwrite: true));
                }
            }
            foreach (var file in files.Keys)
            {
                var stagingPath = Path.Combine(stagingDirectory, file);
                var targetPath = Path.Combine(outputDirectory, file);
                File.Move(stagingPath, targetPath, overwrite: true);
            }
            return backupDirectory;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private async Task MarkGenerationFailedAsync(long pageId, string error)
    {
        try
        {
            var truncatedError = error[..Math.Min(error.Length, 2000)];
            await _db.Updateable<ProjectPage>()
                .SetColumns(item => new ProjectPage { SyncStatus = PageSyncStatus.GenerationFailed, LastError = truncatedError })
                .Where(item => item.Id == pageId)
                .ExecuteCommandAsync();
        }
        catch
        {
            // Preserve the original generation error if the metadata database is unavailable.
        }
    }

    private static async Task SynchronizeMenuRouteAsync(ProjectPageDto page, string outputDirectory)
    {
        var vueRoot = Path.GetFullPath(Path.Combine(outputDirectory, "..", "..", "..", ".."));
        var routerPath = Path.Combine(vueRoot, "src", "router", "index.js");
        if (!File.Exists(routerPath)) return;

        var relativePath = (page.RoutePath ?? string.Empty).TrimStart('/');
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        var routeName = $"ComplexPage{string.Concat(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment => string.Concat(segment.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]))))}";
        var viewsRelative = Path.GetRelativePath(Path.Combine(vueRoot, "src", "views"), outputDirectory);
        var componentPath = $"@/views/{viewsRelative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/')}/index.vue";
        var title = page.PageName ?? page.PageKey;
        var icon = page.Icon ?? "folder";
        var routeEntry = $"  {{\n    path: '{relativePath.Replace("'", "\\'", StringComparison.Ordinal)}',\n    name: '{routeName}',\n    component: () => import('{componentPath.Replace("'", "\\'", StringComparison.Ordinal)}'),\n    meta: {{ title: '{title.Replace("'", "\\'", StringComparison.Ordinal)}', icon: '{icon.Replace("'", "\\'", StringComparison.Ordinal)}' }}\n  }},";

        var lines = (await File.ReadAllLinesAsync(routerPath, new UTF8Encoding(false))).ToList();
        if (lines.Any(line => line.Trim().StartsWith("path: ", StringComparison.Ordinal) &&
                              line.Trim().Contains($"'{relativePath.Replace("'", "\\'", StringComparison.Ordinal)}'", StringComparison.Ordinal)))
            return;

        var anchorIndex = lines.FindIndex(line => line.Contains("EntityDesigner 路由由菜单数据库动态加载", StringComparison.Ordinal));
        if (anchorIndex >= 0)
        {
            lines.Insert(anchorIndex, routeEntry);
        }
        else
        {
            var layoutRootIndex = lines.FindIndex(line => line.Trim().StartsWith("path: '/'", StringComparison.Ordinal));
            if (layoutRootIndex < 0) return;
            var childrenStart = -1;
            for (var i = layoutRootIndex; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.EndsWith("[", StringComparison.Ordinal) || trimmed == "[")
                {
                    childrenStart = i;
                    break;
                }
            }
            if (childrenStart < 0) return;
            var depth = 0;
            var childrenEnd = -1;
            for (var i = childrenStart; i < lines.Count; i++)
            {
                depth += lines[i].Count(ch => ch == '[') - lines[i].Count(ch => ch == ']');
                if (depth <= 0 && lines[i].Trim() == "]")
                {
                    childrenEnd = i;
                    break;
                }
            }
            if (childrenEnd < 0) return;
            lines.Insert(childrenEnd, routeEntry);
        }
        await File.WriteAllLinesAsync(routerPath, lines, new UTF8Encoding(false));
    }
}
