using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionPageProjectDataSource : Migration
{
    public const string MigrationId = "20260806113000_UiCompositionPageProjectDataSource";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "project_data_source_id",
            table: "sys_page_data_source",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_page_data_source_project_source",
            table: "sys_page_data_source",
            column: "project_data_source_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_page_data_source_project_source",
            table: "sys_page_data_source");

        migrationBuilder.DropColumn(
            name: "project_data_source_id",
            table: "sys_page_data_source");
    }
}
