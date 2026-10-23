using CodeMaster.Migrator.SeedData;

namespace CodeMaster.Migrator.SeedData.CodeGen;

/// <summary>
/// 顶级数据源管理目录。实体、数据库视图和 API 数据源在这里统一配置。
/// </summary>
public sealed class DataSourceDirectory : ITopLevelDirectory
{
    public string Name => "数据源管理";
    public string TitleKey => "data_source_management";
    public string Path => "dataSource";
    public string Icon => "DataLine";
    public int Order => 3;
}
