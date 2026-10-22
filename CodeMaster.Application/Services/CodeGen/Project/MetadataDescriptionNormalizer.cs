using CodeMaster.Core.Exceptions;

namespace CodeMaster.Application.Services.CodeGen;

public static class MetadataDescriptionNormalizer
{
    public const int DisplayNameMaxLength = 50;
    public const int BusinessDescriptionMaxLength = 2000;

    public static string NormalizeDisplayName(string? value, string subject)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new BusinessException(400, $"{subject}显示名称不能为空。");
        if (normalized.Length > DisplayNameMaxLength)
            throw new BusinessException(400, $"{subject}显示名称不能超过 {DisplayNameMaxLength} 个字符，请将详细业务含义填写到业务说明中。");
        return normalized;
    }

    public static string? NormalizeBusinessDescription(string? value, string subject)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        if (normalized.Length > BusinessDescriptionMaxLength)
            throw new BusinessException(400, $"{subject}业务说明不能超过 {BusinessDescriptionMaxLength} 个字符。");
        return normalized;
    }
}
