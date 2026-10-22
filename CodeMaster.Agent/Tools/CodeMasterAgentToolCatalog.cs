using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using Microsoft.Extensions.AI;

namespace CodeMaster.Agent.Tools;

public static class CodeMasterAgentToolCatalog
{
    public const string GetProjectSummary = "get_project_summary";
    public const string GetProjectStructure = "get_project_structure";
    public const string GetCodegenCatalog = "get_codegen_catalog";
    public const string GetEntityBlueprint = "get_entity_blueprint";
    public const string GetUiPageBlueprint = "get_ui_page_blueprint";
    public const string GetProjectDictionaries = "get_project_dictionaries";
    public const string ProposeProjectChangeSet = "propose_project_change_set";
    public const string ProposeDictionaryChange = "propose_dictionary_change";
    public const string ProposeUiPageEnhancement = "propose_ui_page_enhancement";
    public const string GetProjectWorkPlan = "get_project_work_plan";
    public const string GetProjectCompletionStatus = "get_project_completion_status";
    public const string GetProjectWorkTask = "get_project_work_task";
    public const string UpsertProjectWorkPlan = "upsert_project_work_plan";
    public const string SupersedeProjectWorkPlan = "supersede_project_work_plan";
    public const string SubmitBusinessPlan = "submit_business_plan";
    public const string GetViewBuilderContext = "get_view_builder_context";
    public const string GetUiCompositionContext = "get_ui_composition_context";
    public const string GetComplexPageContext = "get_complex_page_context";
    public const string ProposeProjectWorkBatch = "propose_project_work_batch";
    // Disabled on 2026-10-20: the agent never reads or patches CodeMaster source code.
    // The names stay as a record of the removed tools; nothing registers them any more.
    public const string InspectCodeMasterSource = "inspect_codemaster_source";
    public const string ApplyTestSourceRepair = "apply_test_source_repair";

