using CodeMaster.Infrastructure.VueParser;

namespace CodeMaster.CodeGenerator.Tests.Services;

public sealed class GeneratedVueRegionProtectorTests
{
    private readonly GeneratedVueRegionProtector _protector = new();

    [Fact]
    public void Formatting_and_attribute_order_do_not_create_region_conflicts()
    {
        var generated = Vue("""
          <div>
            <el-input data-gen-id="gen_field_1" class="wide" :model-value="form.name" />
          </div>
        """);
        var baseline = _protector.Analyze(generated);
        var formattedCurrent = Vue("""
        <div><el-input :model-value="form.name"
          class="wide" data-gen-id="gen_field_1"></el-input></div>
        """);

        var analysis = _protector.Analyze(generated, formattedCurrent, baseline.SidecarJson);

        Assert.False(analysis.HasConflicts);
        Assert.Contains("gen_field_1", analysis.Content);
    }

    [Fact]
    public void Modified_region_is_preserved_while_clean_region_is_regenerated()
    {
        var first = Vue("""
        <section>
          <div data-gen-id="gen_field_1">old one</div>
          <div data-gen-id="gen_field_2">old two</div>
        </section>
        """);
        var baseline = _protector.Analyze(first);
        var current = first.Replace("old one", "user one", StringComparison.Ordinal);
        var next = first
            .Replace("old one", "new one", StringComparison.Ordinal)
            .Replace("old two", "new two", StringComparison.Ordinal);

        var analysis = _protector.Analyze(next, current, baseline.SidecarJson);

        Assert.True(analysis.HasConflicts);
        Assert.Equal("gen_field_1", Assert.Single(analysis.Conflicts).GenId);
        Assert.Contains("user one", analysis.Content);
        Assert.DoesNotContain("new one", analysis.Content);
        Assert.Contains("new two", analysis.Content);
    }

    [Fact]
    public void Missing_anchor_degrades_to_the_safe_side_and_unanchored_user_node_is_preserved()
    {
        var first = Vue("""
        <section>
          <div data-gen-id="gen_field_1">generated</div>
        </section>
        """);
        var baseline = _protector.Analyze(first);
        var current = Vue("""
        <section>
          <aside class="user-note">keep me</aside>
        </section>
        """);
        var next = first.Replace("generated", "regenerated", StringComparison.Ordinal);

        var analysis = _protector.Analyze(next, current, baseline.SidecarJson);

        Assert.True(analysis.HasConflicts);
        Assert.Equal("gen_field_1", Assert.Single(analysis.Conflicts).GenId);
        Assert.DoesNotContain("data-gen-id=\"gen_field_1\"", analysis.Content);
        Assert.Contains("user-note", analysis.Content);
        Assert.Contains("keep me", analysis.Content);
    }

    [Fact]
    public async Task WriteAsync_creates_sidecar_and_keeps_conflicts_repeatable()
    {
        var directory = Path.Combine(CodeMasterTestPaths.Root, "codemaster-genhash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var vuePath = Path.Combine(directory, "index.vue");
            var first = Vue("<main><div data-gen-id=\"gen_field_1\">first</div></main>");
            await _protector.WriteAsync(vuePath, first);
            Assert.True(File.Exists(GeneratedVueRegionProtector.GetSidecarPath(vuePath)));

            await File.WriteAllTextAsync(vuePath, first.Replace("first", "manual", StringComparison.Ordinal));
            var next = first.Replace("first", "second", StringComparison.Ordinal);
            var firstConflict = await _protector.WriteAsync(vuePath, next);
            var repeatedConflict = await _protector.WriteAsync(vuePath, next);

            Assert.True(firstConflict.HasConflicts);
            Assert.True(repeatedConflict.HasConflicts);
            Assert.Contains("manual", await File.ReadAllTextAsync(vuePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Normalized_serialization_preserves_stable_entity_metadata()
    {
        var parser = new VueTemplateParser();
        var serializer = new VueTemplateSerializer();

        var components = parser.Parse("""
        <el-table-column data-gen-id="gen_order_name" data-entity-table="Order" data-entity-field="Name" prop="name" />
        """);
        var output = serializer.Serialize(components);

        Assert.Contains("data-gen-id=\"gen_order_name\"", output);
        Assert.Contains("data-entity-table=\"Order\"", output);
        Assert.Contains("data-entity-field=\"Name\"", output);
    }

    [Fact]
    public void Official_sfc_parser_maps_directives_slots_and_preserves_script_setup_source()
    {
        var source = """
            <template>
              <el-table :data="rows" @row-click="selectRow">
                <template #default="{ row }">
                  <span v-if="row.ready" data-gen-id="gen_status">{{ row.name }}</span>
                </template>
              </el-table>
            </template>
            <script setup>
            const rows = ref([])
            const selectRow = row => console.log(row)
            </script>
            """;

        var parsed = new VueTemplateParser().ParseOfficial(source, "official.vue");

        Assert.Equal(VueOfficialSfcParser.ExpectedCompilerVersion, parsed.CompilerVersion);
        Assert.Contains("const rows = ref([])", parsed.ScriptSetupContent);
        var table = Assert.Single(parsed.Components);
        Assert.Contains(table.Props!, item => item.Key == "data" && item.IsBind && item.Value == "rows");
        Assert.Contains(table.Events!, item => item.Name == "row-click" && item.Body == "selectRow");
        var slot = Assert.Single(table.UseSlots!);
        Assert.Equal("default", slot.Name);
        var status = Assert.Single(slot.Components!);
        Assert.Equal("gen_status", status.GenId);
        Assert.Contains(status.Instructions!, item => item.Name == "v-if" && item.Value == "row.ready");
        Assert.Contains(status.Children!, child => child.Tag == "text" && child.Content!.Contains("row.name"));
    }

    private static string Vue(string template) =>
        $"<template>\n{template}\n</template>\n<script setup>\nconst ready = true\n</script>";
}
