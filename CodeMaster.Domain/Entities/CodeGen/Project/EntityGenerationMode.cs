namespace CodeMaster.Domain.Entities.CodeGen;

/// <summary>
/// Controls whether an entity participates in the standard full-stack generation pipeline.
/// </summary>
public enum EntityGenerationMode
{
    /// <summary>
    /// Normal database-backed entity that can generate backend and frontend artifacts.
    /// </summary>
    FullStack = 0,

    /// <summary>
    /// Field-addressable metadata for an external contract. It is not a database entity and
    /// cannot generate code, menus, pages, views, or entity relations.
    /// </summary>
    MetadataOnly = 1
}
