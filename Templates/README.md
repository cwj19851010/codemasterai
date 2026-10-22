# CodeMaster

基于 ASP.NET Core、Vue 3、Element Plus、SqlSugar 和 EF Core Migrator 的独立应用项目。

项目包含通用的登录认证、用户、角色、部门、菜单、租户、字典、语言、日志、任务调度和打印报表运行能力。业务模块可按项目需要继续添加。

## 项目结构

```text
CodeMaster.Core             核心接口、实体基类和通用约定
CodeMaster.Domain           领域实体
CodeMaster.Application      DTO 和应用服务
CodeMaster.Infrastructure   数据访问、认证、缓存、SignalR 和任务调度
CodeMaster.WebApi           ASP.NET Core Web API
CodeMaster.Migrator         EF Core 数据库迁移和种子数据
CodeMaster.SourceGenerator  EF Core 配置源码生成器
CodeMaster.GeneratedTests   生成项目基础测试
CodeMaster.Vue              Vue 3 管理端
modules                     可选的独立运行时模块，包括打印报表功能
```

## 初始化数据库

```powershell
Set-Location CodeMaster.Migrator
dotnet run
```

数据库类型和连接字符串位于 `CodeMaster.Migrator/appsettings.json`。Web API 与 Migrator 应使用同一个业务数据库。

## 启动后端

```powershell
Set-Location CodeMaster.WebApi
dotnet run
```

## 启动前端

```powershell
Set-Location CodeMaster.Vue
npm install
npm run dev
```

前端开发代理目标由 `vite.config.js` 管理，应指向当前项目自己的 Web API。

## 构建

```powershell
dotnet build CodeMaster.sln

Set-Location CodeMaster.Vue
npm run build
```

## 默认账号

- 用户名：`admin`
- 密码：`admin123`

首次部署后请立即修改默认密码以及 JWT 密钥。

## 打印报表

打印报表作为独立运行时模块放在根目录 `modules` 中。WebApi 和 Migrator 构建时会把模块复制到各自输出目录，因此项目不依赖外部平台进程即可完成报表模板读取、预览、打印以及 PDF、Excel、Word 导出。
