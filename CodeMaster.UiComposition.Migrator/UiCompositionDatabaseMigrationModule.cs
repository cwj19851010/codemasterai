using System.Data;
using System.Data.Common;
using CodeMaster.Migrator.Persistence.EfCore;
using CodeMaster.UiComposition.Migrator.Migrations;
using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CodeMaster.UiComposition.Migrator;

public sealed class UiCompositionDatabaseMigrationModule : IDatabaseMigrationModule
{
    internal const string HistoryTable = "__EFMigrationsHistory_UiComposition";

    private static readonly string[] DefinitionTables =
    {
        "sys_page_component_binding",
        "sys_page_data_source",
        "sys_page_entity_dependency",
        "sys_page_layout_preset",
        "sys_project_page",
        "sys_page_display_template"
    };

    private static readonly string[] ProjectDataSourceTables =
    {
        "sys_page_data_source_binding",
        "sys_project_data_source"
    };

    private static readonly string[] DisplayTemplateTables =
    {
        "sys_page_display_template"
    };

    private static readonly string[] OpenApiTables =
    {
        "sys_openapi_document",
        "sys_openapi_schema_binding",
        "sys_openapi_member_binding"
    };

    private static readonly string[] ProjectViewDefinitionTables =
    {
        "sys_project_view_definition",
        "sys_project_view_dependency",
        "sys_project_view_field_lineage"
    };

    private static readonly string[] SynchronizationColumns =
    {
        "last_synced_time",
        "sync_mode"
    };

    private static readonly string[] OpenApiProjectDataSourceColumns =
    {
        "openapi_document_id",
        "operation_key",
        "http_method",
        "route_path",
        "summary",
        "description",
        "request_entity_id",
        "response_entity_id",
        "operation_contract_json",
        "openapi_sync_state"
    };

    public string ModuleName => "UI Composition";

