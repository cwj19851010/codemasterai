using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Exceptions;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class MetadataDescriptionNormalizerTests
{
    [Fact]
    public void Display_name_is_trimmed_and_limited_to_ui_safe_length()
    {
        Assert.Equal("销售订单", MetadataDescriptionNormalizer.NormalizeDisplayName("  销售订单  ", "实体"));

        var exception = Assert.Throws<BusinessException>(() =>
            MetadataDescriptionNormalizer.NormalizeDisplayName(new string('名', 51), "实体"));

        Assert.Equal(400, exception.Code);
        Assert.Contains("业务说明", exception.Message);
    }

    [Fact]
    public void Business_description_is_optional_trimmed_and_limited()
    {
        Assert.Null(MetadataDescriptionNormalizer.NormalizeBusinessDescription("  ", "字段"));
        Assert.Equal(
            "关联 Customer.Id，保存时必须属于当前租户。",
            MetadataDescriptionNormalizer.NormalizeBusinessDescription(
                "  关联 Customer.Id，保存时必须属于当前租户。  ",
                "字段"));

        var exception = Assert.Throws<BusinessException>(() =>
            MetadataDescriptionNormalizer.NormalizeBusinessDescription(new string('说', 2001), "字段"));

        Assert.Equal(400, exception.Code);
    }
}
