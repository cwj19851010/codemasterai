# CodeMaster 问题记录（2026-08-04，货代项目 HDTCargoERP 联调）

> 本文件用于给 CodeMaster 源码修复提供上下文。现象→根因→建议修复，均带源码定位。
> 相关会话事实：货代项目 21 实体/6 模块元数据已通过 MCP 创建成功；代码生成与字典操作遇到下述问题。

---

## 一、MCP 参数绑定：ID 必须传字符串，传数字报"An error occurred invoking"（无明细）

- **现象**：`generate_code` 传 `{"projectId": 2082292671791108096}`（JSON 数字）时，MCP 返回通用错误 `An error occurred invoking 'generate_code'.`，无异常信息、无日志；改传 `"2082292671791108096"`（字符串）即正常进入执行流程。
- **根因**：`CodeMaster.McpServer\Tools\CodeMasterMcpTools.cs` 中所有 `[McpServerTool]` 方法签名把 ID 声明为 `string?`（如 `GenerateCodeAsync(string? entityId, ...)`），内部靠 `ParseId` 转 long。而 MCP 框架（ModelContextProtocol.Server）对数字 JSON 绑定到 string 参数直接抛异常，异常被框架吞掉。
- **注意矛盾点**：`CodeMaster.McpServer\Tools\CodeGenTool.cs`（Definition.InputSchema）与 `DictionaryTool.cs` 的 schema 明确写 `oneOf: [integer, string]`——schema 声明与真实绑定不一致。
- **建议修复（MCP server 侧，可选）**：
  1. 把工具方法 ID 参数类型统一为 `long?`（框架绑数字没问题），或
  2. 参数改为 `JsonElement`/`object` 自行 `TryParse`，或
  3. 至少保证框架异常带明细（自定义 exception handler），否则客户端无法排查。
- **影响**：客户端必须遵守"ID 一律字符串"约定；已通过 `mcp-call.ps1` 封装规避，无需再修，但建议修复以降低误用成本。

## 二、generate_code 卡死/502：LocalAgent 拉取 generation-bundle 失败，且失败无超时无日志

- **现象**：`generate_code`（mode=full 24 实体；单实体 Customer 亦然）行为不稳定：
  - 第一次：5 秒内返回 `Server request failed: 502 Bad Gateway`（24 实体全部 generated=false）。
  - 之后多次：启动后**无任何返回**，挂起直至客户端超时（300s/600s），期间进程仍在（LocalAgent 日志只有 execution.started，无 failed/completed）。
  - 同一时段 `get_project_structure`（也调 generation-bundle，见 ProjectStructureTool.cs:47）**正常返回**——说明服务端 bundle 接口本身可达。
- **根因链**（`CodeMaster.LocalAgent\Services\LocalCodegenExecutionService.cs:299-323` GenerateProjectCodeAsync）：
  1. 先从服务端 `GET /api/codegen/project/{id}/generation-bundle`（`CodeMasterServerClient.cs:25-31`）取元数据；
  2. 然后 `_metadataStore.CreateAsync(bundle, ...)` 写本地 sqlite 元数据缓存（`%LOCALAPPDATA%\CodeMaster\LocalAgent\*.metadata.db`）；
  3. 再执行代码生成。
  - 502 出现在步骤 1（栈：`GetApiDataAsync → line 171`）。
  - 挂起无返回：请求已发出但服务端 5-10 分钟内无响应；`CodeMasterServerClient.GetApiDataAsync`（HttpClient 默认超时 100s，见 CodeMasterServerClient.cs）无明确超时/重试/进度上报，客户端只能干等。
- **日志证据**：`%LOCALAPPDATA%\CodeMaster\LocalAgent\logs\local-agent-20260804.jsonl`：
  - `02:03:03 execution.failed ... 502 Bad Gateway`（processId 37780，durationMs 5078）
  - `01:57:41 / 02:02:58 / 02:03:21 / 02:06:45 / 02:19:10` 多次 `execution.started`（generateProjectCode）均无 completed/failed 记录。
- **建议修复（LocalAgent + 服务端）**：
  1. LocalAgent：HttpClient 增加合理超时（如 180s）+ 请求阶段上报（started→bundle→generated→build），失败时把 HTTP 状态码/响应体/耗时写入日志；
  2. 服务端：排查 502 期间 WebApi 是否崩溃/死锁（生成 bundle 需序列化全部实体+字段+关系，本项目约 24 实体规模不大，怀疑与前端端口 9001 或后端 9000 端口占用/网关有关）；
  3. 建议 `GenerateProjectCodeAsync` 失败后不要只记 `execution.failed`，要带 exception 序列化全文（现有日志 exception 已带，但 502 场景没有）。

## 三、字典接口 404：/api/codegen/project/{id}/dictionaries 不存在于远程 9000

