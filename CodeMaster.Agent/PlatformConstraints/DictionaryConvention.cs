namespace CodeMaster.Agent.PlatformConstraints;

/// <summary>
/// Rules for business dictionaries. A field only stores the dictionary code, so the dictionary
/// itself has to be created for the generated select to have anything to render.
/// </summary>
public sealed record DictionaryConvention
{
    /// <summary>Plan section that carries dictionary definitions; compiled into its own task.</summary>
    public string PlanSection => "dictionaries";

    public string NamingRule =>
        "Use snake_case dictType derived from the entity and the business meaning (contract_status, payment_method). Keep it stable: an existing dictType must never be renamed, because generated columns already store those values.";

    public string DeclarationRule =>
        "Every field that sets dictionary must also appear in the plan's dictionaries list. A referenced-but-undeclared dictionary still produces valid metadata, but the generated select renders empty.";

    public string EntryRule =>
        "Entries (label/value) are optional. Provide them when the requirement enumerates the values; otherwise create the type only and let the user fill entries in the generated project's dictionary page.";

    public string EnumFieldRule =>
        "Fields named Status, Type, Category, Level, Stage, Method, Kind or similar are enumerations: declare them as string + dictionary instead of a free-text input.";

    public string RenderForPrompt() =>
        $"""
        Dictionary conventions:
        - Plan section `{PlanSection}` carries the dictionary types for this project.
        - {EnumFieldRule}
        - {NamingRule}
        - {DeclarationRule}
        - {EntryRule}
        """;
}
