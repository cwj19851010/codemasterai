# CodeMasterAI

**面向 .NET 10 + Vue 3 的 AI 辅助低代码开发平台，从可视化建模直接生成可独立运行、可继续维护的前后端源码。**

[在线体验](http://124.221.136.83:8000) · [介绍视频](https://www.bilibili.com/video/BV1Cxg96nE73/) · [Gitee](https://gitee.com/cwj19851010/codemasterai) · [GitHub](https://github.com/cwj19851010/codemasterai) · [English](README.en.md)

CodeMasterAI 面向企业管理系统、内部业务平台和定制化软件交付场景。开发者可以在平台中定义项目、模块、实体、字段、关系和页面控件，再生成包含后端服务、DTO、数据库迁移、权限菜单、国际化资源和 Vue 页面在内的完整工程。

它不是只能运行在平台内部的页面搭建器。生成结果是标准的 **ASP.NET Core + Vue 3** 源码，可以脱离 CodeMasterAI 独立构建、部署和二次开发；平台同时提供模板、增量生成、MCP 和本地客户端能力，让重复的工程工作可以持续自动化。

> 当前状态：项目处于持续迭代阶段。核心框架、代码生成、模板设计器、MCP 集成和 Tauri 客户端流程已经可用，模板契约和部分交互仍会继续完善。
>
> 项目品牌已由 CodeMaster 更新为 CodeMasterAI。为保持现有工程兼容性，解决方案、命名空间和部分客户端标识暂时保留 `CodeMaster.*`。

## 演示视频

[![CodeMasterAI 完整介绍视频：自带 Agent 的低代码快速开发平台](https://i0.hdslb.com/bfs/archive/d89000ccb3ac6bd309a2401c05144fc45913007d.jpg)](https://www.bilibili.com/video/BV1Cxg96nE73/)

[在 Bilibili 观看《自带Agent的全新低代码快速开发平台》](https://www.bilibili.com/video/BV1Cxg96nE73/)（2 分 24 秒）

视频展示了可视化建模、Vue 3 页面设计、完整代码生成、智能增量更新、内置 Agent、MCP/Skill 连接和多数据库支持。视频发布时项目仍使用 CodeMaster 名称，与当前 CodeMasterAI 为同一项目。

## 从建模到交付

```text
创建项目与模块
      ↓
设计实体、字段和一对一/一对多关系
      ↓
配置列表、表单、详情及字段控件
      ↓
生成后端、前端、权限、菜单和迁移数据
      ↓
运行并验证生成项目
      ↓
通过模板、增量生成或 AI Agent 持续迭代
```

## 适用场景

- 企业后台、ERP、CRM、MES、WMS 等数据密集型管理系统
- 需要统一权限、菜单、字典、多租户和审计能力的内部平台
- 希望沉淀自有开发框架、控件模板和代码规范的技术团队
- 需要让 AI 通过结构化工具参与建模、生成和维护的工程场景

## 核心能力

- **约定式建模**：定义项目、模块、实体、字段和关系后，自动生成后端服务、DTO、前端页面、菜单、权限、语言资源和迁移数据。
- **动态 API**：应用服务可按约定自动暴露为 REST API，也可以通过特性调整路由和权限规则，减少重复的 Controller 代码。
- **自动权限**：根据服务、菜单、按钮和生成规则自动维护权限编码，降低权限遗漏风险。
- **模板化代码生成**：页面模板、字段控件模板和子表模板存储在数据库中，可通过系统界面维护，支持一对一、一对多和复杂选项控件生成。
- **全量与增量生成**：既能从零生成完整项目，也能更新已有工程，并通过结构化脚本区尽量保留用户定制内容。
- **可视化页面设计**：实体设计器覆盖常用 Element Plus 控件，组件树、属性和关联脚本可以协同编辑。
- **MCP 智能体集成**：AI 可以通过 MCP 工具新增/编辑项目、模块、实体、字段，执行初始化、生成代码和启动服务。
- **Tauri + LocalAgent 客户端**：桌面客户端可调用本地能力，例如本地初始化、生成代码、启动生成项目的前后端。
- **多数据库支持**：运行时和迁移流程覆盖 SQL Server、MySQL、PostgreSQL、SQLite 与 Oracle，可按项目部署环境选择 Provider。
- **多租户基础能力**：内置租户上下文、菜单范围、JWT 租户声明和数据权限扩展点。
- **企业开发栈**：ASP.NET Core、SqlSugar、EF Core Migrator、Vue 3、Element Plus、Vite、SignalR、Quartz、Redis 可选缓存。

## 产品截图

### 项目实体管理

![项目实体管理](CodeMaster.WebApi/wwwroot/site/images/product-project-list.png)

### 模板配置

![模板配置](CodeMaster.WebApi/wwwroot/site/images/product-template-config.png)

### 实体设计器

![实体设计器](CodeMaster.WebApi/wwwroot/site/images/product-entity-designer.png)

## 项目结构

```text
CodeMaster.WebApi          ASP.NET Core API、动态 API、Controller、中间件
CodeMaster.Application     应用服务、DTO、代码生成、模板和 MCP 相关业务
CodeMaster.Domain          领域实体
CodeMaster.Infrastructure  SqlSugar、仓储、认证授权、SignalR、Quartz、中间件
CodeMaster.Core            基础实体、接口、公共抽象
CodeMaster.Migrator        EF Core 迁移和种子数据
CodeMaster.Vue             Vue 3 管理端和 Tauri 客户端源码
CodeMaster.LocalAgent      桌面客户端使用的本地 sidecar 服务
CodeMaster.McpServer       面向 AI 代码生成流程的 MCP 服务
Templates                  生成项目使用的源码模板包
```

## 技术栈

- .NET SDK 10
- ASP.NET Core Web API
- SqlSugar 运行时 ORM
- EF Core Migrator
- Vue 3 + Element Plus + Vite
- Pinia + vue-i18n
- SignalR
- Quartz
- Tauri + LocalAgent
- SQL Server / MySQL / PostgreSQL / SQLite / Oracle

仓库中的快速开始配置以 MySQL 8 为例，但 MySQL 不是唯一选择。WebApi 运行时通过 `DbProvider` 切换数据库；Migrator 使用对应的编译属性加载 EF Core Provider。EF 迁移历史不提交到仓库，初始化或部署时应针对目标数据库重新生成并验证迁移。

## 快速开始

开始前请安装 .NET SDK 10、Node.js 20+，以及任意一种受支持的数据库。下面以 MySQL 8 为例；仓库只提交不含真实密码的示例配置，首次运行需要复制并填写本地配置。

1. 创建本地配置：

```powershell
Copy-Item CodeMaster.WebApi/appsettings.example.json CodeMaster.WebApi/appsettings.json
Copy-Item CodeMaster.Migrator/appsettings.example.json CodeMaster.Migrator/appsettings.json
```

修改两个 `appsettings.json` 中的 MySQL 密码，并确保它们连接同一个 `CodeMasterDB` 数据库。Migrator 会自动创建数据库、初始化表结构并写入种子数据。

2. 还原并构建：

```bash
dotnet restore CodeMaster.sln
dotnet build CodeMaster.sln
```

3. 使用 MySQL Provider 构建迁移程序并初始化数据库：

```bash
cd CodeMaster.Migrator
dotnet build -p:DbProvider=MySql
dotnet run --no-build
```

4. 启动后端：

```bash
cd CodeMaster.WebApi
dotnet run
```

5. 启动前端：

```bash
cd CodeMaster.Vue
npm install
npm run dev
```

默认开发账号：

```text
admin / admin123
```

## 切换数据库

`CodeMaster.WebApi` 根据配置文件中的 `DbProvider` 在运行时选择 SqlSugar Provider；`CodeMaster.Migrator` 还需要使用相同名称的 MSBuild 属性进行构建。

| 数据库 | `DbProvider` / 构建属性 |
| --- | --- |
| SQL Server | `SqlServer` |
| MySQL | `MySql` |
| PostgreSQL | `PostgreSQL` |
| SQLite | `Sqlite` |
| Oracle | `Oracle` |

例如使用 PostgreSQL：

```bash
cd CodeMaster.Migrator
dotnet build -p:DbProvider=PostgreSQL
dotnet run --no-build
```

迁移程序的构建属性、Migrator 配置和 WebApi 配置必须指向同一种数据库。切换数据库后应重新构建迁移程序，并在目标数据库上验证迁移和种子数据。

## 配置说明

请不要提交真实生产密钥。推荐使用：

- 环境变量
- 本地 user secrets
- 被 `.gitignore` 忽略的 `appsettings.Production.json`
- 服务器密钥管理服务

常用配置键：

```text
ConnectionStrings__DefaultConnection
DbProvider
JwtSettings__SecretKey
Authentication__GitHub__ClientId
Authentication__GitHub__ClientSecret
Authentication__GitHub__CallbackUrl
Email__Smtp__UserName
Email__Smtp__Password
Email__CodeSecret
```

Vue/Tauri 客户端需要自定义服务地址时，可复制 `CodeMaster.Vue/.env.example` 为本地 `.env`。

## 代码生成约定

- 不建议手工修改生成的 `.vue` 和 `.auto.js` 文件，重新生成时可能被覆盖。
- 模板变更应同步维护数据库种子数据；一次性的本地修复或更新脚本不要提交到仓库。
- 实体设计器以 `.tree.json` 作为页面结构来源。
- `.fields.json` 用于保留字段级脚本配置，支持增量生成。

## MCP 场景

CodeMasterAI MCP Server 面向自然语言代码生成工作流，例如：

- 新增或编辑项目
- 新增或编辑模块
- 新增或编辑实体
- 维护实体字段和控件属性
- 初始化生成项目
- 执行全量或增量代码生成
- 启动生成项目的前后端服务
- 查看生成项目的整体实体结构

涉及项目元数据的操作，推荐通过 MCP 工具完成，而不是让智能体直接编辑生成项目的元数据文件。

## 桌面客户端
 :point_right: 特别注意，如果非本地部署，要使用远程服务器，请通过桌面客户端打开远程部署的vue前端地址，客户端有调用本地数据库和调用本地大模型的能力，并且所有的代码生成都是在本地电脑上的
CodeMasterAI 的 Tauri 客户端用于连接服务端前端，同时通过 LocalAgent 调用本地能力。典型场景：

- 在客户端中打开部署在服务器上的 CodeMasterAI 前端
- 调用本地 sidecar 执行项目初始化和代码生成
- 启动本地生成项目的前端和后端
- 下载和管理生成模板

客户端安装包由 GitHub Actions 跨平台构建。推送 `v1.0.1` 形式的 Git Tag 后，工作流会自动生成 Windows x64、macOS Intel、macOS Apple Silicon、Linux x64 和 Linux ARM64 安装包，并上传到对应的 GitHub Release。也可以在仓库的 `Actions` 页面选择 `Release CodeMaster Client` 手动输入版本号运行。

```bash
git tag v1.0.5
git push github v1.0.5
```

客户端不内置项目模板 ZIP。安装后可在项目列表点击“下载模板”，由 LocalAgent 将服务器上的最新模板保存到本地模板目录。

## 安全提醒

公开仓库或生产部署前请务必：

- 轮换所有曾经出现在源码、日志、截图或聊天记录里的密钥
- 不要提交生产 `appsettings`、`.env`、证书和数据库备份
- 生产环境默认关闭或保护 Swagger
- 使用 HTTPS 和足够强的 JWT 密钥
- 对可编辑模板、上传文件和生成代码流程做权限控制

漏洞报告请查看 [SECURITY.md](SECURITY.md)。

## 路线图

- 完善代码生成模板稳定性
- 完善 MCP 工具能力和文档
- 改进 Tauri 客户端安装、升级和模板下载流程
- 完善论坛、账号注册、GitHub 登录和租户初始化体验
- 补充更多测试和示例项目
- 移动端/小程序生成能力，敬请期待后续版本

## 参与贡献

欢迎提交 Issue 和 Pull Request。较大的功能变更建议先开 Issue 讨论设计方向。

贡献前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 开源协议

CodeMasterAI 基于 [Apache License 2.0](LICENSE) 开源。
