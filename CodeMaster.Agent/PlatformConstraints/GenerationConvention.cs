namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// Metadata conventions that govern how the agent describes the five
/// generation artifacts: ordinary entities, view entities, complex pages,
/// child entities, and read-only entities.
/// </summary>
public sealed record GenerationConvention
{
    public IReadOnlyList<EntityTypeConvention> EntityTypes { get; init; } = new[]
    {
        new EntityTypeConvention(
            Name: "FullStack",
            When: "Normal business entity that needs database persistence plus a generated UI.",
            Mechanism: "Stores GenerationMode=0 (FullStack) on sys_module_entity. Code generation produces the database table, the backend CRUD service, and four frontend pages (index/add/edit/detail). Menu entries are emitted automatically.",
            SuitableFor: "Engineering project, labor contract, material inbound order, payment reconciliation. The default for any business object the user wants to manage."),
        new EntityTypeConvention(
            Name: "MetadataOnly",
            When: "An external-contract description; not a database table and not a managed entity.",
            Mechanism: "Stores GenerationMode=1 (MetadataOnly). Skipped by code generation: no table, no service, no menu, no page, no relations. Used to model shape for a third-party API.",
            SuitableFor: "External shape descriptors, payload templates for an upstream API, schemas that should never materialize as tables.")
    };

    public ViewMechanicsConvention ViewMechanics { get; init; } = new()
    {
        Description = "A database view is a read-only aggregation across one or more tables. CodeMaster stores its definition in the UiComposition module as a ProjectViewDefinition.",
        SqlRules = "SQL must contain exactly one read-only SELECT ... FROM ... statement. No INSERT/UPDATE/DELETE/DDL. Use the final migrated table names directly. Joins, aggregates, repeated aliases, computed expressions, and one-to-many joins are allowed.",
        ConsumerFieldMechanism = "Each read-only field consumed by the view is declared via EntityFields with GenerationMode=FullStack and a consumer marker. The Relations stage finalizes the foreign-key mapping for view consumers.",
        GenerationPipeline = "Views stage writes ProjectViewDefinition rows; the final materialization pipeline deploys the physical view to the target database together with entity generation."
    };

    public ComplexPageMechanicsConvention ComplexPageMechanics { get; init; } = new()
    {
        Description = "A complex page is a visually composed page (Dashboard, Login, or a complex report) built from bindings between components, data sources, and entity fields.",
        RequiredTables = new[]
        {
            "sys_project_page",
            "sys_page_component_binding",
            "sys_page_data_source",
            "sys_page_entity_dependency"
        },
        BindingMechanism = "Each component uses a stable component_id and binding_id. The binding row's binding_role classifies how the component consumes data; sync_mode controls whether new entity fields are picked up automatically.",
        DataSourceMechanism = "Data sources are referenced by data_source_id and source_key on sys_page_data_source; they may point at entity services, view services, or fixed dictionaries.",
        DependencyMechanism = "sys_page_entity_dependency records which entities the page consumes. The final materialization pipeline generates the page alongside ordinary entity code generation.",
        GenerationPipeline = "ComplexPages stage writes the four sys_page_* rows; the final materialization pipeline generates the page Vue source files together with entity generation."
    };

    public ChildEntityRulesConvention ChildEntityRules { get; init; } = new()
    {
        Description = "A child entity is a detail row inside a parent's page (e.g. material inbound inside a material, or labor payment inside a labor contract).",
        FrontendRule = "IsChildTable=true on sys_module_entity. The entity is NEVER rendered as a standalone page or a top-level menu entry.",
        InlineRule = "Children are edited inside the parent page's child card with a popup dialog. Generated code wires the dialog, add/edit/remove buttons, and the rowStatus=1/2/3 flag.",
        GenerationRule = "GenerateAggregateCodeAsync walks parent-child relations; the child is generated when its parent is generated."
    };

    public ReadOnlyEntityRulesConvention ReadOnlyEntityRules { get; init; } = new()
    {
        Description = "A read-only entity has no create/update/delete operations.",
        BackendRule = "IsReadOnly=true on sys_module_entity. The backend exposes only GetById (when HasPrimaryKey=true), paged list, query, and export. No POST/PUT/DELETE controllers.",
        FrontendRule = "The generated frontend exposes only the list page (no add/edit/detail pages, no toolbar add/delete buttons)."
    };

