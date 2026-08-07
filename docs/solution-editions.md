# CodeMaster 发布

当前本地发布统一使用完整版本。`CodeMaster.sln`、`CodeMaster.Private.sln`、普通 `FolderProfile` 和直接 `dotnet publish` 都会包含复杂页面后端模块、Agent/MCP 桥接模块以及完整 Vue 前端。

## Visual Studio 发布

1. 打开 `CodeMaster.sln`（`CodeMaster.Private.sln` 也兼容，但不再需要区分）。
2. 在解决方案资源管理器中右键 `CodeMaster.WebApi`，选择“发布”。
3. 使用现有的 `FolderProfile`，发布会自动执行完整 Vue 构建，并将前端放入发布目录的 `wwwroot`。

完整版会把 WebApi、LocalAgent、MCP Server 和 Migrator 所需的复杂页面模块分别复制到各自发布目录的 `modules` 文件夹。

## 命令行发布

```powershell
# 默认完整版本
dotnet publish CodeMaster.WebApi/CodeMaster.WebApi.csproj -c Release -o .artifacts/publish/full

# 其他宿主也默认发布完整版本
dotnet publish CodeMaster.LocalAgent/CodeMaster.LocalAgent.csproj -c Release
dotnet publish CodeMaster.McpServer/CodeMaster.McpServer.csproj -c Release
dotnet publish CodeMaster.Migrator/CodeMaster.Migrator.csproj -c Release
```

只有明确需要公开版或纯后端发布时，才使用 `-p:CodeMasterEdition=Public -p:IncludePrivateModules=false` 或 `-p:SkipCodeMasterVueBuild=true` 覆盖默认值。
