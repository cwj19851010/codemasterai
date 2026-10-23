using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Migrator.Persistence.EfCore;
using CodeMaster.UiComposition.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeMaster.Migrator.SeedData.CodeGen;

/// <summary>
/// Seeds semantic layout trees for the complex-page wizard. These presets only
/// initialize a page; the saved page tree remains independently editable.
/// </summary>
public sealed class ComplexPageLayoutModule : ISeedModule
{
    public string ModuleName => "复杂页面布局";

    public Task<bool> HasMenuAsync(CodeMasterDbContext dbContext) => Task.FromResult(true);

    public Task AddMenusAsync(CodeMasterDbContext dbContext, long adminRoleId) => Task.CompletedTask;

    public async Task AddInitialDataAsync(CodeMasterDbContext dbContext)
    {
        var presets = new[]
        {
            new PageLayoutPreset
            {
                Id = 7001001,
                PresetKey = "blank",
                PresetName = "空白画布",
                Description = "从空白页面开始，自定义行、列和业务组件。",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[]}}""",
                Sort = 1,
                IsSystem = true
            },
            new PageLayoutPreset
            {
                Id = 7001002,
                PresetKey = "query-list",
                PresetName = "查询列表",
                Description = "查询区、分页表格和分页器，适合复合查询页面。",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[{"type":"row","componentId":"query_row","origin":1,"children":[{"type":"column","componentId":"query_column","span":24,"origin":1,"children":[{"type":"search","componentId":"query_panel","origin":1,"children":[]}]}]},{"type":"row","componentId":"list_row","origin":1,"children":[{"type":"column","componentId":"list_column","span":24,"origin":1,"children":[{"type":"table","componentId":"result_table","origin":1,"children":[]},{"type":"pagination","componentId":"result_pagination","origin":1,"children":[]}]}]}]}}""",
                Sort = 2,
                IsSystem = true
            },
            new PageLayoutPreset
            {
                Id = 7001003,
                PresetKey = "list-detail",
                PresetName = "列表详情",
                Description = "左侧集合列表、右侧单体详情，适合联动查看。",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[{"type":"row","componentId":"list_detail_row","origin":1,"children":[{"type":"column","componentId":"list_area","span":14,"origin":1,"children":[{"type":"table","componentId":"master_table","origin":1,"children":[]},{"type":"pagination","componentId":"master_pagination","origin":1,"children":[]}]},{"type":"column","componentId":"detail_area","span":10,"origin":1,"children":[{"type":"detail","componentId":"selected_detail","origin":1,"children":[]}]}]}]}}""",
                Sort = 3,
                IsSystem = true
            },
            new PageLayoutPreset
            {
                Id = 7001004,
                PresetKey = "master-detail",
                PresetName = "主从工作台",
                Description = "主实体表单与明细集合并排，适合录入和编辑业务单据。",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[{"type":"row","componentId":"master_detail_row","origin":1,"children":[{"type":"column","componentId":"master_area","span":8,"origin":1,"children":[{"type":"form","componentId":"master_form","origin":1,"children":[]}]},{"type":"column","componentId":"detail_list_area","span":16,"origin":1,"children":[{"type":"table","componentId":"detail_table","origin":1,"children":[]}]}]}]}}""",
                Sort = 4,
                IsSystem = true
            },
            new PageLayoutPreset
            {
                Id = 7001005,
                PresetKey = "dashboard-list",
                PresetName = "指标与列表",
                Description = "顶部指标卡片、底部业务列表，适合监控和运营看板。",
                LayoutJson = """{"version":1,"root":{"type":"page","componentId":"page_root","origin":1,"children":[{"type":"row","componentId":"metric_row","origin":1,"children":[{"type":"column","componentId":"metric_one","span":8,"origin":1,"children":[{"type":"card","componentId":"metric_card_one","origin":1,"children":[]}]},{"type":"column","componentId":"metric_two","span":8,"origin":1,"children":[{"type":"card","componentId":"metric_card_two","origin":1,"children":[]}]},{"type":"column","componentId":"metric_three","span":8,"origin":1,"children":[{"type":"card","componentId":"metric_card_three","origin":1,"children":[]}]}]},{"type":"row","componentId":"dashboard_list_row","origin":1,"children":[{"type":"column","componentId":"dashboard_list_area","span":24,"origin":1,"children":[{"type":"table","componentId":"dashboard_table","origin":1,"children":[]},{"type":"pagination","componentId":"dashboard_pagination","origin":1,"children":[]}]}]}]}}""",
                Sort = 5,
                IsSystem = true
            }
        };

        foreach (var preset in presets)
        {
            var existing = await dbContext.Set<PageLayoutPreset>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.PresetKey == preset.PresetKey);
            if (existing == null)
            {
                await dbContext.Set<PageLayoutPreset>().AddAsync(preset);
                continue;
            }

            existing.PresetName = preset.PresetName;
            existing.Description = preset.Description;
            existing.LayoutJson = preset.LayoutJson;
            existing.Sort = preset.Sort;
            existing.IsSystem = true;
            existing.IsDeleted = false;
            existing.DeleteTime = null;
        }

        var templates = new[]
        {
            new PageDisplayTemplate
            {
                Id = 7001101,
                TemplateKey = "query-form-space",
                TemplateName = "查询表单横向间距",
                Scope = PageDisplayTemplateScope.Container,
                TargetType = "query-form",
                TemplateContent = "<el-space wrap>[items]</el-space>",
                Description = "将查询字段放入横向可换行的间距容器。",
                Sort = 1,
                IsSystem = true,
                IsEnabled = true
            },
            new PageDisplayTemplate
            {
                Id = 7001102,
                TemplateKey = "edit-form-section",
                TemplateName = "编辑表单分组",
                Scope = PageDisplayTemplateScope.Container,
                TargetType = "edit-form",
                TemplateContent = "<div class=\"display-template-form-section\">[items]</div>",
                Description = "为编辑表单字段提供可复用的分组容器。",
                Sort = 2,
                IsSystem = true,
                IsEnabled = true
            },
            new PageDisplayTemplate
            {
                Id = 7001103,
                TemplateKey = "table-field-link",
                TemplateName = "表格字段链接",
                Scope = PageDisplayTemplateScope.Field,
                TargetType = "table-column",
                TemplateContent = "<a class=\"display-template-link\" :href=\"row.field\">{{ row.field }}</a>",
                Description = "将当前行字段渲染为链接，字段表达式由生成器绑定。",
                Sort = 3,
                IsSystem = true,
                IsEnabled = true
            }
        };

        foreach (var template in templates)
        {
            var existing = await dbContext.Set<PageDisplayTemplate>()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.TemplateKey == template.TemplateKey);
            if (existing == null)
            {
                template.CreateBy = "CodeMaster";
                template.CreateUserId = 0;
                template.CreateTime = DateTime.UtcNow;
                template.TenantId = 0;
                await dbContext.Set<PageDisplayTemplate>().AddAsync(template);
                continue;
            }

            existing.TemplateName = template.TemplateName;
            existing.Scope = template.Scope;
            existing.TargetType = template.TargetType;
            existing.TemplateContent = template.TemplateContent;
            existing.Description = template.Description;
            existing.Sort = template.Sort;
            existing.IsSystem = true;
            existing.IsEnabled = true;
            existing.IsDeleted = false;
            existing.DeleteTime = null;
        }
    }

    public Dictionary<string, Dictionary<string, string>> GetTranslations() => new();
}