    public GenerationEntryPointsConvention EntryPoints { get; init; } = new()
    {
        EntityFieldsPipeline = "INSERT/UPDATE sys_module_entity, sys_entity_field, sys_entity_relation.",
        ViewsPipeline = "INSERT/UPDATE CodeMaster.UiComposition.Entities.ProjectViewDefinition. The final materialization pipeline deploys the physical view to the target database.",
        RelationsPipeline = "INSERT/UPDATE sys_one_to_many_relation and child entity references. The Relations stage finalizes types after Views so consumer foreign keys are correct.",
        FieldControlsPipeline = "INSERT/UPDATE per-field control configuration (FormControlType, RelatedEntity*, SelectOptions, etc.).",
        ComplexPagesPipeline = "INSERT/UPDATE sys_project_page + sys_page_component_binding + sys_page_data_source + sys_page_entity_dependency.",
        GenerateCodePipeline = "Call ModuleEntityService.GenerateCodeAsync / GenerateProjectSmartCodeAsync. Writes .cs/.vue/.json to ProjectPath.",
        BuildPipeline = "After GenerateCode the WebApi runs `dotnet build <ProjectName>.sln` and `cd <ProjectName>.Vue && npm run dev`. Build failures surface as a tool_failed event.",
        FinalMigrationTrigger = "Triggered by the user clicking Confirm; the Agent must NOT run EF migrations, start the WebApi, or boot the Vue dev server from inside the plan."
    };

    public string RenderForPrompt() =>
        $"""
        Generation conventions:
        Entity types:
        {string.Join("\n", EntityTypes.Select(t => $"- {t.Name}: when {t.When}. Mechanism: {t.Mechanism} SuitableFor: {t.SuitableFor}"))}

        View mechanics:
        - {ViewMechanics.Description}
        - SQL rule: {ViewMechanics.SqlRules}
        - Consumer fields: {ViewMechanics.ConsumerFieldMechanism}
        - Pipeline: {ViewMechanics.GenerationPipeline}

        Complex page mechanics:
        - {ComplexPageMechanics.Description}
        - Required tables: {string.Join(", ", ComplexPageMechanics.RequiredTables)}
        - Binding: {ComplexPageMechanics.BindingMechanism}
        - Data source: {ComplexPageMechanics.DataSourceMechanism}
        - Dependency: {ComplexPageMechanics.DependencyMechanism}
        - Pipeline: {ComplexPageMechanics.GenerationPipeline}

        Child entity rules:
        - {ChildEntityRules.Description}
        - Frontend: {ChildEntityRules.FrontendRule}
        - Inline: {ChildEntityRules.InlineRule}
        - Generation: {ChildEntityRules.GenerationRule}

        Read-only entity rules:
        - {ReadOnlyEntityRules.Description}
        - Backend: {ReadOnlyEntityRules.BackendRule}
        - Frontend: {ReadOnlyEntityRules.FrontendRule}

        Generation entry points (the Agent invokes these only via the matching stage task):
        - EntityFields: {EntryPoints.EntityFieldsPipeline}
        - Views: {EntryPoints.ViewsPipeline}
        - Relations: {EntryPoints.RelationsPipeline}
        - FieldControls: {EntryPoints.FieldControlsPipeline}
        - ComplexPages: {EntryPoints.ComplexPagesPipeline}
        - GenerateCode: {EntryPoints.GenerateCodePipeline}
        - Build: {EntryPoints.BuildPipeline}
        - Final migration: {EntryPoints.FinalMigrationTrigger}
        """;
}

public sealed record EntityTypeConvention(
    string Name,
    string When,
    string Mechanism,
    string SuitableFor);

public sealed record ViewMechanicsConvention
{
    public string Description { get; init; } = string.Empty;
    public string SqlRules { get; init; } = string.Empty;
    public string ConsumerFieldMechanism { get; init; } = string.Empty;
    public string GenerationPipeline { get; init; } = string.Empty;
}

public sealed record ComplexPageMechanicsConvention
{
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();
    public string BindingMechanism { get; init; } = string.Empty;
    public string DataSourceMechanism { get; init; } = string.Empty;
    public string DependencyMechanism { get; init; } = string.Empty;
    public string GenerationPipeline { get; init; } = string.Empty;
}

public sealed record ChildEntityRulesConvention
{
    public string Description { get; init; } = string.Empty;
    public string FrontendRule { get; init; } = string.Empty;
    public string InlineRule { get; init; } = string.Empty;
    public string GenerationRule { get; init; } = string.Empty;
}

public sealed record ReadOnlyEntityRulesConvention
{
    public string Description { get; init; } = string.Empty;
    public string BackendRule { get; init; } = string.Empty;
    public string FrontendRule { get; init; } = string.Empty;
}

public sealed record GenerationEntryPointsConvention
{
    public string EntityFieldsPipeline { get; init; } = string.Empty;
    public string ViewsPipeline { get; init; } = string.Empty;
    public string RelationsPipeline { get; init; } = string.Empty;
    public string FieldControlsPipeline { get; init; } = string.Empty;
    public string ComplexPagesPipeline { get; init; } = string.Empty;
    public string GenerateCodePipeline { get; init; } = string.Empty;
    public string BuildPipeline { get; init; } = string.Empty;
    public string FinalMigrationTrigger { get; init; } = string.Empty;
}