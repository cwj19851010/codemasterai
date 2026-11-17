namespace CodeMaster.WebApi.ContentRendering;

/// <summary>
/// Presentation helpers shared by the community views.
/// </summary>
public static class CommunityViewHelpers
{
    private static readonly string[] AvatarPalette =
    [
        "#8a4b32",
        "#3f5a52",
        "#5a4b6b",
        "#7a5a2e",
        "#3f5068",
        "#6b4234"
    ];

    public static string Iso(DateTime value)
    {
        return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O");
    }

    public static string Initial(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var trimmed = name.Trim();
        return char.ToUpperInvariant(trimmed[0]).ToString();
    }

    public static string AvatarColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return AvatarPalette[0];
        }

        var hash = 0;
        foreach (var ch in name.Trim())
        {
            hash = (hash * 31 + ch) & 0x7FFFFFFF;
        }

        return AvatarPalette[hash % AvatarPalette.Length];
    }
}