- **现象**：`query_dictionaries` / `save_dictionary` 调 `GET|POST /api/codegen/project/{id}/dictionaries` 返回 404 Not Found。
- **确认**：`CodeMaster.WebApi\Controllers\CodeGen\ProjectController.cs:233-262` 路由**已存在**（GetDictionaries/SaveDictionary），且 `ProjectService.GetProjectDictionariesAsync / SaveProjectDictionaryAsync`（CodeMaster.Application\Services\CodeGen\Project\ProjectService.cs:1406 附近）存在。
- **判断**：本地源码路由存在但远程 `http://101.37.24.154:9000` 部署版本无此接口（版本落后）。→ 属**部署版本问题**，非代码问题；远程 9000 需重新发布，或 MCP 改连本地 WebApi（localhost:5000）。
- **建议**：远程服务重新部署后再试；字典数据写入依赖生成项目数据库（SysDictType/SysDictData），接口本身只做中转。

## 四、PS5.1 客户端编码问题：中文经 mcp-call.ps1 全部变乱码（U+FFFD）存入数据库

- **现象**：所有通过 MCP 创建的实体/字段中文 description 在服务端为 `\uFFFD` 乱码（`query_project`/`get_project_structure` 可见），如 Customer 创建时传"客户"存成 `\uFFFD\uFFFD...`；而平台 UI 创建的老模块描述正常。
- **根因**：客户端 `mcp-call.ps1`（C:\Users\caiwenjie\AppData\Local\Temp\opencode\mcp-call.ps1）用 `$p.StandardInput.WriteLine()` 发送 JSON，PowerShell 5.1 控制台编码为 GB2312（`[Console]::OutputEncoding` 实测 GB2312），写出的 JSON 字节按 GB2312，而 MCP server/WebApi 按 UTF-8 解析 → 乱码。
- **已修复**：mcp-call.ps1 开头设置 `[Console]::OutputEncoding/[InputEncoding] = UTF8`（并 try/finally 恢复），修复后 `create_or_update_entity` 传"客户"验证服务端已存为 `\u5BA2\u6237`（正确）。
- **遗留**：**已入库的 21 实体中文 description 已损坏，需重发**。修复方式：用修复后的 mcp-call.ps1 逐实体 `create_or_update_entity`（fields 传当前完整字段，增量合并，fieldsUpdated 会更新 description；本次验证 Customer 已重发成功）。
- **建议（MCP server 侧可选）**：无代码改动必要；属客户端编码规范。可在 MCP 文档注明 Windows 下需 UTF-8 环境。

## 五、MCP 登录与多服务切换：token 按 serverBaseUrl 存储

- **现象**：`codemaster_whoami` 指定 `serverBaseUrl=http://localhost:5000` 返回 `No CodeMaster MCP token saved`——即使 `mcp-auth.json` 中已有 101.37.24.154:9000 的 token。
- **根因**：`AuthTool.cs:115-121`（WhoAmI）按 `McpAuthStore` 中保存的 entry 的 ServerBaseUrl 精确匹配；`McpAuthStore.NormalizeBaseUrl` 归一化后按 host 区分。
- **环境事实**：本地 `CodeMaster.WebApi.exe`（PID 4984，`D:\MyHomeWorks\CodeMaster\CodeMaster.WebApi\bin\Debug\net10.0\CodeMaster.WebApi.exe`）已在 **localhost:5000** 运行，appsettings.json 连接串指向 **101.37.24.154:5432/CodeMasterDB（PostgreSQL）**——与远程 9000 **共用同一数据库**。
- **建议**：在本地 5000 上 `codemaster_login`（同一 token 理论上可用，因数据库相同）；并将 `D:\MyWorks\HDTCargoERP\.codemaster\project-context.json` 的 serverBaseUrl 切到 localhost:5000（或 MCP 调用统一带 serverBaseUrl 参数）。代码生成/字典等是否切本地，取决于远程 9000 是否重新部署（见二、三）。

## 六、其他观察（非阻塞）

- 实体创建接口成功时 message 偶含无意义异常文案（如 `System.Exception: 未将对象引用设置到对象的实例`），实际以 `fieldsCreated` 为准——建议服务端成功路径不要拼异常文本。
- `get_project_structure`（includeGeneratedFiles=false）响应极大（24 实体×字段全量序列化），MCP stdout 单条 JSON 超过工具输出截断阈值，客户端排查不便；可考虑分页/摘要选项。
- 本地 `%LOCALAPPDATA%\CodeMaster\LocalAgent\templates\` 已有模板 zip（2070875663014957056 等），LocalAgent 首次执行会下载模板，若网络受限也会表现为挂起（本次 502 与之无关，但值得日志区分）。

---

## 给开发者的最小修复清单（按优先级）

| # | 位置 | 修复 | 阻塞性 |
|---|------|------|--------|
| 1 | 远程 9000 部署 | 重新发布含 dictionaries 路由与 generation-bundle 稳定版本的 WebApi | 高（字典 404、生成 502/挂起） |
| 2 | CodeMasterServerClient.cs:171 附近 | HttpClient 超时（180s）+ 阶段进度 + 失败写完整响应体 | 中（生成挂起无反馈） |
| 3 | CodeMasterMcpTools.cs 签名 | ID 参数改 long? 或统一 schema | 低（已客户端规避） |
| 4 | mcp-call.ps1（客户端） | 已修复 UTF-8；重发 21 实体中文 description | 数据修复动作（进行中） |
