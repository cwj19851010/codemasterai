namespace CodeMaster.Core.Entities;

/// <summary>
/// Marks a generated entity as a read-only database view.
/// EF migration generation maps these entities with ToView instead of creating a table.
/// </summary>
public interface IViewEntity
{
}
