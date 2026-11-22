using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionProjectDataSource : Migration
{
    public const string MigrationId = "20260802201826_UiCompositionProjectDataSource";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sys_page_data_source_binding",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                page_id = table.Column<long>(nullable: true),
                module_entity_id = table.Column<long>(nullable: true),
                page_type = table.Column<string>(maxLength: 50, nullable: true),
                data_source_id = table.Column<long>(nullable: false),
                binding_role = table.Column<int>(nullable: false),
                binding_status = table.Column<int>(nullable: false),
                sync_mode = table.Column<int>(nullable: false),
                last_known_schema_hash = table.Column<string>(maxLength: 64, nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_page_data_source_binding", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_project_data_source",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                source_key = table.Column<string>(maxLength: 100, nullable: false),
                display_name = table.Column<string>(maxLength: 200, nullable: false),
                source_type = table.Column<int>(nullable: false),
                entity_id = table.Column<long>(nullable: true),
                service_name = table.Column<string>(maxLength: 200, nullable: false),
                method_name = table.Column<string>(maxLength: 200, nullable: false),
                dto_name = table.Column<string>(maxLength: 300, nullable: false),
                data_shape = table.Column<int>(nullable: false),
                sync_mode = table.Column<int>(nullable: false),
                params_json = table.Column<string>(nullable: false),
                view_name = table.Column<string>(maxLength: 200, nullable: true),
                schema_hash = table.Column<string>(maxLength: 64, nullable: true),
                last_synced_time = table.Column<DateTime>(nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_project_data_source", x => x.id));

        var sqlServer = ActiveProvider.Contains("SqlServer", StringComparison.OrdinalIgnoreCase);
        migrationBuilder.CreateIndex(
            name: "ux_page_ds_binding_page_source",
            table: "sys_page_data_source_binding",
            columns: new[] { "page_id", "data_source_id" },
            unique: true,
            filter: sqlServer ? "[page_id] IS NOT NULL" : null);
        migrationBuilder.CreateIndex(
            name: "ux_page_ds_binding_entity_page_source",
            table: "sys_page_data_source_binding",
            columns: new[] { "module_entity_id", "page_type", "data_source_id" },
            unique: true,
            filter: sqlServer ? "[module_entity_id] IS NOT NULL AND [page_type] IS NOT NULL" : null);
        migrationBuilder.CreateIndex(
            name: "ux_project_data_source_project_key",
            table: "sys_project_data_source",
            columns: new[] { "project_id", "source_key" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "sys_page_data_source_binding");
        migrationBuilder.DropTable(name: "sys_project_data_source");
    }
}
