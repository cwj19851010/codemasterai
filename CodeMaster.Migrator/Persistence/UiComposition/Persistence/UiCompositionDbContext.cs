using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeMaster.UiComposition.Migrator.Persistence;

public sealed class UiCompositionDbContext(DbContextOptions<UiCompositionDbContext> options)
    : DbContext(options)
{
    public DbSet<ProjectPage> ProjectPages => Set<ProjectPage>();
    public DbSet<PageDataSource> PageDataSources => Set<PageDataSource>();
    public DbSet<PageComponentBinding> PageComponentBindings => Set<PageComponentBinding>();
    public DbSet<PageEntityDependency> PageEntityDependencies => Set<PageEntityDependency>();
    public DbSet<PageLayoutPreset> PageLayoutPresets => Set<PageLayoutPreset>();
    public DbSet<ProjectDataSource> ProjectDataSources => Set<ProjectDataSource>();
    public DbSet<PageDataSourceBinding> PageDataSourceBindings => Set<PageDataSourceBinding>();
    public DbSet<PageDisplayTemplate> PageDisplayTemplates => Set<PageDisplayTemplate>();
    public DbSet<OpenApiDocumentRegistration> OpenApiDocuments => Set<OpenApiDocumentRegistration>();
    public DbSet<OpenApiSchemaBinding> OpenApiSchemaBindings => Set<OpenApiSchemaBinding>();
    public DbSet<OpenApiMemberBinding> OpenApiMemberBindings => Set<OpenApiMemberBinding>();
    public DbSet<ProjectViewDefinition> ProjectViewDefinitions => Set<ProjectViewDefinition>();
    public DbSet<ProjectViewDependency> ProjectViewDependencies => Set<ProjectViewDependency>();
    public DbSet<ProjectViewFieldLineage> ProjectViewFieldLineages => Set<ProjectViewFieldLineage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        UiCompositionModelConfiguration.Configure(modelBuilder, excludeFromMigrations: false);
    }
}
