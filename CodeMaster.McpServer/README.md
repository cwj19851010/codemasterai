# CodeMaster MCP Server

`CodeMaster.McpServer` exposes CodeMaster project and code-generation workflows through the official .NET MCP SDK over stdio.

## Architecture

The MCP process never connects to the CodeMaster metadata database.

- Project, module, entity, field, and structure operations call the authenticated CodeMaster WebApi.
- MCP tokens are validated by CodeMaster and saved under `~/.codemaster/mcp-auth.json`.
- Initialization, generation, migration, build, and process operations reuse `CodeMaster.LocalAgent` and run on the user's machine.
- LocalAgent downloads a tenant-filtered generation bundle and templates from WebApi, uses a temporary local SQLite metadata snapshot, and reports completion to WebApi.

## Tools

- `codemaster_login`
- `codemaster_logout`
- `codemaster_whoami`
- `resolve_project_context`
- `query_project`
- `analyze_requirements`
- `save_project`
- `save_module`
- `create_or_update_entity`
- `query_dictionaries`
- `save_dictionary`
- `run_project_operation`
- `generate_code`
- `get_project_structure`

When the private printing LocalAgent extension is installed,
`run_project_operation` accepts `operation=printing`. It copies the printing
runtime and migration module to the generated project's source-level `modules`
directory. Run the generated project's Migrator afterwards before publishing
template rows. Legacy generated projects are upgraded idempotently with the
stable runtime loaders, report pages, Program hooks, and modules copy targets;
patched files keep a `.printing.bak` backup.

When the private UiComposition module is installed, the MCP host also exposes
the structured view and complex-page workflow:

- `get_view_builder_context`
- `preview_view_sql`
- `save_view_definition`
- `deploy_view_definition`
- `register_read_only_view_entity`
- `import_existing_view`
- `get_complex_page_context`
- `propose_complex_page_plan`
- `materialize_project`

The intended broad-project sequence is to preview and save all independent view
definitions (including declared fields and lineage), register their read-only
consumers, then call `materialize_project` once. LocalAgent smart-generates the
normal and read-only entities first, runs the target migration which applies
prepared managed views, generates complex pages, and finally builds and tests.
Independent views do not wait on one another unless a view explicitly depends
on another managed view. `deploy_view_definition` and
`register_read_only_view_entity` retain an explicit `confirmApply` gate for
standalone use.

`create_or_update_entity` supports the same relation metadata used by the Web UI:

- `relations`: legacy one-to-many child-table relations.
- `ownedOneRelations`: owned one-to-one composition relations, resolved across modules in the same project.
- `fields[].resultMappings`: `select-table` source-to-local field assignments.
- `description` is a short display title (prefer concise Chinese, max 50 characters); put purpose, rules, units, relation, tenant, and soft-delete semantics in `businessDescription` (max 2000 characters).

Entity capability rules are shared by Web UI, Agent, MCP, generated services, and migration scanning:

- Every generated entity starts from `IBaseEntity`.
- `hasPrimaryKey=true` adds the standard `long Id`, `IEntity<long>`, migration scanning, and `getById` support.
- `isReadOnly=true` disables create/update/delete. A keyed read-only entity keeps `getById`; a keyless read-only entity exposes list/query/export only.
- Writable and tree entities require a primary key.
- Audit fields are `CreateUserId`, `CreateBy`, `CreateTime`, `UpdateUserId`, `UpdateBy`, and `UpdateTime`.

Field controls include `input`, `textarea`, `number`, `select`, `select-table`, `date`, `datetime`, `switch`, `checkbox`, `radio-group`, `checkbox-group`, `file`, `image`, `editor`, and `cascader`.

- Computed fields use `fieldCategory=Computed` and arithmetic formulas with `[FieldName]` references.
- Aggregate fields use `fieldCategory=Aggregate`, `aggregateType=Sum|Avg|Concat`, and a child field from an existing one-to-many relation.

All metadata writes still go through the authenticated CodeMaster WebApi. The MCP process does not receive or require a database connection string.

`query_dictionaries` reads target-project dictionary types and optional entries for a tenant. `save_dictionary` performs a non-destructive upsert keyed by `(tenantId, dictType)` and `(tenantId, dictType, value)`; omitted entries are preserved and soft-deleted matches are restored.

## Optional MCP Modules

The MCP host discovers optional tool assemblies from its local `modules` directory. Additional module directories can be supplied through the semicolon-separated `CODEMASTER_MCP_MODULE_PATHS` environment variable.

Optional modules may contribute MCP tool types and shared LocalAgent action handlers through CodeMaster's generic extension contracts. The public MCP host does not reference feature-specific module types, and removing every optional module leaves the standard tools above buildable and usable.

## Authentication And Project Context

1. Generate an MCP Token in CodeMaster personal center.
2. Open a generated project directory containing `.codemaster/project-context.json`.
3. Call `codemaster_login` once and paste the token.
4. Future tools resolve `serverBaseUrl` and `projectId` from project context and the saved token.

Tokens are never written into generated project directories.

## Build

```powershell
dotnet build CodeMaster.McpServer/CodeMaster.McpServer.csproj
```

## Recommended MCP Configuration

```json
{
  "mcpServers": {
    "codemaster": {
      "command": "dotnet",
      "args": [
        "D:/MyHomeWorks/CodeMaster/CodeMaster.McpServer/bin/Debug/net10.0/CodeMaster.McpServer.dll"
      ],
      "cwd": "D:/your-generated-project"
    }
  }
}
```

Use a prebuilt DLL. Running `dotnet run` can write restore/build output before the MCP stdio transport starts.
