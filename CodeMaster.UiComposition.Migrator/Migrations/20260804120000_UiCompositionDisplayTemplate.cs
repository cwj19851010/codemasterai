using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionDisplayTemplate : Migration
{
    public const string MigrationId = "20260804120000_UiCompositionDisplayTemplate";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "sys_page_display_template",
            columns: table => new
            {
                id = table.Column<long>(nullable: false),
                template_key = table.Column<string>(maxLength: 120, nullable: false),
                template_name = table.Column<string>(maxLength: 200, nullable: false),
                scope = table.Column<int>(nullable: false),
                target_type = table.Column<string>(maxLength: 100, nullable: true),
                template_content = table.Column<string>(nullable: false),
                description = table.Column<string>(maxLength: 500, nullable: true),
                sort = table.Column<int>(nullable: false),
                is_system = table.Column<bool>(nullable: false),
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
            constraints: table => table.PrimaryKey("PK_sys_page_display_template", x => x.id));

        migrationBuilder.CreateIndex(
            name: "ux_page_display_template_tenant_key",
            table: "sys_page_display_template",
            columns: new[] { "tenant_id", "template_key" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "sys_page_display_template");
    }
}