    public async Task MigrateAsync(
        MigrationModuleExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var optionsBuilder = new DbContextOptionsBuilder<UiCompositionDbContext>();
        var migrationsAssembly = typeof(UiCompositionDbContext).Assembly.GetName().Name
            ?? throw new InvalidOperationException("Unable to resolve the UI Composition migrations assembly name.");
        context.ConfigureDbContext(
            optionsBuilder,
            context.ConnectionString,
            migrationsAssembly,
            HistoryTable);
        // SQL Server adds provider-specific filters to nullable unique indexes.
        // The migration itself contains that filter; EF's provider-independent
        // snapshot otherwise reports a false pending-model warning.
        optionsBuilder.ConfigureWarnings(warnings =>
            warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

        await using var dbContext = new UiCompositionDbContext(optionsBuilder.Options);
        await AdoptExistingSchemaAsync(dbContext, cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);
        await UiCompositionLegacyDataMigrator.MigrateAsync(dbContext, cancellationToken);
        await BackfillMetadataOnlyEntitiesAsync(dbContext, cancellationToken);
        await ValidateSchemaAsync(dbContext, cancellationToken);
    }

    private static async Task<int> BackfillMetadataOnlyEntitiesAsync(
        UiCompositionDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var inspector = new UiCompositionSchemaInspector(dbContext.Database.GetDbConnection());
        var tables = await inspector.GetTablesAsync(cancellationToken);
        if (!tables.Contains("sys_module_entity") || !tables.Contains("sys_openapi_schema_binding"))
            return 0;

        var entityColumns = await inspector.GetColumnsAsync("sys_module_entity", cancellationToken);
        if (!entityColumns.Contains("generation_mode"))
            return 0;

        FormattableString sql = $"UPDATE sys_module_entity SET generation_mode = 1, is_generated = {false}, last_generated_time = NULL WHERE id IN (SELECT module_entity_id FROM sys_openapi_schema_binding)";
        return await dbContext.Database.ExecuteSqlInterpolatedAsync(sql, cancellationToken);
    }

    private static async Task AdoptExistingSchemaAsync(
        UiCompositionDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pending.Count == 0)
        {
            return;
        }

        var inspector = new UiCompositionSchemaInspector(dbContext.Database.GetDbConnection());
        var tables = await inspector.GetTablesAsync(cancellationToken);

        if (pending.Contains(UiCompositionDefinition.MigrationId))
        {
            await AdoptTablePhaseAsync(
                dbContext,
                tables,
                DefinitionTables,
                UiCompositionDefinition.MigrationId,
                cancellationToken);
        }

        if (pending.Contains(UiCompositionSynchronizationMetadata.MigrationId)
            && tables.Contains("sys_page_data_source"))
        {
            var columns = await inspector.GetColumnsAsync("sys_page_data_source", cancellationToken);
            var existingColumnCount = SynchronizationColumns.Count(columns.Contains);
            if (existingColumnCount == SynchronizationColumns.Length)
            {
                await RecordMigrationAsync(
                    dbContext,
                    UiCompositionSynchronizationMetadata.MigrationId,
                    cancellationToken);
            }
            else if (existingColumnCount > 0)
            {
                throw new InvalidOperationException(
                    "The existing UI Composition data-source table contains only part of the synchronization metadata. " +
                    "Repair the missing column before applying private migrations.");
            }
        }

        if (pending.Contains(UiCompositionProjectDataSource.MigrationId))
        {
            await AdoptTablePhaseAsync(
                dbContext,
                tables,
                ProjectDataSourceTables,
                UiCompositionProjectDataSource.MigrationId,
                cancellationToken);
        }

        if (pending.Contains(UiCompositionDisplayTemplate.MigrationId))
        {
            await AdoptTablePhaseAsync(
                dbContext,
                tables,
                DisplayTemplateTables,
                UiCompositionDisplayTemplate.MigrationId,
                cancellationToken);
        }

        if (pending.Contains(UiCompositionOpenApiDataSource.MigrationId))
        {
            var existingTableCount = OpenApiTables.Count(tables.Contains);
            var projectDataSourceColumns = tables.Contains("sys_project_data_source")
                ? await inspector.GetColumnsAsync("sys_project_data_source", cancellationToken)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existingColumnCount = OpenApiProjectDataSourceColumns.Count(projectDataSourceColumns.Contains);

            if (existingTableCount == OpenApiTables.Length
                && existingColumnCount == OpenApiProjectDataSourceColumns.Length)
            {
                await RecordMigrationAsync(
                    dbContext,
                    UiCompositionOpenApiDataSource.MigrationId,
                    cancellationToken);
            }
            else if (existingTableCount > 0 || existingColumnCount > 0)
            {
                var missingTables = OpenApiTables.Where(table => !tables.Contains(table));
                var missingColumns = OpenApiProjectDataSourceColumns.Where(column => !projectDataSourceColumns.Contains(column));
                throw new InvalidOperationException(
                    $"The existing UI Composition OpenAPI schema is incomplete. " +
                    $"Missing tables: {string.Join(", ", missingTables)}. " +
                    $"Missing sys_project_data_source columns: {string.Join(", ", missingColumns)}.");
            }
        }

        if (pending.Contains(UiCompositionPageProjectDataSource.MigrationId)
            && tables.Contains("sys_page_data_source"))
        {
            var columns = await inspector.GetColumnsAsync("sys_page_data_source", cancellationToken);
            if (columns.Contains("project_data_source_id"))
            {
                await RecordMigrationAsync(
                    dbContext,
                    UiCompositionPageProjectDataSource.MigrationId,
                    cancellationToken);
            }
        }

        if (pending.Contains(UiCompositionProjectViewDefinition.MigrationId))
        {
            await AdoptTablePhaseAsync(
                dbContext,
                tables,
                ProjectViewDefinitionTables,
                UiCompositionProjectViewDefinition.MigrationId,
                cancellationToken);
        }
    }

    private static async Task AdoptTablePhaseAsync(
        UiCompositionDbContext dbContext,
        IReadOnlySet<string> existingTables,
        IReadOnlyCollection<string> requiredTables,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var existingCount = requiredTables.Count(existingTables.Contains);
        if (existingCount == requiredTables.Count)
        {
            await RecordMigrationAsync(dbContext, migrationId, cancellationToken);
            return;
        }

        if (existingCount > 0)
        {
            var missing = requiredTables.Where(table => !existingTables.Contains(table));
            throw new InvalidOperationException(
                $"The existing UI Composition schema is incomplete for migration {migrationId}. " +
                $"Missing tables: {string.Join(", ", missing)}.");
        }
    }

