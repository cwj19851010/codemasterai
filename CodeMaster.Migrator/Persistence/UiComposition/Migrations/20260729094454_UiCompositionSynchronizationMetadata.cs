using CodeMaster.UiComposition.Migrator.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodeMaster.UiComposition.Migrator.Migrations;

[DbContext(typeof(UiCompositionDbContext))]
[Migration(MigrationId)]
public sealed partial class UiCompositionSynchronizationMetadata : Migration
{
    public const string MigrationId = "20260729094454_UiCompositionSynchronizationMetadata";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "last_synced_time",
            table: "sys_page_data_source",
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "sync_mode",
            table: "sys_page_data_source",
            nullable: false,
            defaultValue: 1);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "last_synced_time", table: "sys_page_data_source");
        migrationBuilder.DropColumn(name: "sync_mode", table: "sys_page_data_source");
    }
}
