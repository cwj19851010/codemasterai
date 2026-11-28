# 更新日志

## 2026.11

这一版主要在收编译和初始化的问题——社区里反馈最多的「生成出来编译不过、初始化跑不完」。同时把门户的注册登录和联系方式补齐了。

### 生成项目编译

- 开源版模板不再引用私有打印模块，修复 `CS0234: 命名空间 SeedData.CodeGen 中不存在类型`
- 开源版模板补齐 `CodeMaster.Vue/src/utils/printReport.js` 占位实现，修复 `vite build` 报 `Could not load .../printReport`
- 初始化时自动清理历史模板包里的孤立种子引用，旧的模板 zip 也能构建通过
- 导出模板时同步清理 `Migrator/Program.cs` 的 `SeedData.CodeGen`、`PrintTemplateModule` 引用

### 初始化与数据库

- 合并为单一 `DbContext` 并改用 EF 模块扫描，`Add-Migration` 不再报 `More than one DbContext was found`
- 修正 SQLite 相对路径的工作目录，Migrator 与 WebApi 指向同一个数据库文件，修复「初始化完没有种子数据、登录 400」
- 修复 Agent 批次适配器重复注册导致的 `An item with the same key has already been added: project_change_set`

### 建模与生成

- 编译器在实体阶段提前补齐关系外键字段，修复 `references missing target field` / `uses removed or renamed target field`
- 关系外键字段名统一规整为 PascalCase，避免 `project_id` 与 `ProjectId` 对不上
- 补齐 `system` / `monitor` 两个顶级目录的多语言翻译，菜单不再回退显示英文 key
- 模板 include 清单修正（`appsettings.Development.json` → `appsettings.example.json`），导出不再中途失败

### 界面与门户

- 移除「数据源管理」，实体 / 视图 / API 收归「代码管理」
- 菜单顺序调整为：项目、模块、实体、视图、API、复杂页面、模板配置、组件管理
- 注册与 GitHub 登录同时建立门户会话，社区页不再显示「未登录」
- 首页导航的工作流 / Agent / 能力 / 架构锚点补全，并新增技术架构段落
- 门户补齐登录入口（此前只有注册），新增「联系我们」页，支持 QQ 群二维码
- 修正首页硬编码的仓库地址（`code-master` → `codemasterai`）

### 升级方式

1. 拉取最新代码（Gitee 与 GitHub 已同步）
2. 重新导出模板；或删除 `Templates/CodeMaster_Template_*.zip` 后重新生成
3. 已生成过的项目建议重新初始化，或对照上面的条目手动处理

### 已知问题

- 多租户隔离尚未生效：项目创建时不写 `tenant_id`，且全局租户过滤器在请求早期被固化为宿主（0），当前所有登录用户都能看到全部项目。修复会改变现有数据可见性，计划在下一版处理。
