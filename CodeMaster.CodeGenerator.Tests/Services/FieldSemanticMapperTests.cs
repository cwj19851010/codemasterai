using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.CodeGen;

namespace CodeMaster.CodeGenerator.Tests.Services;

public sealed class FieldSemanticMapperTests
{
    [Theory]
    [InlineData("int", "number")]
    [InlineData("decimal?", "number")]
    [InlineData("bool", "switch")]
    [InlineData("DateTime?", "datetime")]
    [InlineData("byte[]", "upload")]
    public void Infers_control_from_data_type(string dataType, string expectedControl)
    {
        var result = FieldSemanticMapper.Map(new EntityField
        {
            DataType = dataType,
            FormControlType = "auto"
        });

        Assert.Equal(expectedControl, result.ControlType);
    }

    [Fact]
    public void Infers_select_and_props_from_field_semantics()
    {
        var result = FieldSemanticMapper.Map(new EntityField
        {
            DataType = "string",
            FormControlType = "auto",
            IsMultiple = true,
            MaxLength = 120,
            IsEmail = true
        });

        Assert.Equal("select", result.ControlType);
        Assert.Equal(true, result.Props["multiple"]);
        Assert.Equal(120, result.Props["maxlength"]);
        Assert.Equal("email", result.Props["inputType"]);
    }

    [Fact]
    public void Explicit_control_overrides_inference()
    {
        var result = FieldSemanticMapper.Map(new EntityField
        {
            DataType = "bool",
            FormControlType = "textarea"
        });

        Assert.Equal("textarea", result.ControlType);
    }
}