    private static async Task RecordMigrationAsync(
        UiCompositionDbContext dbContext,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var historyRepository = dbContext.GetService<IHistoryRepository>();
        await dbContext.Database.ExecuteSqlRawAsync(
            historyRepository.GetCreateIfNotExistsScript(),
            cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            historyRepository.GetInsertScript(new HistoryRow(migrationId, ProductInfo.GetVersion())),
            cancellationToken);
    }

    private static async Task ValidateSchemaAsync(
        UiCompositionDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var inspector = new UiCompositionSchemaInspector(dbContext.Database.GetDbConnection());
        var tables = await inspector.GetTablesAsync(cancellationToken);
        var missingTables = DefinitionTables
            .Concat(ProjectDataSourceTables)
            .Concat(DisplayTemplateTables)
            .Concat(OpenApiTables)
            .Concat(ProjectViewDefinitionTables)
            .Where(table => !tables.Contains(table))
            .ToArray();
        if (missingTables.Length > 0)
        {
            throw new InvalidOperationException(
                $"UI Composition migration completed without all required tables: {string.Join(", ", missingTables)}.");
        }

        var columns = await inspector.GetColumnsAsync("sys_page_data_source", cancellationToken);
        var missingColumns = SynchronizationColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missingColumns.Length > 0)
        {
            throw new InvalidOperationException(
                $"UI Composition migration completed without all synchronization columns: {string.Join(", ", missingColumns)}.");
        }

        if (!columns.Contains("project_data_source_id"))
        {
            throw new InvalidOperationException(
                "UI Composition migration completed without the page project-data-source reference column.");
        }


        var projectDataSourceColumns = await inspector.GetColumnsAsync("sys_project_data_source", cancellationToken);
        var missingOpenApiColumns = OpenApiProjectDataSourceColumns
            .Where(column => !projectDataSourceColumns.Contains(column))
            .ToArray();
        if (missingOpenApiColumns.Length > 0)
        {
            throw new InvalidOperationException(
                $"UI Composition migration completed without all OpenAPI data-source columns: {string.Join(", ", missingOpenApiColumns)}.");
        }
    }
}

internal sealed class UiCompositionSchemaInspector(DbConnection connection)
{
    public async Task<IReadOnlySet<string>> GetTablesAsync(CancellationToken cancellationToken)
    {
        var provider = connection.GetType().FullName ?? connection.GetType().Name;
        var sql = provider switch
        {
            var name when name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                => "SELECT name FROM sqlite_master WHERE type = 'table'",
            var name when name.Contains("MySql", StringComparison.OrdinalIgnoreCase)
                => "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE'",
            var name when name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                => "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'",
            var name when name.Contains("Oracle", StringComparison.OrdinalIgnoreCase)
                => "SELECT TABLE_NAME FROM USER_TABLES",
            var name when name.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase)
                => "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'",
            _ => throw new NotSupportedException($"Unsupported migration connection type: {provider}")
        };

        return await ReadNamesAsync(sql, cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        var provider = connection.GetType().FullName ?? connection.GetType().Name;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadNamesAsync($"PRAGMA table_info('{tableName.Replace("'", "''")}')", cancellationToken, 1);
        }

        var commandText = provider switch
        {
            var name when name.Contains("MySql", StringComparison.OrdinalIgnoreCase)
                => "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName",
            var name when name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                => "SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @tableName",
            var name when name.Contains("Oracle", StringComparison.OrdinalIgnoreCase)
                => "SELECT COLUMN_NAME FROM USER_TAB_COLUMNS WHERE TABLE_NAME = @tableName",
            var name when name.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase)
                => "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName",
            _ => throw new NotSupportedException($"Unsupported migration connection type: {provider}")
        };

        return await ReadNamesAsync(commandText, cancellationToken, parameterValue: tableName);
    }

    private async Task<IReadOnlySet<string>> ReadNamesAsync(
        string commandText,
        CancellationToken cancellationToken,
        int ordinal = 0,
        string? parameterValue = null)
    {
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            if (parameterValue != null)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@tableName";
                parameter.Value = connection.GetType().FullName?.Contains("Oracle", StringComparison.OrdinalIgnoreCase) == true
                    ? parameterValue.ToUpperInvariant()
                    : parameterValue;
                command.Parameters.Add(parameter);
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(ordinal))
                {
                    result.Add(reader.GetString(ordinal));
                }
            }

            return result;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
