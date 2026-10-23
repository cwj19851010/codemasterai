using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using CodeMaster.UiComposition.Entities;
using SqlSugar;
using Yitter.IdGenerator;

namespace CodeMaster.UiComposition.Tests;

public sealed class PageDisplayTemplateTests : IDisposable
{
    private readonly SqlSugarClient _db;

    public PageDisplayTemplateTests()
    {
        YitIdHelper.SetIdGenerator(new IdGeneratorOptions { WorkerId = 3 });
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={Path.Combine(CodeMasterTestPaths.Root, $"display_template_{Guid.NewGuid():N}.db")};",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(DbType.Sqlite)
        });
        _db.CodeFirst.InitTables(typeof(PageDisplayTemplate));
    }

    [Fact]
    public void Container_markers_support_all_items_and_sequential_items()
    {
        var result = PageDisplayTemplateContract.ReplaceContainer(
            "<section>[item]<em>[item]</em><footer>[items]</footer></section>",
            new[] { "<span>A</span>", "<span>B</span>" });

        Assert.Contains("<span>A</span>", result);
        Assert.Contains("<em><span>B</span></em>", result);
        Assert.Contains("<footer><span>A</span>\r\n<span>B</span></footer>", result);
    }

    [Fact]
    public void Field_marker_replaces_generic_and_specific_row_expressions()
    {
        var result = PageDisplayTemplateContract.ReplaceField(
            "<a>{{ row.field }}</a><b>{{ row.CustomerName }}</b>",
            "scope.row.customerName",
            "CustomerName");

        Assert.Equal("<a>{{ scope.row.customerName }}</a><b>{{ scope.row.customerName }}</b>", result);
    }

    [Fact]
    public void Invalid_fragments_are_rejected_before_generation()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            PageDisplayTemplateContract.Validate("<div>[items]", PageDisplayTemplateScope.Container));
        Assert.Contains("未闭合", error.Message);
        Assert.Throws<ArgumentException>(() =>
            PageDisplayTemplateContract.Validate("<script>alert(1)</script>", PageDisplayTemplateScope.Field));
    }

    [Fact]
    public async Task Display_template_crud_validates_and_round_trips_fragments()
    {
        var service = new PageDisplayTemplateService(_db);
        var id = await service.CreateAsync(new CodeMaster.Application.Dtos.CodeGen.CreatePageDisplayTemplateDto
        {
            TemplateKey = "crud-template",
            TemplateName = "CRUD template",
            Scope = PageDisplayTemplateScope.Container,
            TargetType = "query-form",
            TemplateContent = "<div>[items]</div>",
            IsSystem = false
        });

        var created = await service.GetByIdAsync(id);
        Assert.Equal("crud-template", created!.TemplateKey);
        Assert.Single(await service.GetListAsync(new CodeMaster.Application.Dtos.CodeGen.PageDisplayTemplateQueryDto
        {
            Scope = PageDisplayTemplateScope.Container,
            TargetType = "query-form"
        }));

        await service.UpdateAsync(id, new CodeMaster.Application.Dtos.CodeGen.UpdatePageDisplayTemplateDto
        {
            TemplateKey = "crud-template",
            TemplateName = "Updated template",
            Scope = PageDisplayTemplateScope.Container,
            TargetType = "query-form",
            TemplateContent = "<section>[item]</section>",
            IsSystem = false
        });
        Assert.Equal("Updated template", (await service.GetByIdAsync(id))!.TemplateName);
        Assert.Equal(1, await service.DeleteAsync(id));
        Assert.Null(await service.GetByIdAsync(id));
    }

    public void Dispose() => _db.Dispose();
}
