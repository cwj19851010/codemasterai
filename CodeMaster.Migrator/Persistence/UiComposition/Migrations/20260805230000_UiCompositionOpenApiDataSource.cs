using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionOpenApiDataSource : Migration
{
    public const string MigrationId = "20260805230000_UiCompositionOpenApiDataSource";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "openapi_document_id", table: "sys_project_data_source", nullable: true);
        migrationBuilder.AddColumn<string>(name: "operation_key", table: "sys_project_data_source", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>(name: "http_method", table: "sys_project_data_source", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<string>(name: "route_path", table: "sys_project_data_source", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<string>(name: "summary", table: "sys_project_data_source", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>(name: "description", table: "sys_project_data_source", maxLength: 2000, nullable: true);
        migrationBuilder.AddColumn<long>(name: "request_entity_id", table: "sys_project_data_source", nullable: true);
        migrationBuilder.AddColumn<long>(name: "response_entity_id", table: "sys_project_data_source", nullable: true);
        migrationBuilder.AddColumn<string>(name: "operation_contract_json", table: "sys_project_data_source", nullable: true);
        migrationBuilder.AddColumn<int>(name: "openapi_sync_state", table: "sys_project_data_source", nullable: true);

        migrationBuilder.CreateTable(
            name: "sys_openapi_document",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                module_id = table.Column<long>(nullable: false),
                name = table.Column<string>(maxLength: 200, nullable: false),
                source_url = table.Column<string>(maxLength: 2000, nullable: true),
                title = table.Column<string>(maxLength: 300, nullable: true),
                description = table.Column<string>(maxLength: 2000, nullable: true),
                openapi_version = table.Column<string>(maxLength: 50, nullable: false),
                document_hash = table.Column<string>(maxLength: 64, nullable: false),
                raw_document = table.Column<string>(nullable: false),
                normalized_snapshot_json = table.Column<string>(nullable: false),
                status = table.Column<int>(nullable: false),
                last_synced_time = table.Column<DateTime>(nullable: true),
                last_error = table.Column<string>(maxLength: 2000, nullable: true),
                create_by = table.Column<string>(maxLength: 64, nullable: true),
                create_user_id = table.Column<long>(nullable: true),
                create_time = table.Column<DateTime>(nullable: false),
                update_user_id = table.Column<long>(nullable: true),
                update_by = table.Column<string>(maxLength: 64, nullable: true),
                update_time = table.Column<DateTime>(nullable: true),
                remark = table.Column<string>(maxLength: 500, nullable: true),
                is_deleted = table.Column<bool>(nullable: false),
                delete_by = table.Column<string>(maxLength: 64, nullable: true),
                delete_time = table.Column<DateTime>(nullable: true),
                delete_user_id = table.Column<long>(nullable: true),
                tenant_id = table.Column<long>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_sys_openapi_document", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_openapi_schema_binding",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                document_id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                module_id = table.Column<long>(nullable: false),
                module_entity_id = table.Column<long>(nullable: false),
                schema_key = table.Column<string>(maxLength: 500, nullable: false),
                schema_name = table.Column<string>(maxLength: 300, nullable: false),
                dto_name = table.Column<string>(maxLength: 300, nullable: false),
                dto_role = table.Column<int>(nullable: false),
                schema_hash = table.Column<string>(maxLength: 64, nullable: false),
                snapshot_json = table.Column<string>(nullable: false),
                sync_state = table.Column<int>(nullable: false),
                last_synced_time = table.Column<DateTime>(nullable: true),
                create_by = table.Column<string>(maxLength: 64, nullable: true),
                create_user_id = table.Column<long>(nullable: true),
                create_time = table.Column<DateTime>(nullable: false),
                update_user_id = table.Column<long>(nullable: true),
                update_by = table.Column<string>(maxLength: 64, nullable: true),
                update_time = table.Column<DateTime>(nullable: true),
                remark = table.Column<string>(maxLength: 500, nullable: true),
                is_deleted = table.Column<bool>(nullable: false),
                delete_by = table.Column<string>(maxLength: 64, nullable: true),
                delete_time = table.Column<DateTime>(nullable: true),
                delete_user_id = table.Column<long>(nullable: true),
                tenant_id = table.Column<long>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_sys_openapi_schema_binding", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_openapi_member_binding",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                document_id = table.Column<long>(nullable: false),
                schema_binding_id = table.Column<long>(nullable: false),
                entity_field_id = table.Column<long>(nullable: true),
                target_schema_binding_id = table.Column<long>(nullable: true),
                member_key = table.Column<string>(maxLength: 700, nullable: false),
                external_id = table.Column<string>(maxLength: 300, nullable: true),
                json_name = table.Column<string>(maxLength: 300, nullable: false),
                member_name = table.Column<string>(maxLength: 300, nullable: false),
                member_kind = table.Column<int>(nullable: false),
                data_type = table.Column<string>(maxLength: 300, nullable: true),
                format = table.Column<string>(maxLength: 100, nullable: true),
                is_required = table.Column<bool>(nullable: false),
                is_nullable = table.Column<bool>(nullable: false),
                is_collection = table.Column<bool>(nullable: false),
                member_hash = table.Column<string>(maxLength: 64, nullable: false),
                snapshot_json = table.Column<string>(nullable: false),
                sync_state = table.Column<int>(nullable: false),
                last_synced_time = table.Column<DateTime>(nullable: true),
                create_by = table.Column<string>(maxLength: 64, nullable: true),
                create_user_id = table.Column<long>(nullable: true),
                create_time = table.Column<DateTime>(nullable: false),
                update_user_id = table.Column<long>(nullable: true),
                update_by = table.Column<string>(maxLength: 64, nullable: true),
                update_time = table.Column<DateTime>(nullable: true),
                remark = table.Column<string>(maxLength: 500, nullable: true),
                is_deleted = table.Column<bool>(nullable: false),
                delete_by = table.Column<string>(maxLength: 64, nullable: true),
                delete_time = table.Column<DateTime>(nullable: true),
                delete_user_id = table.Column<long>(nullable: true),
                tenant_id = table.Column<long>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_sys_openapi_member_binding", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ux_openapi_document_project_name",
            table: "sys_openapi_document",
            columns: new[] { "tenant_id", "project_id", "name" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_openapi_schema_document_key",
            table: "sys_openapi_schema_binding",
            columns: new[] { "document_id", "schema_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_openapi_schema_entity",
            table: "sys_openapi_schema_binding",
            column: "module_entity_id",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_openapi_member_schema_key",
            table: "sys_openapi_member_binding",
            columns: new[] { "schema_binding_id", "member_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_openapi_member_field",
            table: "sys_openapi_member_binding",
            column: "entity_field_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "sys_openapi_member_binding");
        migrationBuilder.DropTable(name: "sys_openapi_schema_binding");
        migrationBuilder.DropTable(name: "sys_openapi_document");

        migrationBuilder.DropColumn(name: "openapi_document_id", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "operation_key", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "http_method", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "route_path", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "summary", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "description", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "request_entity_id", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "response_entity_id", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "operation_contract_json", table: "sys_project_data_source");
        migrationBuilder.DropColumn(name: "openapi_sync_state", table: "sys_project_data_source");
    }
}
