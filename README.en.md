# CodeMasterAI

**An AI-assisted low-code platform for .NET 10 and Vue 3 that turns visual models into maintainable, independently deployable application source code.**

[Website](https://www.codemasterai.top) · [Live Demo](https://www.codemasterai.top/index.html#/login) · [Introduction Video](https://www.bilibili.com/video/BV1Cxg96nE73/) · [Gitee](https://gitee.com/cwj19851010/codemasterai) · [GitHub](https://github.com/cwj19851010/codemasterai) · [简体中文](README.md)

CodeMasterAI targets enterprise management systems, internal business platforms and custom software delivery. Teams define projects, modules, entities, fields, relationships and page controls, then generate a complete solution containing backend services, DTOs, database migrations, permissions, menus, localization resources and Vue pages.

It is not a page builder that locks applications into a proprietary runtime. Generated projects are standard **ASP.NET Core + Vue 3** source code that can be built, deployed and extended without CodeMasterAI. Templates, incremental generation, MCP tools and the local desktop client automate repeated engineering work across later iterations.

> Current status: the project is under active development. The core framework, code generation, template designer, MCP integration and Tauri client workflow are usable, while template contracts and selected interactions continue to evolve.
>
> The project brand has changed from CodeMaster to CodeMasterAI. Existing solution names, namespaces and selected client identifiers remain under `CodeMaster.*` for compatibility.

> **The latest release fixes 10+ build and initialization issues.** If you previously hit `dotnet build` or `npm run build` failures in generated projects, init errors, or a 400 on login, pull the latest code and re-export the template. See [Release Notes](#release-notes) for details.

## Introduction Video

[![CodeMasterAI introduction: an Agent-powered low-code development platform](https://i0.hdslb.com/bfs/archive/d89000ccb3ac6bd309a2401c05144fc45913007d.jpg)](https://www.bilibili.com/video/BV1Cxg96nE73/)

[Watch the complete CodeMasterAI introduction on Bilibili](https://www.bilibili.com/video/BV1Cxg96nE73/) (2 minutes 24 seconds).

The video covers visual modeling, Vue 3 page design, full and incremental code generation, the built-in Agent, MCP/Skill integration and multiple databases. It was published under the former CodeMaster name and demonstrates the same project now branded as CodeMasterAI.

## From Model to Delivery

```text
Create projects and modules
        ↓
Design entities, fields and one-to-one/one-to-many relationships
        ↓
Configure list, form, detail and field controls
        ↓
Generate backend, frontend, permissions, menus and migration data
        ↓
Run and validate the generated application
        ↓
Iterate through templates, incremental generation or AI agents
```

## Use Cases

- Data-intensive ERP, CRM, MES, WMS and enterprise administration systems
- Internal platforms requiring consistent permissions, menus, dictionaries, tenancy and auditing
- Engineering teams building their own framework, control templates and code conventions
- Structured workflows where AI participates in modeling, generation and maintenance through tools

## Highlights

- **Convention over configuration**: define entities and fields once, then generate backend services, DTOs, frontend pages, menus, permissions, language resources and migration data.
- **Dynamic API and permissions**: application services can be exposed as REST endpoints and protected by generated permission codes.
- **Template-driven code generation**: page templates, field controls and child-table templates are stored in the database and can be edited from the UI.
- **Full and incremental generation**: regenerate clean projects or update existing generated code while preserving user-edited script sections.
- **Visual page design**: the entity designer covers common Element Plus controls and keeps component trees, properties and related scripts aligned.
- **MCP integration**: AI agents can create or edit CodeMasterAI projects, modules, entities and fields through MCP instead of writing metadata files directly.
- **Tauri + LocalAgent client**: the desktop client can call local capabilities such as project initialization, local code generation and starting generated services.
- **Multiple databases**: runtime and migration workflows support SQL Server, MySQL, PostgreSQL, SQLite and Oracle through selectable providers.
- **Multi-tenant foundation**: tenant-aware entities, menu scopes, JWT tenant claims and data-permission hooks are built in.
- **Production-oriented stack**: .NET 10, SqlSugar runtime ORM, EF Core Migrator, Vue 3, Element Plus, Vite, SignalR, Quartz and optional Redis cache.

## Screenshots

### Project Entity Management

![Project entity management](CodeMaster.WebApi/wwwroot/site/images/product-project-list.png)

### Template Configuration

![Template configuration](CodeMaster.WebApi/wwwroot/site/images/product-template-config.png)

### Entity Designer

![Entity designer](CodeMaster.WebApi/wwwroot/site/images/product-entity-designer.png)

## Architecture

```text
CodeMaster.WebApi          ASP.NET Core API, dynamic API, controllers, middleware
CodeMaster.Application     Services, DTOs, code generation, templates, MCP-facing logic
CodeMaster.Domain          Entity models
CodeMaster.Infrastructure  SqlSugar, repositories, auth, SignalR, Quartz, middleware
CodeMaster.Core            Base entities, interfaces, shared abstractions
CodeMaster.Migrator        EF Core migrations and seed data
CodeMaster.Vue             Vue 3 admin UI and Tauri client source
CodeMaster.LocalAgent      Local sidecar service used by the desktop client
CodeMaster.McpServer       MCP server for AI-assisted code generation workflows
Templates                  Source template package used for generated projects
```

## Requirements

- .NET SDK 10
- Node.js 20+
- SQL Server, MySQL, PostgreSQL, SQLite or Oracle
- The quick-start configuration uses MySQL 8 as an example. EF migration history is not committed; regenerate and verify migrations for the selected provider before publishing or deploying.
- Optional: Redis, Tauri prerequisites for desktop client builds

## Quick Start

Install .NET SDK 10 and Node.js 20+. The default configuration uses SQLite, so no database server is needed to get started; see "Switching Databases" below for MySQL and other providers.

1. Create local configuration (the repository only ships example files without real passwords):

```bash
cp CodeMaster.WebApi/appsettings.example.json CodeMaster.WebApi/appsettings.json
cp CodeMaster.Migrator/appsettings.example.json CodeMaster.Migrator/appsettings.json
```

The examples default to `DbProvider=Sqlite` with `Data Source=../CodeMaster.db`. Both project folders are siblings, so they resolve to the same database file at the repository root.

2. Restore and build:

```bash
dotnet restore CodeMaster.sln
dotnet build CodeMaster.sln
```

3. Initialize the database (schema and seed data):

```bash
cd CodeMaster.Migrator
dotnet run
```

`CodeMaster.Migrator` defaults to `DbProvider=Sqlite`, matching the example configuration, so no extra argument is needed. If it was previously built for another provider, rebuild with `dotnet build -p:DbProvider=Sqlite`.

4. Start the API:

```bash
cd CodeMaster.WebApi
dotnet run
```

5. Start the frontend:

```bash
cd CodeMaster.Vue
npm install
npm run dev
```

Default development account:

```text
admin / admin123
```

## Switching Databases

`CodeMaster.WebApi` selects its SqlSugar provider at runtime from `DbProvider`. `CodeMaster.Migrator` must also be built with the matching MSBuild property.

| Database | `DbProvider` / build property |
| --- | --- |
| SQL Server | `SqlServer` |
| MySQL | `MySql` |
| PostgreSQL | `PostgreSQL` |
| SQLite | `Sqlite` |
| Oracle | `Oracle` |

For example, to use PostgreSQL:

```bash
cd CodeMaster.Migrator
dotnet build -p:DbProvider=PostgreSQL
dotnet run --no-build
```

The migrator build property, Migrator configuration and WebApi configuration must all target the same database. Rebuild the migrator after changing providers, then validate migrations and seed data against that database.

## Configuration

Do not commit real production secrets. Use one of these approaches:

- environment variables
- user secrets for local development
- an ignored `appsettings.Production.json`
- server-side secret managers

Important keys:

```text
ConnectionStrings__DefaultConnection
DbProvider
JwtSettings__SecretKey
Authentication__GitHub__ClientId
Authentication__GitHub__ClientSecret
Authentication__GitHub__CallbackUrl
Email__Smtp__UserName
Email__Smtp__Password
Email__CodeSecret
```

For the Vue/Tauri client, copy `CodeMaster.Vue/.env.example` to a local `.env` file when a custom server address is needed.

## Code Generation Notes

- Generated `.vue` and `.auto.js` files should not be hand-edited.
- Template changes should be made in database seed data; one-off local repair or update scripts should not be committed.
- `tree.json` is the entity designer source of truth.
- `fields.json` preserves per-field script sections across regeneration.

## MCP Usage

The MCP server is intended for natural-language workflows such as:

- create or edit projects, modules and entities
- define entity fields and control attributes
- initialize generated projects
- run full or incremental code generation
- start generated frontend/backend services
- inspect generated project entity structure

Agents should use MCP tools for metadata operations instead of directly editing generated project metadata.

## Security

Before publishing a fork or deployment:

- rotate any secrets that may have been committed or shared
- keep production appsettings out of git
- disable Swagger in public production deployments unless explicitly protected
- use HTTPS and a strong JWT secret
- review generated templates before exposing them to untrusted users

See [SECURITY.md](SECURITY.md) for vulnerability reporting.

## Release Notes

Current release: **2026.11** · Full list in [CHANGELOG.md](CHANGELOG.md) (Chinese)

### November 2026: build and initialization fixes

This release targets the most common reports from the community — **generated projects that fail to build, and initializations that stop halfway**.

**Generated project build**

- The open-source template no longer references the private printing module (`CS0234: SeedData.CodeGen does not exist`)
- Added a `CodeMaster.Vue/src/utils/printReport.js` stub so `vite build` stops failing with `Could not load .../printReport`
- Initialization now removes dangling seed references from older template packages

**Initialization and database**

- Merged into a single `DbContext` with EF module scanning; `Add-Migration` no longer reports `More than one DbContext was found`
- Fixed the working directory for the relative SQLite connection string so Migrator and WebApi share one database file (fixes "no seed data after init" and the 400 on login)
- Fixed duplicate registration of the agent batch adapter (`An item with the same key has already been added: project_change_set`)

**Modeling and generation**

- The compiler now creates relation foreign-key fields during the entity stage, fixing `references missing target field` / `uses removed or renamed target field`
- Added translations for the `system` / `monitor` top-level menus so they no longer fall back to raw English keys
- Corrected the template include list (`appsettings.Development.json` → `appsettings.example.json`) so exports complete reliably

**Navigation**

- Removed "Data Source Management"; entities, views and APIs now live under "Code Management"
- Menu order: Projects, Modules, Entities, Views, APIs, Complex Pages, Template Config, Components

### Upgrading

1. Pull the latest code (Gitee and GitHub are in sync)
2. Re-export the template, or delete `Templates/CodeMaster_Template_*.zip` and regenerate
3. Re-initialize existing generated projects, or apply the fixes above manually

If a project still fails to build, open an issue with the full `dotnet build` / `npm run build` output.

## Contributing

Issues and pull requests are welcome while the project is stabilizing. Please read [CONTRIBUTING.md](CONTRIBUTING.md) before opening larger changes.

## Community and Support

- Issues and feature requests: [Gitee Issues](https://gitee.com/cwj19851010/codemasterai/issues) · [GitHub Issues](https://github.com/cwj19851010/codemasterai/issues)
- QQ group (Chinese-speaking community): **1135890233** — the QR code is on the [Contact page](https://www.codemasterai.top/contact)
- When reporting a build failure, include the full `dotnet build` / `npm run build` output and the template version used

## License

CodeMasterAI is released under the [Apache License 2.0](LICENSE).
