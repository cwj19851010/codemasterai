using CodeMaster.Migrator.Persistence.EfCore;
using Microsoft.EntityFrameworkCore;

namespace CodeMaster.UiComposition.Migrator.Persistence;

public sealed class ProjectPageModelContributor : IMigrationModelContributor
{
    public void Configure(ModelBuilder modelBuilder)
        => UiCompositionModelConfiguration.Configure(modelBuilder, excludeFromMigrations: true);
}
