using CodeMaster.Core.Entities;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeMaster.UiComposition.Migrator.Persistence;

internal static class UiCompositionModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        ConfigureProjectPage(modelBuilder, excludeFromMigrations);
        ConfigurePageDataSource(modelBuilder, excludeFromMigrations);
        ConfigurePageComponentBinding(modelBuilder, excludeFromMigrations);
        ConfigurePageEntityDependency(modelBuilder, excludeFromMigrations);
        ConfigurePageLayoutPreset(modelBuilder, excludeFromMigrations);
        ConfigureProjectDataSource(modelBuilder, excludeFromMigrations);
        ConfigurePageDataSourceBinding(modelBuilder, excludeFromMigrations);
        ConfigurePageDisplayTemplate(modelBuilder, excludeFromMigrations);
        ConfigureOpenApiDocument(modelBuilder, excludeFromMigrations);
        ConfigureOpenApiSchemaBinding(modelBuilder, excludeFromMigrations);
        ConfigureOpenApiMemberBinding(modelBuilder, excludeFromMigrations);
        ConfigureProjectViewDefinition(modelBuilder, excludeFromMigrations);
        ConfigureProjectViewDependency(modelBuilder, excludeFromMigrations);
        ConfigureProjectViewFieldLineage(modelBuilder, excludeFromMigrations);
    }

    private static void ConfigureProjectPage(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<ProjectPage>(modelBuilder, "sys_project_page", excludeFromMigrations);
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.ModuleId).HasColumnName("module_id");
        builder.Property(x => x.PageKey).HasColumnName("page_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.PageName).HasColumnName("page_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(x => x.RoutePath).HasColumnName("route_path").HasMaxLength(300).IsRequired();
        builder.Property(x => x.Icon).HasColumnName("icon").HasMaxLength(100);
        builder.Property(x => x.OrderNum).HasColumnName("order_num");
        builder.Property(x => x.PageType).HasColumnName("page_type").HasConversion<int>();
        builder.Property(x => x.LayoutPresetId).HasColumnName("layout_preset_id");
        builder.Property(x => x.LayoutJson).HasColumnName("layout_json").IsRequired();
        builder.Property(x => x.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(x => x.SchemaVersion).HasColumnName("schema_version").HasMaxLength(32).IsRequired();
        builder.Property(x => x.SyncStatus).HasColumnName("sync_status").HasConversion<int>();
        builder.Property(x => x.SyncMode).HasColumnName("sync_mode").HasConversion<int>();
        builder.Property(x => x.IsMenuSynced).HasColumnName("is_menu_synced");
        builder.Property(x => x.TargetDirectory).HasColumnName("target_directory").HasMaxLength(500);
        builder.Property(x => x.LastGeneratedTime).HasColumnName("last_generated_time");
        builder.Property(x => x.LastGeneratedVueHash).HasColumnName("last_generated_vue_hash").HasMaxLength(64);
        builder.Property(x => x.LastDefinitionHash).HasColumnName("last_definition_hash").HasMaxLength(64);
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);
        builder.HasIndex(x => new { x.ProjectId, x.PageKey })
            .IsUnique()
            .HasDatabaseName("ux_project_page_project_key");
        builder.HasIndex(x => new { x.ProjectId, x.RoutePath })
            .IsUnique()
            .HasDatabaseName("ux_project_page_project_route");
    }

    private static void ConfigurePageDataSource(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<PageDataSource>(modelBuilder, "sys_page_data_source", excludeFromMigrations);
        builder.Property(x => x.PageId).HasColumnName("page_id");
        builder.Property(x => x.SourceKey).HasColumnName("source_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceType).HasColumnName("source_type").HasConversion<int>();
        builder.Property(x => x.ProjectDataSourceId).HasColumnName("project_data_source_id");
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.ServiceName).HasColumnName("service_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.MethodName).HasColumnName("method_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DtoName).HasColumnName("dto_name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.DataShape).HasColumnName("data_shape").HasConversion<int>();
        builder.Property(x => x.SyncMode).HasColumnName("sync_mode").HasConversion<int>();
        builder.Property(x => x.ParamsJson).HasColumnName("params_json").IsRequired();
        builder.Property(x => x.SchemaHash).HasColumnName("schema_hash").HasMaxLength(64);
        builder.Property(x => x.LastSyncedTime).HasColumnName("last_synced_time");
        builder.Property(x => x.OrderNum).HasColumnName("order_num");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.HasIndex(x => new { x.PageId, x.SourceKey })
            .IsUnique()
            .HasDatabaseName("ux_page_data_source_page_key");
        builder.HasIndex(x => x.ProjectDataSourceId)
            .HasDatabaseName("ix_page_data_source_project_source");
    }

    private static void ConfigurePageComponentBinding(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<PageComponentBinding>(modelBuilder, "sys_page_component_binding", excludeFromMigrations);
        builder.Property(x => x.PageId).HasColumnName("page_id");
        builder.Property(x => x.ComponentId).HasColumnName("component_id").HasMaxLength(100).IsRequired();
        builder.Property(x => x.BindingId).HasColumnName("binding_id").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DataSourceId).HasColumnName("data_source_id");
        builder.Property(x => x.FieldId).HasColumnName("field_id");
        builder.Property(x => x.BindingRole).HasColumnName("binding_role").HasConversion<int>();
        builder.Property(x => x.BindingStatus).HasColumnName("binding_status").HasConversion<int>();
        builder.Property(x => x.SyncMode).HasColumnName("sync_mode").HasConversion<int>();
        builder.Property(x => x.AutoIncludeNewFields).HasColumnName("auto_include_new_fields");
        builder.Property(x => x.ControlType).HasColumnName("control_type").HasMaxLength(100);
        builder.Property(x => x.PropsJson).HasColumnName("props_json").IsRequired();
        builder.Property(x => x.LabelOverride).HasColumnName("label_override").HasMaxLength(200);
        builder.Property(x => x.Origin).HasColumnName("origin").HasConversion<int>();
        builder.Property(x => x.GeneratedGenId).HasColumnName("generated_gen_id").HasMaxLength(160).IsRequired();
        builder.Property(x => x.LastKnownPath).HasColumnName("last_known_path").HasMaxLength(500);
        builder.HasIndex(x => new { x.PageId, x.ComponentId })
            .HasDatabaseName("ux_page_component_page_component");
        builder.HasIndex(x => new { x.PageId, x.BindingId })
            .IsUnique()
            .HasDatabaseName("ux_page_component_page_binding");
    }

    private static void ConfigurePageEntityDependency(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<PageEntityDependency>(modelBuilder, "sys_page_entity_dependency", excludeFromMigrations);
        builder.Property(x => x.PageId).HasColumnName("page_id");
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.DataSourceId).HasColumnName("data_source_id");
        builder.Property(x => x.DependencyType).HasColumnName("dependency_type").HasConversion<int>();
        builder.Property(x => x.FieldIdsJson).HasColumnName("field_ids_json").IsRequired();
        builder.Property(x => x.SummaryJson).HasColumnName("summary_json").IsRequired();
        builder.Property(x => x.SchemaHash).HasColumnName("schema_hash").HasMaxLength(64);
        builder.HasIndex(x => new { x.PageId, x.EntityId, x.DependencyType })
            .IsUnique()
            .HasDatabaseName("ux_page_dependency_page_entity_type");
    }

    private static void ConfigurePageLayoutPreset(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<PageLayoutPreset>(modelBuilder, "sys_page_layout_preset", excludeFromMigrations);
        builder.Property(x => x.PresetKey).HasColumnName("preset_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.PresetName).HasColumnName("preset_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(x => x.LayoutJson).HasColumnName("layout_json").IsRequired();
        builder.Property(x => x.Sort).HasColumnName("sort");
        builder.Property(x => x.IsSystem).HasColumnName("is_system");
        builder.HasIndex(x => x.PresetKey)
            .IsUnique()
            .HasDatabaseName("ux_page_layout_preset_key");
    }

    private static void ConfigureProjectDataSource(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<ProjectDataSource>(modelBuilder, "sys_project_data_source", excludeFromMigrations);
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.SourceKey).HasColumnName("source_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceType).HasColumnName("source_type").HasConversion<int>();
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.ServiceName).HasColumnName("service_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.MethodName).HasColumnName("method_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DtoName).HasColumnName("dto_name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.DataShape).HasColumnName("data_shape").HasConversion<int>();
        builder.Property(x => x.SyncMode).HasColumnName("sync_mode").HasConversion<int>();
        builder.Property(x => x.ParamsJson).HasColumnName("params_json").IsRequired();
        builder.Property(x => x.ViewName).HasColumnName("view_name").HasMaxLength(200);
        builder.Property(x => x.OpenApiDocumentId).HasColumnName("openapi_document_id");
        builder.Property(x => x.OperationKey).HasColumnName("operation_key").HasMaxLength(500);
        builder.Property(x => x.HttpMethod).HasColumnName("http_method").HasMaxLength(20);
        builder.Property(x => x.RoutePath).HasColumnName("route_path").HasMaxLength(1000);
        builder.Property(x => x.Summary).HasColumnName("summary").HasMaxLength(500);
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.RequestEntityId).HasColumnName("request_entity_id");
        builder.Property(x => x.ResponseEntityId).HasColumnName("response_entity_id");
        builder.Property(x => x.OperationContractJson).HasColumnName("operation_contract_json");
        builder.Property(x => x.OpenApiSyncState).HasColumnName("openapi_sync_state").HasConversion<int?>();
        builder.Property(x => x.SchemaHash).HasColumnName("schema_hash").HasMaxLength(64);
        builder.Property(x => x.LastSyncedTime).HasColumnName("last_synced_time");
        builder.Property(x => x.OrderNum).HasColumnName("order_num");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.HasIndex(x => new { x.ProjectId, x.SourceKey })
            .IsUnique()
            .HasDatabaseName("ux_project_data_source_project_key");
    }

    private static void ConfigurePageDataSourceBinding(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<PageDataSourceBinding>(modelBuilder, "sys_page_data_source_binding", excludeFromMigrations);
        builder.Property(x => x.PageId).HasColumnName("page_id");
        builder.Property(x => x.ModuleEntityId).HasColumnName("module_entity_id");
        builder.Property(x => x.PageType).HasColumnName("page_type").HasMaxLength(50);
        builder.Property(x => x.DataSourceId).HasColumnName("data_source_id");
        builder.Property(x => x.BindingRole).HasColumnName("binding_role").HasConversion<int>();
        builder.Property(x => x.BindingStatus).HasColumnName("binding_status").HasConversion<int>();
        builder.Property(x => x.SyncMode).HasColumnName("sync_mode").HasConversion<int>();
        builder.Property(x => x.LastKnownSchemaHash).HasColumnName("last_known_schema_hash").HasMaxLength(64);
        builder.HasIndex(x => new { x.PageId, x.DataSourceId })
            .IsUnique()
            .HasDatabaseName("ux_page_ds_binding_page_source");
        builder.HasIndex(x => new { x.ModuleEntityId, x.PageType, x.DataSourceId })
            .IsUnique()
            .HasDatabaseName("ux_page_ds_binding_entity_page_source");
    }

    private static void ConfigurePageDisplayTemplate(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<PageDisplayTemplate>(modelBuilder, "sys_page_display_template", excludeFromMigrations);
        builder.Property(x => x.TemplateKey).HasColumnName("template_key").HasMaxLength(120).IsRequired();
        builder.Property(x => x.TemplateName).HasColumnName("template_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Scope).HasColumnName("scope").HasConversion<int>();
        builder.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(100);
        builder.Property(x => x.TemplateContent).HasColumnName("template_content").IsRequired();
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(x => x.Sort).HasColumnName("sort");
        builder.Property(x => x.IsSystem).HasColumnName("is_system");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        builder.HasIndex(x => new { x.TenantId, x.TemplateKey })
            .IsUnique()
            .HasDatabaseName("ux_page_display_template_tenant_key");
    }

    private static void ConfigureOpenApiDocument(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<OpenApiDocumentRegistration>(modelBuilder, "sys_openapi_document", excludeFromMigrations);
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.ModuleId).HasColumnName("module_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(2000);
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(300);
        builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(x => x.OpenApiVersion).HasColumnName("openapi_version").HasMaxLength(50).IsRequired();
        builder.Property(x => x.DocumentHash).HasColumnName("document_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.RawDocument).HasColumnName("raw_document").IsRequired();
        builder.Property(x => x.NormalizedSnapshotJson).HasColumnName("normalized_snapshot_json").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        builder.Property(x => x.LastSyncedTime).HasColumnName("last_synced_time");
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);
        builder.HasIndex(x => new { x.TenantId, x.ProjectId, x.Name })
            .IsUnique()
            .HasDatabaseName("ux_openapi_document_project_name");
    }

    private static void ConfigureOpenApiSchemaBinding(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<OpenApiSchemaBinding>(modelBuilder, "sys_openapi_schema_binding", excludeFromMigrations);
        builder.Property(x => x.DocumentId).HasColumnName("document_id");
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.ModuleId).HasColumnName("module_id");
        builder.Property(x => x.ModuleEntityId).HasColumnName("module_entity_id");
        builder.Property(x => x.SchemaKey).HasColumnName("schema_key").HasMaxLength(500).IsRequired();
        builder.Property(x => x.SchemaName).HasColumnName("schema_name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.DtoName).HasColumnName("dto_name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.DtoRole).HasColumnName("dto_role").HasConversion<int>();
        builder.Property(x => x.SchemaHash).HasColumnName("schema_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.SnapshotJson).HasColumnName("snapshot_json").IsRequired();
        builder.Property(x => x.SyncState).HasColumnName("sync_state").HasConversion<int>();
        builder.Property(x => x.LastSyncedTime).HasColumnName("last_synced_time");
        builder.HasIndex(x => new { x.DocumentId, x.SchemaKey })
            .IsUnique()
            .HasDatabaseName("ux_openapi_schema_document_key");
        builder.HasIndex(x => x.ModuleEntityId)
            .IsUnique()
            .HasDatabaseName("ux_openapi_schema_entity");
    }

    private static void ConfigureOpenApiMemberBinding(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<OpenApiMemberBinding>(modelBuilder, "sys_openapi_member_binding", excludeFromMigrations);
        builder.Property(x => x.DocumentId).HasColumnName("document_id");
        builder.Property(x => x.SchemaBindingId).HasColumnName("schema_binding_id");
        builder.Property(x => x.EntityFieldId).HasColumnName("entity_field_id");
        builder.Property(x => x.TargetSchemaBindingId).HasColumnName("target_schema_binding_id");
        builder.Property(x => x.MemberKey).HasColumnName("member_key").HasMaxLength(700).IsRequired();
        builder.Property(x => x.ExternalId).HasColumnName("external_id").HasMaxLength(300);
        builder.Property(x => x.JsonName).HasColumnName("json_name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.MemberName).HasColumnName("member_name").HasMaxLength(300).IsRequired();
        builder.Property(x => x.MemberKind).HasColumnName("member_kind").HasConversion<int>();
        builder.Property(x => x.DataType).HasColumnName("data_type").HasMaxLength(300);
        builder.Property(x => x.Format).HasColumnName("format").HasMaxLength(100);
        builder.Property(x => x.IsRequired).HasColumnName("is_required");
        builder.Property(x => x.IsNullable).HasColumnName("is_nullable");
        builder.Property(x => x.IsCollection).HasColumnName("is_collection");
        builder.Property(x => x.MemberHash).HasColumnName("member_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.SnapshotJson).HasColumnName("snapshot_json").IsRequired();
        builder.Property(x => x.SyncState).HasColumnName("sync_state").HasConversion<int>();
        builder.Property(x => x.LastSyncedTime).HasColumnName("last_synced_time");
        builder.HasIndex(x => new { x.SchemaBindingId, x.MemberKey })
            .IsUnique()
            .HasDatabaseName("ux_openapi_member_schema_key");
        builder.HasIndex(x => x.EntityFieldId)
            .HasDatabaseName("ix_openapi_member_field");
    }

    private static void ConfigureProjectViewDefinition(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<ProjectViewDefinition>(modelBuilder, "sys_project_view_definition", excludeFromMigrations);
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.DefinitionKey).HasColumnName("definition_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.ViewName).HasColumnName("view_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.SchemaName).HasColumnName("schema_name").HasMaxLength(200);
        builder.Property(x => x.DatabaseType).HasColumnName("database_type").HasConversion<int>();
        builder.Property(x => x.Sql).HasColumnName("sql_text").IsRequired(false);
        builder.Property(x => x.SqlHash).HasColumnName("sql_hash").HasMaxLength(64);
        builder.Property(x => x.DefinitionVersion).HasColumnName("definition_version");
        builder.Property(x => x.DefinitionHash).HasColumnName("definition_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.KeyColumn).HasColumnName("key_column").HasMaxLength(200);
        builder.Property(x => x.Ownership).HasColumnName("ownership").HasConversion<int>();
        builder.Property(x => x.DeploymentState).HasColumnName("deployment_state").HasConversion<int>();
        builder.Property(x => x.DeclaredSchemaJson).HasColumnName("declared_schema_json").IsRequired();
        builder.Property(x => x.DeclaredSchemaHash).HasColumnName("declared_schema_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ActualSchemaJson).HasColumnName("actual_schema_json");
        builder.Property(x => x.ActualSchemaHash).HasColumnName("actual_schema_hash").HasMaxLength(64);
        builder.Property(x => x.AppliedDefinitionHash).HasColumnName("applied_definition_hash").HasMaxLength(64);
        builder.Property(x => x.ModuleEntityId).HasColumnName("module_entity_id");
        builder.Property(x => x.ProjectDataSourceId).HasColumnName("project_data_source_id");
        builder.Property(x => x.AllowReplace).HasColumnName("allow_replace");
        builder.Property(x => x.LastDeployedAt).HasColumnName("last_deployed_at");
        builder.Property(x => x.LastInspectedAt).HasColumnName("last_inspected_at");
        builder.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);
        builder.HasIndex(x => new { x.TenantId, x.ProjectId, x.DefinitionKey })
            .IsUnique().HasDatabaseName("ux_project_view_definition_project_key");
        builder.HasIndex(x => new { x.TenantId, x.ProjectId, x.ViewName })
            .IsUnique().HasDatabaseName("ux_project_view_definition_project_view");
    }

    private static void ConfigureProjectViewDependency(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<ProjectViewDependency>(modelBuilder, "sys_project_view_dependency", excludeFromMigrations);
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.DefinitionId).HasColumnName("definition_id");
        builder.Property(x => x.DependencyKey).HasColumnName("dependency_key").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Kind).HasColumnName("dependency_kind").HasConversion<int>();
        builder.Property(x => x.PhysicalSchema).HasColumnName("physical_schema").HasMaxLength(200);
        builder.Property(x => x.PhysicalName).HasColumnName("physical_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.SourceAlias).HasColumnName("source_alias").HasMaxLength(100);
        builder.Property(x => x.SourceRole).HasColumnName("source_role").HasMaxLength(100);
        builder.Property(x => x.ModuleEntityId).HasColumnName("module_entity_id");
        builder.Property(x => x.SourceViewDefinitionId).HasColumnName("source_view_definition_id");
        builder.HasIndex(x => new { x.DefinitionId, x.DependencyKey })
            .IsUnique().HasDatabaseName("ux_project_view_dependency_definition_key");
        builder.HasIndex(x => x.ProjectId).HasDatabaseName("ix_project_view_dependency_project");
    }

    private static void ConfigureProjectViewFieldLineage(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        var builder = ConfigureEntity<ProjectViewFieldLineage>(modelBuilder, "sys_project_view_field_lineage", excludeFromMigrations);
        builder.Property(x => x.ProjectId).HasColumnName("project_id");
        builder.Property(x => x.DefinitionId).HasColumnName("definition_id");
        builder.Property(x => x.OutputName).HasColumnName("output_name").HasMaxLength(200).IsRequired();
        builder.Property(x => x.OutputColumn).HasColumnName("output_column").HasMaxLength(200).IsRequired();
        builder.Property(x => x.DataType).HasColumnName("data_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.IsNullable).HasColumnName("is_nullable");
        builder.Property(x => x.Ordinal).HasColumnName("ordinal");
        builder.Property(x => x.LineageKind).HasColumnName("lineage_kind").HasConversion<int>();
        builder.Property(x => x.SourceDependencyId).HasColumnName("source_dependency_id");
        builder.Property(x => x.SourceEntityId).HasColumnName("source_entity_id");
        builder.Property(x => x.SourceFieldId).HasColumnName("source_field_id");
        builder.Property(x => x.Expression).HasColumnName("expression");
        builder.Property(x => x.LineageNotes).HasColumnName("lineage_notes").HasMaxLength(1000);
        builder.HasIndex(x => new { x.DefinitionId, x.OutputName })
            .IsUnique().HasDatabaseName("ux_project_view_lineage_definition_name");
        builder.HasIndex(x => new { x.DefinitionId, x.OutputColumn })
            .IsUnique().HasDatabaseName("ux_project_view_lineage_definition_column");
        builder.HasIndex(x => x.ProjectId).HasDatabaseName("ix_project_view_lineage_project");
    }

    private static EntityTypeBuilder<TEntity> ConfigureEntity<TEntity>(
        ModelBuilder modelBuilder,
        string tableName,
        bool excludeFromMigrations)
        where TEntity : EntityBaseWithTenant
    {
        var builder = modelBuilder.Entity<TEntity>();
        if (excludeFromMigrations)
        {
            builder.ToTable(tableName, table => table.ExcludeFromMigrations());
        }
        else
        {
            builder.ToTable(tableName);
        }

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.CreateBy).HasColumnName("create_by").HasMaxLength(64);
        builder.Property(x => x.CreateUserId).HasColumnName("create_user_id");
        builder.Property(x => x.CreateTime).HasColumnName("create_time");
        builder.Property(x => x.UpdateUserId).HasColumnName("update_user_id");
        builder.Property(x => x.UpdateBy).HasColumnName("update_by").HasMaxLength(64);
        builder.Property(x => x.UpdateTime).HasColumnName("update_time");
        builder.Property(x => x.Remark).HasColumnName("remark").HasMaxLength(500);
        builder.Property(x => x.IsDeleted).HasColumnName("is_deleted");
        builder.Property(x => x.DeleteBy).HasColumnName("delete_by").HasMaxLength(64);
        builder.Property(x => x.DeleteTime).HasColumnName("delete_time");
        builder.Property(x => x.DeleteUserId).HasColumnName("delete_user_id");
        builder.Property(x => x.TenantId).HasColumnName("tenant_id");
        return builder;
    }
}
