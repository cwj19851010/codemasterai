using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.Application.Services.CodeGen.Relations;

internal static class OneToManyRelationDeduplicator
{
    public static List<OneToManyRelation> Deduplicate(IEnumerable<OneToManyRelation> relations)
    {
        return relations
            .GroupBy(relation => string.Join('|',
                relation.ModuleEntityId,
                relation.ChildEntityId,
                NormalizeField(relation.MasterField),
                NormalizeField(relation.ChildForeignKey)))
            .Select(group => group
                .OrderBy(relation => relation.OrderNum)
                .ThenBy(relation => relation.Id)
                .First())
            .OrderBy(relation => relation.OrderNum)
            .ThenBy(relation => relation.Id)
            .ToList();
    }

    private static string NormalizeField(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}
