using CodeMaster.Application.Services.CodeGen.Marker;
using Xunit;

namespace CodeMaster.CodeGenerator.Tests.Services;

public class MarkerReplacerTests
{
    [Fact]
    public void ReplaceGen_Resolves_Project_And_Module_Id_Markers()
    {
        // 打印按钮脚本依赖 [gen.projectId]/[gen.moduleId] 按范围查询打印模板
        var ctx = new GenContext
        {
            ProjectId = "500",
            ModuleId = "12"
        };

        var html = MarkerReplacer.ReplaceGen(
            "openEntityPrintReport({ projectId: [gen.projectId], moduleId: [gen.moduleId] }, { id: target.id });", ctx);
        Assert.Contains("projectId: 500", html);
        Assert.Contains("moduleId: 12", html);

        var script = ScriptMarkerReplacer.ReplaceGenInScript(
            "openEntityPrintReport({ projectId: [gen.projectId], moduleId: [gen.moduleId] });", ctx);
        Assert.Contains("projectId: 500", script);
        Assert.Contains("moduleId: 12", script);
    }

    [Fact]
    public void ReplaceGen_Leaves_Project_Module_Markers_Unresolved_When_Empty()
    {
        var ctx = new GenContext();
        var html = MarkerReplacer.ReplaceGen("x = [gen.projectId]", ctx);
        // 未设置时替换为空字符串（与其他 gen 标记行为一致）
        Assert.Equal("x = ", html);
    }

    [Fact]
    public void ReplaceField_Expands_DisplayField_VFor_Nodes()
    {
        var template =
            "<el-table-column [v-for=\"displayField in field.displayFields\"] " +
            ":label=\"$t('[displayField.labelKey]')\" data-gen-id=\"gen_col_[field.id]_[displayField.nameLower]\">" +
            "<template #default=\"scope\">{{ getSelectLabel(scope.row.[field.nameLower], customerOptions, '[displayField.name]') }}</template>" +
            "</el-table-column>";

        var ctx = new FieldContext
        {
            Id = "100",
            NameLower = "customerId",
            DisplayFields =
            {
                new DisplayFieldContext { Name = "Name", NameLower = "name", LabelKey = "name" },
                new DisplayFieldContext { Name = "Phone", NameLower = "phone", LabelKey = "phone" }
            }
        };

        var html = MarkerReplacer.ReplaceField(template, ctx);

        Assert.DoesNotContain("field.displayFields", html);
        Assert.Contains("$t('name')", html);
        Assert.Contains("$t('phone')", html);
        Assert.Contains("gen_col_100_name", html);
        Assert.Contains("gen_col_100_phone", html);
        Assert.Contains("getSelectLabel(scope.row.customerId, customerOptions, 'Name')", html);
        Assert.Contains("getSelectLabel(scope.row.customerId, customerOptions, 'Phone')", html);
    }

    [Fact]
    public void ReplaceField_Expands_DisplayField_VFor_DescriptionItems()
    {
        var template =
            "<el-descriptions-item [v-for=\"displayField in field.displayFields\"] " +
            ":label=\"$t('[displayField.labelKey]')\" data-gen-id=\"gen_field_[field.id]_[displayField.nameLower]\">" +
            "{{ getSelectLabel(detail.[field.nameLower], customerOptions, '[displayField.name]') }}" +
            "</el-descriptions-item>";

        var ctx = new FieldContext
        {
            Id = "100",
            NameLower = "customerId",
            DisplayFields =
            {
                new DisplayFieldContext { Name = "Name", NameLower = "name", LabelKey = "name" },
                new DisplayFieldContext { Name = "Type", NameLower = "type", LabelKey = "type" }
            }
        };

        var html = MarkerReplacer.ReplaceField(template, ctx);

        Assert.DoesNotContain("field.displayFields", html);
        Assert.Contains("$t('name')", html);
        Assert.Contains("$t('type')", html);
        Assert.Contains("gen_field_100_name", html);
        Assert.Contains("gen_field_100_type", html);
        Assert.Contains("getSelectLabel(detail.customerId, customerOptions, 'Name')", html);
        Assert.Contains("getSelectLabel(detail.customerId, customerOptions, 'Type')", html);
    }

    [Fact]
    public void ReplaceField_Does_Not_Expand_Plain_Vue_VFor()
    {
        var template = "<span v-for=\"displayField in field.displayFields\">{{ [displayField.name] }}</span>";
        var ctx = new FieldContext
        {
            DisplayFields =
            {
                new DisplayFieldContext { Name = "Name", NameLower = "name", LabelKey = "name" }
            }
        };

        var html = MarkerReplacer.ReplaceField(template, ctx);

        Assert.Contains("v-for=\"displayField in field.displayFields\"", html);
        Assert.Contains("[displayField.name]", html);
    }
}
