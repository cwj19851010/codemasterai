using System.Text.Json;
using CodeMaster.Infrastructure.VueParser.Model;

namespace CodeMaster.CodeGenerator.Tests.Services;

public sealed class ComponentSerializationCompatibilityTests
{
    [Fact]
    public void LegacyTreeRoundTripPreservesExistingAndOptionalDesignerMetadata()
    {
        const string legacyJson = """
            [{
              "id":"customer-field",
              "tag":"el-input",
              "genId":"gen_field_42",
              "entityField":"CustomerName",
              "props":[{"key":"model-value","value":"form.customerName","isBind":true,"isSingle":false,"type":"string"}],
              "events":[{"name":"change","expression":"reloadDetails","isCustom":true}],
              "instructions":[{"name":"v-if","value":"visible"}],
              "useSlots":[{"name":"prefix","parameter":"slotProps","components":[{"tag":"span","content":"Customer"}]}],
              "scriptSection":"{\"functions\":[{\"name\":\"reloadDetails\"}]}"
            }]
            """;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var original = JsonSerializer.Deserialize<List<Component>>(legacyJson, options)!;
        original[0].DataSourceId = 100;
        original[0].BindingId = "binding_customer";
        original[0].FieldId = 42;
        original[0].Origin = ComponentOriginKind.User;
        var roundTrip = JsonSerializer.Deserialize<List<Component>>(JsonSerializer.Serialize(original, options), options)!;

        var component = Assert.Single(roundTrip);
        Assert.Equal("CustomerName", component.EntityField);
        Assert.Equal("binding_customer", component.BindingId);
        Assert.Equal(42, component.FieldId);
        Assert.Single(component.Props!);
        Assert.Single(component.Events!);
        Assert.Single(component.Instructions!);
        Assert.Single(component.UseSlots!);
        Assert.Contains("reloadDetails", component.ScriptSection);
    }
}
