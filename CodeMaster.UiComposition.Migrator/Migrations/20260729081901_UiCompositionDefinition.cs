using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionDefinition : Migration
{
    public const string MigrationId = "20260729081901_UiCompositionDefinition";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sys_page_component_binding",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                page_id = table.Column<long>(nullable: false),
                component_id = table.Column<string>(maxLength: 100, nullable: false),
                binding_id = table.Column<string>(maxLength: 100, nullable: false),
                data_source_id = table.Column<long>(nullable: false),
                field_id = table.Column<long>(nullable: true),
                binding_role = table.Column<int>(nullable: false),
                binding_status = table.Column<int>(nullable: false),
                sync_mode = table.Column<int>(nullable: false),
                auto_include_new_fields = table.Column<bool>(nullable: false),
                control_type = table.Column<string>(maxLength: 100, nullable: true),
                props_json = table.Column<string>(nullable: false),
                label_override = table.Column<string>(maxLength: 200, nullable: true),
                origin = table.Column<int>(nullable: false),
                generated_gen_id = table.Column<string>(maxLength: 160, nullable: false),
                last_known_path = table.Column<string>(maxLength: 500, nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_page_component_binding", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_page_data_source",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                page_id = table.Column<long>(nullable: false),
                source_key = table.Column<string>(maxLength: 100, nullable: false),
                source_type = table.Column<int>(nullable: false),
                entity_id = table.Column<long>(nullable: true),
                service_name = table.Column<string>(maxLength: 200, nullable: false),
                method_name = table.Column<string>(maxLength: 200, nullable: false),
                dto_name = table.Column<string>(maxLength: 300, nullable: false),
                data_shape = table.Column<int>(nullable: false),
                params_json = table.Column<string>(nullable: false),
                schema_hash = table.Column<string>(maxLength: 64, nullable: true),
                order_num = table.Column<int>(nullable: false),
                is_enabled = table.Column<bool>(nullable: false),
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
            constraints: table => table.PrimaryKey("PK_sys_page_data_source", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_page_entity_dependency",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                page_id = table.Column<long>(nullable: false),
                entity_id = table.Column<long>(nullable: false),
                data_source_id = table.Column<long>(nullable: true),
                dependency_type = table.Column<int>(nullable: false),
                field_ids_json = table.Column<string>(nullable: false),
                summary_json = table.Column<string>(nullable: false),
                schema_hash = table.Column<string>(maxLength: 64, nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_page_entity_dependency", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_page_layout_preset",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                preset_key = table.Column<string>(maxLength: 100, nullable: false),
                preset_name = table.Column<string>(maxLength: 200, nullable: false),
                description = table.Column<string>(maxLength: 500, nullable: true),
                layout_json = table.Column<string>(nullable: false),
                sort = table.Column<int>(nullable: false),
                is_system = table.Column<bool>(nullable: false),
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
            constraints: table => table.PrimaryKey("PK_sys_page_layout_preset", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_project_page",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                module_id = table.Column<long>(nullable: false),
                page_key = table.Column<string>(maxLength: 100, nullable: false),
                page_name = table.Column<string>(maxLength: 200, nullable: false),
                description = table.Column<string>(maxLength: 500, nullable: true),
                route_path = table.Column<string>(maxLength: 300, nullable: false),
                icon = table.Column<string>(maxLength: 100, nullable: true),
                order_num = table.Column<int>(nullable: false),
                page_type = table.Column<int>(nullable: false),
                layout_preset_id = table.Column<long>(nullable: true),
                layout_json = table.Column<string>(nullable: false),
                definition_version = table.Column<int>(nullable: false),
                schema_version = table.Column<string>(maxLength: 32, nullable: false),
                sync_status = table.Column<int>(nullable: false),
                sync_mode = table.Column<int>(nullable: false),
                is_menu_synced = table.Column<bool>(nullable: false),
                target_directory = table.Column<string>(maxLength: 500, nullable: true),
                last_generated_time = table.Column<DateTime>(nullable: true),
                last_generated_vue_hash = table.Column<string>(maxLength: 64, nullable: true),
                last_definition_hash = table.Column<string>(maxLength: 64, nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_project_page", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ux_page_component_page_binding",
            table: "sys_page_component_binding",
            columns: new[] { "page_id", "binding_id" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_page_component_page_component",
            table: "sys_page_component_binding",
            columns: new[] { "page_id", "component_id" });
        migrationBuilder.CreateIndex(
            name: "ux_page_data_source_page_key",
            table: "sys_page_data_source",
            columns: new[] { "page_id", "source_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_page_dependency_page_entity_type",
            table: "sys_page_entity_dependency",
            columns: new[] { "page_id", "entity_id", "dependency_type" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_page_layout_preset_key",
            table: "sys_page_layout_preset",
            column: "preset_key",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_project_page_project_key",
            table: "sys_project_page",
            columns: new[] { "project_id", "page_key" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_project_page_project_route",
            table: "sys_project_page",
            columns: new[] { "project_id", "route_path" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "sys_page_component_binding");
        migrationBuilder.DropTable(name: "sys_page_data_source");
        migrationBuilder.DropTable(name: "sys_page_entity_dependency");
        migrationBuilder.DropTable(name: "sys_page_layout_preset");
        migrationBuilder.DropTable(name: "sys_project_page");
    }
}