    public static IList<AITool> Create(
        bool includeProjectSummary,
        Func<Task<string>> getProjectSummary,
        bool includeProjectStructure,
        Func<Task<string>> getProjectStructure,
        bool includeCodegenCatalog,
        Func<Task<string>> getCodegenCatalog,
        bool includeEntityBlueprint,
        Func<EntityBlueprintQuery, Task<string>> getEntityBlueprint,
        bool includeUiPageBlueprint,
        Func<UiPageBlueprintQuery, Task<string>> getUiPageBlueprint,
        bool includeDictionaryRead,
        Func<ProjectDictionaryQuery?, Task<string>> getProjectDictionaries,
        bool includeChangeTools,
        Func<ProjectChangeSetProposal, Task<string>> proposeProjectChangeSet,
        Func<UiPageEnhancementProposal, Task<string>> proposeUiPageEnhancement,
        bool includeDictionaryChange,
        Func<ProjectDictionaryChangeProposal, Task<string>> proposeDictionaryChange,
        bool includeWorkPlan = false,
        Func<Task<string>>? getProjectWorkPlan = null,
        Func<Task<string>>? getProjectCompletionStatus = null,
        Func<AiProjectWorkTaskQuery, Task<string>>? getProjectWorkTask = null,
        Func<UpsertAiProjectWorkPlanRequest, Task<string>>? upsertProjectWorkPlan = null,
        bool includeWorkBatch = false,
        Func<ProposeAiProjectWorkBatchRequest, Task<string>>? proposeProjectWorkBatch = null,
        Func<SupersedeAiProjectWorkPlanRequest, Task<string>>? supersedeProjectWorkPlan = null,
        bool includeSubmitBusinessPlan = false,
        Func<BusinessPlanDefinition, Task<string>>? submitBusinessPlan = null)
    {
        var tools = new List<AITool>();
        if (includeProjectSummary)
        {
            tools.Add(AIFunctionFactory.Create(
                getProjectSummary,
                name: GetProjectSummary,
                description: "Read a compact project overview containing modules, entities, concise field metadata, relations, and totals. projectStructureHash is a metadata cache fingerprint, NOT a work-plan revision. A restored conversation may receive a small notModified response and reuse its prior summary."));
        }

        if (includeProjectStructure)
        {
            tools.Add(AIFunctionFactory.Create(
                getProjectStructure,
                name: GetProjectStructure,
                description: "Read the complete and potentially large CodeMaster blueprint, including every field option and generation catalog. Use only for genuinely project-wide changes that cannot be resolved from the summary plus targeted entity blueprints."));
        }

        if (includeCodegenCatalog)
        {
            tools.Add(AIFunctionFactory.Create(
                getCodegenCatalog,
                name: GetCodegenCatalog,
                description: "Read the supported control types, page templates, and child templates without loading all project field metadata. The returned JSON also contains a platformConstraints block with the CodeMaster frontend/backend/control/workflow/generation conventions; consult that block before drafting plans or repair tasks."));
        }

        if (includeEntityBlueprint)
        {
            tools.Add(AIFunctionFactory.Create(
                getEntityBlueprint,
                name: GetEntityBlueprint,
                description: "Read one entity in full detail by entityId or entityName from the project bound to this conversation."));
        }

        if (includeUiPageBlueprint)
        {
            tools.Add(AIFunctionFactory.Create(
                getUiPageBlueprint,
                name: GetUiPageBlueprint,
                description: "Read the stable generated node identifiers for one entity page. Use this before proposing page layout, grouping, grid, visual property, movement, or compatible node-type changes."));
        }

        if (includeDictionaryRead)
        {
            // The reflected delegate defines the tool schema. Local forwarding
            // lambdas do not retain the server method's optional parameter, so
            // establish the contract here for both execution modes.
            Task<string> ReadDictionariesAsync(ProjectDictionaryQuery? query = null)
                => getProjectDictionaries(query ?? new ProjectDictionaryQuery());

            tools.Add(AIFunctionFactory.Create(
                ReadDictionariesAsync,
                name: GetProjectDictionaries,
                description: "Read dictionary types and optional entries from the generated project's database. The query object is optional: use {} to read all dictionaries, or {\"query\":{\"dictType\":\"order_status\"}} to filter. Use this before referencing an existing dictionary code or proposing a dictionary update."));
        }

        if (includeChangeTools)
        {
            tools.Add(AIFunctionFactory.Create(
                proposeProjectChangeSet,
                name: ProposeProjectChangeSet,
                description: "Validate a narrow CodeMaster metadata change set that can create, edit, reorder, or delete modules, entities, fields, and relations. After confirmation, apply the metadata; generation, migration, build, and tests are handled by the project workflow. For broad system requests use one confirmed project work plan instead. For repeated or all-controls entities, use CreateEntityProposal.CopyFieldsFromEntityName instead of enumerating every copied field or re-reading the control catalog."));

            tools.Add(AIFunctionFactory.Create(
                proposeUiPageEnhancement,
                name: ProposeUiPageEnhancement,
                description: "Prepare a controlled visual redesign for CodeMaster. Scaffold targets redesign Login or Dashboard. EntityPage targets index/add/edit/detail and uses stable genId-anchored SetTag, SetProp, RemoveProp, SetGrid, Move, and Group operations that are replayed after future code generation."));
        }


        if (includeDictionaryChange)
        {
            // Dictionary changes are represented by ProjectChangeSetProposal.Dictionaries so a
            // single user request always produces one ordered approval card. Keep the delegate
            // parameter for binary compatibility with extension callers and older tests.
        }

        if (includeWorkPlan && getProjectWorkPlan != null && upsertProjectWorkPlan != null)
        {
            if (getProjectCompletionStatus != null)
            {
                tools.Add(AIFunctionFactory.Create(
                    getProjectCompletionStatus,
                    name: GetProjectCompletionStatus,
                    description: "Read one authoritative completion snapshot combining current project metadata/generation state with the active work plan, stages, tasks, blockers, and diagnostics. Use this for remaining-work, progress, continue, finish-project, and delivery-status requests."));
            }

            if (getProjectWorkTask != null)
            {
                tools.Add(AIFunctionFactory.Create(
                    getProjectWorkTask,
                    name: GetProjectWorkTask,
                description: "Read one work-plan task by stable task key, including its exact executable definition, dependencies, status, diagnostics, and stage/plan version. Use this instead of loading the entire plan when repairing or completing one task. For view_definition tasks, sql must contain only one read-only SELECT ... FROM ... statement; broad-plan tasks prepare definition/consumer metadata first and final migration applies the physical view."));
            }

            tools.Add(AIFunctionFactory.Create(
                getProjectWorkPlan,
                name: GetProjectWorkPlan,
                description: "Read the authoritative standard-JSON project plan, including EntityFields, FieldControls, Relations, Views, ComplexPages, and ConsistencyCheck stages, dependency locks, confirmation hash, checkpoints, and diagnostics."));

            tools.Add(new WorkPlanUpsertFunction(AIFunctionFactory.Create(
                upsertProjectWorkPlan,
                name: UpsertProjectWorkPlan,
                description: "Create or update a complete executable task plan. CREATE: omit planId and expectedStructureVersion; the server assigns revision 1. UPDATE: use planId and the exact current integer structureVersion returned by get_project_work_plan (not projectStructureHash or contentHash). Preserve all task definitions when correcting parameters. Use the fixed stages EntityFields, FieldControls, Relations, Views, ComplexPages, ConsistencyCheck; every stage is metadata-only with GenerationMode=None and the agent never generates code. Ordinary plan confirmation and metadata execution follow the conversation workflow; generating the source code is a separate manual action the user triggers from the project page."), upsertProjectWorkPlan));

            if (supersedeProjectWorkPlan != null)
            {
                tools.Add(AIFunctionFactory.Create(
                    supersedeProjectWorkPlan,
                    name: SupersedeProjectWorkPlan,
                    description: "Retire the active work plan as Superseded when its stored task definition is malformed or the plan must be rebuilt. This preserves audit history, requires no active approval batch, and never changes project metadata. Read the plan first and use this only when an update cannot repair the plan."));
            }
        }

        if (includeWorkBatch && proposeProjectWorkBatch != null)
        {
            tools.Add(AIFunctionFactory.Create(
                proposeProjectWorkBatch,
                name: ProposeProjectWorkBatch,
                description: "Legacy/manual recovery tool for grouping ready tasks. Use it only when the server workflow explicitly requests it; normal confirmed plans are advanced automatically. Do not call this while a plan is AwaitingConfirmation."));
        }

        if (includeSubmitBusinessPlan && submitBusinessPlan != null)
        {
            tools.Add(AIFunctionFactory.Create(
                submitBusinessPlan,
                name: SubmitBusinessPlan,
                description: "Submit a business-only plan definition (modules, entities, fields, relations, views, pages) using stable cross-block keys. The server compiles it into the six-stage executable work plan (EntityFields, Views, Relations, FieldControls, ComplexPages, ConsistencyCheck) and streams progress events. Use this on confirmation turns instead of authoring task definitions, dependency graphs, or task JSON."));
        }

        return tools;
    }
}
