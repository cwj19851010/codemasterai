using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionProjectViewDefinition : Migration
{
    public const string MigrationId = "20260817130000_UiCompositionProjectViewDefinition";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sys_project_view_definition",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                definition_key = table.Column<string>(maxLength: 100, nullable: false),
                view_name = table.Column<string>(maxLength: 200, nullable: false),
                schema_name = table.Column<string>(maxLength: 200, nullable: true),
                database_type = table.Column<int>(nullable: false),
                sql_text = table.Column<string>(nullable: true),
                sql_hash = table.Column<string>(maxLength: 64, nullable: true),
                definition_version = table.Column<long>(nullable: false),
                definition_hash = table.Column<string>(maxLength: 64, nullable: false),
                key_column = table.Column<string>(maxLength: 200, nullable: true),
                ownership = table.Column<int>(nullable: false),
                deployment_state = table.Column<int>(nullable: false),
                declared_schema_json = table.Column<string>(nullable: false),
                declared_schema_hash = table.Column<string>(maxLength: 64, nullable: false),
                actual_schema_json = table.Column<string>(nullable: true),
                actual_schema_hash = table.Column<string>(maxLength: 64, nullable: true),
                applied_definition_hash = table.Column<string>(maxLength: 64, nullable: true),
                module_entity_id = table.Column<long>(nullable: true),
                project_data_source_id = table.Column<long>(nullable: true),
                allow_replace = table.Column<bool>(nullable: false),
                last_deployed_at = table.Column<DateTime>(nullable: true),
                last_inspected_at = table.Column<DateTime>(nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_project_view_definition", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_project_view_dependency",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                definition_id = table.Column<long>(nullable: false),
                dependency_key = table.Column<string>(maxLength: 100, nullable: false),
                dependency_kind = table.Column<int>(nullable: false),
                physical_schema = table.Column<string>(maxLength: 200, nullable: true),
                physical_name = table.Column<string>(maxLength: 200, nullable: false),
                source_alias = table.Column<string>(maxLength: 100, nullable: true),
                source_role = table.Column<string>(maxLength: 100, nullable: true),
                module_entity_id = table.Column<long>(nullable: true),
                source_view_definition_id = table.Column<long>(nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_project_view_dependency", x => x.id));

        migrationBuilder.CreateTable(
            name: "sys_project_view_field_lineage",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                project_id = table.Column<long>(nullable: false),
                definition_id = table.Column<long>(nullable: false),
                output_name = table.Column<string>(maxLength: 200, nullable: false),
                output_column = table.Column<string>(maxLength: 200, nullable: false),
                data_type = table.Column<string>(maxLength: 100, nullable: false),
                is_nullable = table.Column<bool>(nullable: false),
                ordinal = table.Column<int>(nullable: false),
                lineage_kind = table.Column<int>(nullable: false),
                source_dependency_id = table.Column<long>(nullable: true),
                source_entity_id = table.Column<long>(nullable: true),
                source_field_id = table.Column<long>(nullable: true),
                expression = table.Column<string>(nullable: true),
                lineage_notes = table.Column<string>(maxLength: 1000, nullable: true),
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
            constraints: table => table.PrimaryKey("PK_sys_project_view_field_lineage", x => x.id));

        migrationBuilder.CreateIndex("ux_project_view_definition_project_key", "sys_project_view_definition", new[] { "tenant_id", "project_id", "definition_key" }, unique: true);
        migrationBuilder.CreateIndex("ux_project_view_definition_project_view", "sys_project_view_definition", new[] { "tenant_id", "project_id", "view_name" }, unique: true);
        migrationBuilder.CreateIndex("ux_project_view_dependency_definition_key", "sys_project_view_dependency", new[] { "definition_id", "dependency_key" }, unique: true);
        migrationBuilder.CreateIndex("ix_project_view_dependency_project", "sys_project_view_dependency", "project_id");
        migrationBuilder.CreateIndex("ux_project_view_lineage_definition_name", "sys_project_view_field_lineage", new[] { "definition_id", "output_name" }, unique: true);
        migrationBuilder.CreateIndex("ux_project_view_lineage_definition_column", "sys_project_view_field_lineage", new[] { "definition_id", "output_column" }, unique: true);
        migrationBuilder.CreateIndex("ix_project_view_lineage_project", "sys_project_view_field_lineage", "project_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("sys_project_view_field_lineage");
        migrationBuilder.DropTable("sys_project_view_dependency");
        migrationBuilder.DropTable("sys_project_view_definition");
    }
}
