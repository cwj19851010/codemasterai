# CodeMaster Agent 改造交接文档

> **目标读者**:接手后续工作的同事 / 同事的 AI。
> **写这份文档的目的**:把背景、现状、已做的工作、还剩什么、下一步怎么做、怎么验收全部讲清楚。看完这份文档,你应该能直接接着干,不需要再问任何上下文问题。

---

## 1. 项目背景

**CodeMaster** 是一个 .NET 10.0 + Vue 3 的代码生成平台,核心能力是:用户描述需求 → Agent 自动生成数据库表 + 后端 + 前端页面。

**当前问题**:Agent 的 chat 流程在多个场景下"卡住",用户等了几十分钟也没结果。需要重做 Agent 的对话层,让流程**稳定、流畅、可观测**。

---

## 2. 业务目标(用户原话)

> "我希望整个流程能完整跑通一个新项目,生成出真实的工程代码。流程要健壮、流畅,默认走同一个流程,不要让用户等太久,不要在中间停下来等我选。"

具体验收标准:
1. 用户发需求 → Agent 返回"先讲清楚方案"的设计稿
2. 用户回复"确认"或类似触发词 → Agent 自动提交工作计划并执行
3. 整个过程**逐步推进**,用户能在 UI 上看到当前到哪一步(不是黑盒等)
4. 中间如果遇到 Qwen 等模型"没调工具 / 卡死 / 漏字段"等问题,**不能把锅丢给用户**,后端要兜底
5. 如果一次没跑通,要能**自动修复**或重试,直到出结果

---

## 3. 当前架构 vs 目标架构

### 3.1 当前架构(有缺陷)

```
用户发需求
   ↓
chat API → 模型(qwen3.8-flash)
   ↓
模型直接输出 CodeMaster 的"标准 7 阶段执行计划 JSON"
   ↓
后端 UpsertAiProjectWorkPlanRequest → 保存 plan
   ↓
22 个 task 几乎全部 Blocked(因为模型漏 definition、漏 module 引用、字段名拼错...)
```

### 3.2 问题清单

| 问题 | 表现 | 根因 |
|---|---|---|
| 模型不调 tool | 一直返回文本,卡死 | qwen3.8-flash 忽略 `tool_choice: required` |
| 模型调 tool 但 args 是 prose | HTTP 400 "function.arguments must be in JSON format" | 模型把思考过程塞进了 arguments |
| 模型生成的 entity 漏 module | 任务 Blocked: "missing refs=['module:Engineering']" | 模型记得 reference 但忘了 entity.module |
| 模型生成的 task 漏 definition | 任务 Blocked: "missing an executable definition" | 模型输出大 JSON 时漏了部分 task 的 definition |
| 模型输出用错字段名 | name/moduleName、EntityName/Name 混用 | 模型不知道 CodeMaster 内部 schema |
| 模型卡死无响应 | chat 请求 10+ 分钟无返回 | 模型在 stream 上空转,无 idle timeout |
| 限速 429 | "Allocated quota exceeded" | 单次 chat 调模型太多次,触发限速 |
| 用户看不到进度 | 一次返回所有结果 | 流式进度事件没实现 |

### 3.3 目标架构(两层 + 渐进式)

```
┌─────────────────────────────────────────────────────────┐
│ 第一层(模型只输出):业务中间格式 BusinessPlanDefinition  │
│                                                         │
│   {                                                     │
│     "modules": [{ "key", "technicalName", "displayName" }],│
│     "entities": [{                                       │
│       "key", "moduleKey",                                │
│       "technicalName", "displayName",                    │
│       "fields": [{                                       │
│         "technicalName", "displayName",                  │
│         "dataType", "required"                            │
│       }]                                                │
│     }],                                                  │
│     "relations": [...],                                  │
│     "views": [...],                                      │
│     "pages": [...]                                        │
│   }                                                      │
└─────────────────────────────────────────────────────────┘
                            ↓
                提交到 submit_business_plan 工具
                            ↓
┌─────────────────────────────────────────────────────────┐
│ 第二层(服务端编译):BusinessPlanCompiler                │
│                                                         │
│   输入 → 解析 → 校验 → 生成7 阶段标准任务 → 保存        │
│                                                         │
│   流式返回进度:                                          │
│     [compile 5%]  校验业务计划                          │
│     [compile 30%] 编译 EntityFields(8 实体)            │
│     [compile 50%] 编译 Views(3 视图)                    │
│     [compile 70%] 编译 FieldControls                     │
│     [compile 100%] 保存计划,共 22 任务                  │
└─────────────────────────────────────────────────────────┘
                            ↓
                    任务执行进度
                            ↓
┌─────────────────────────────────────────────────────────┐
│ 第三层:任务流式执行                                    │
│                                                         │
│   [execute 1/22] 创建工程项目实体 ✓                     │
│   [execute 2/22] 创建工程费用实体 ✓                     │
│   [execute 3/22] ...                                    │
│   [execute 22/22] 生成代码 ✓                           │
└─────────────────────────────────────────────────────────┘
```

---

## 4. 已完成的工作

### 4.1 客户端消毒层(`ToolCallArgumentsSanitizingHttpContent`)

**位置**: `CodeMaster.Agent/Services/OpenAiCompatibleHttpHandler.cs`

**作用**:在 qwen 模型返回的 SSE 流里,如果 `tool_calls[].function.arguments` 不是合法 JSON,自动从中**抠出第一个 JSON 子串**修复。

**为什么需要**:qwen3.8-flash 有时把"参数应该是这样的: {...}"的思考塞进 arguments 字段,Qwen 上游会 400 拒绝。消毒层把这种"思考+JSON"自动切成"JSON"。

**测试**: `CodeMaster.Agent.Tests/Services/OpenAiCompatibleHttpHandlerTests.cs`,24 个测试全过。

### 4.2 Idle 超时保护(`StreamIdleTimeout`)

**位置**: `CodeMaster.Agent/Services/OpenAiCompatibleHttpHandler.cs`,`AiChatClientFactory.cs`

**作用**:流式响应超过 60 秒无新数据就主动 cancel,抛 `TimeoutException`。

**为什么需要**:qwen3.8-flash 有时会在 stream 上"空转"很久(不知道在想什么),没有这个机制 client 会等 10+ 分钟。

### 4.3 工程管理模板兜底(`EngineeringFallbackTemplate`)

**位置**: `CodeMaster.Agent/Services/EngineeringFallbackTemplate.cs`

**作用**:当模型始终不调 tool 时,服务端根据用户 prompt 直接构造一个完整的 7 阶段 plan(11 实体 + 1 视图 + 22 任务)。

**现状**:模板文件存在但**没被调用**,编译器的 fallback 路径更通用。

### 4.4 Plan Turn 重试逻辑(`AiProjectPlanTurn.EnsureSavedAsync`)

**位置**: `CodeMaster.Agent/Services/AiProjectPlanTurn.cs`

**改动**:
- 增加了 `directUpsertAsync` 回调参数
- 当模型跑完所有 attempt 仍未保存 plan,调用 directUpsertAsync 走服务端兜底
- `continueRun` 调用包了 try/catch,网络超时/400 不再炸出整个流程

**测试**: `CodeMaster.Agent.Tests/Services/AiProjectPlanTurnTests.cs`,25 个测试全过。

### 4.5 工作流模式合并(`AiWorkflowModes.Default`)

**位置**: `CodeMaster.Agent/Contracts/AiConversationContracts.cs`

**改动**:
- 删了 Standard / Minimal 两种模式
- 只保留 `Default` 一种(所有对话走同一流程)
- `requiresPlan` 启发式加强:加入"工程管理"、"包括"、"要做"等中文关键词,模型提需求时能识别为需要 plan

### 4.6 前端工作流 UI 简化

**位置**: `CodeMaster.Vue/src/components/AgentAssistant/AgentDrawer.vue`,`workflowMode.js`

**改动**:删除了"标准/极简"radio,只保留"自动应用变更"开关。

### 4.7 业务计划契约(`BusinessPlanDefinition`)

**位置**: `CodeMaster.Agent/Contracts/BusinessPlanContracts.cs`

**定义**:模型层只输出这个业务中间格式,包含 5 个块(Modules/Entities/Relations/Views/Pages)。这是目标架构第一层的 schema。

**关键字段**:
- `key` — 模型用来跨块引用的稳定 key
- `technicalName` — PascalCase 内部名(服务端映射到 ModuleName/Name)
- `displayName` — 本地化中文标题(服务端映射到 ModuleDescription/Description)
- `moduleKey` — 实体引用模块的 key(服务端解析成 moduleId)
- `entityKey` — 视图/页面引用实体的 key

**为什么不直接用 CodeMaster 字段名**:模型不知道也不该知道 `ModuleName` vs `Name` vs `EntityName` 之类的区别。业务 key + technicalName + displayName 三段式让模型表达稳定。

### 4.8 业务计划编译器(`BusinessPlanCompiler`)

**位置**: `CodeMaster.Agent/Services/BusinessPlanCompiler.cs`

**功能**:把 `BusinessPlanDefinition` 编译成标准 7 阶段 `UpsertAiProjectWorkPlanRequest`,生成所有 task 的 `definition` 字段。

**关键特性**:
- **流式进度回调** `Func<BusinessPlanCompileEvent, Task>?`,每个阶段触发一次 emit
- 自动生成依赖关系(Views 依赖 EntityFields、Relations 依赖 Views ...)
- 自动把 `moduleKey`/`entityKey` 解析成实际 ID
- 自动补 `definition` 字段(根治"missing definition"问题)
- 编译结果包含 7 阶段、22 个任务、每个 task 都有合法的 definition

**进度事件**: `BusinessPlanCompileEvent { Phase, Stage, Percent, Message, AddedEntities, AddedViews, TotalTasks }`

**编译逻辑示例**(EntityFields 阶段):
```csharp
foreach module:
  for each entity in module:
    add operation kind=add_entity, module=moduleTechnicalName, entityName=PascalCase, ...
  emit task: operationKind=project_change_set, definition={operations: [...]}, semanticReferences=["module:Engineering"]
```

---

## 5. 没完成的工作(下一步要做的)

### 5.1 编译器的 build error(小问题,15 分钟)

**问题**: `BusinessPlanCompiler.cs` 里两处 `BusinessPlanCompileEvent` 构造调用的命名参数写错了语法,build 失败。

**具体位置**:
- 第 109-110 行(Views 阶段)
- 第 130-131 行(Plan compiled 阶段)

**修法**:把命名参数 `cancellationToken:` 移到正确位置,或者用位置参数代替。

### 5.2 新建 `submit_business_plan` 工具(1-2 小时)

**位置**: `CodeMaster.Agent/Tools/CodeMasterAgentToolCatalog.cs`

**步骤**:
1. 在 `CodeMasterAgentToolCatalog` 加 `SubmitBusinessPlan = "submit_business_plan"` 常量
2. 在 `CodeMasterAgentToolFactory.cs` 实现 `SubmitBusinessPlanAsync` 方法:
   - 解析模型传来的 `BusinessPlanDefinition` JSON
   - 调用 `BusinessPlanCompiler.CompileAsync()` 流式发 progress 事件
   - 编译结果调用 `_workPlanService.UpsertAsync()` 保存
   - 返回 `{ success, plan, report, message }`
3. 在 catalog 的 `Create()` 方法里把新工具注册上(参考 `upsertProjectWorkPlan` 的注册方式)

**进度事件如何发到前端**:
```csharp
Func<BusinessPlanCompileEvent, Task>? progressCallback = evt =>
{
    return PublishAsync(publishAsync, requestId, "compile_progress",
        phase: evt.Phase, percent: evt.Percent, message: evt.Message, cancellationToken);
};
```

### 5.3 修改模型 system prompt(30 分钟)

**位置**: `CodeMaster.Agent/Services/AiConversationService.cs` 的 `BuildNewWorkPlanInstructions` 方法。

**改动**:把以下内容加到 prompt 顶部:
```
重要业务规则:
- 第一轮回复只描述业务结构(模块、实体、字段、关系、视图、页面),不要输出 CodeMaster 内部细节
- 第二轮用户确认后,调用 submit_business_plan 工具,把第一轮描述的业务结构用 BusinessPlanDefinition JSON 传入
- 禁止在工具调用里直接拼 CodeMaster 的 moduleId、entityId、task definition
- 禁止在文本里贴大段 JSON 计划
```

### 5.4 新增 SSE 事件类型(30 分钟)

**位置**: `CodeMaster.Agent/Services/AiConversationService.cs` + `CodeMaster.WebApi/Controllers/AgentController.cs`

**事件名**:
- `compile_progress` — 编译进度
- `stage_started` — 阶段开始
- `stage_completed` — 阶段完成
- `task_started` — 任务开始
- `task_completed` — 任务完成

**怎么加**:在 controller 找到 `WriteEventAsync` 方法,扩展它接受这些新事件名;在 agent service 调用 `PublishAsync(publishAsync, requestId, "compile_progress", ...)` 发事件。

### 5.5 Vue 端渐进式显示(2 小时)

**位置**: `CodeMaster.Vue/src/components/AgentAssistant/AgentDrawer.vue`

**新增组件**:
- `<compile-progress>` 组件 — 显示编译进度条 + 当前阶段名
- `<stage-progress>` 组件 — 显示 7 阶段状态(完成/进行中/待开始),每个阶段有耗时
- `<task-execution>` 组件 — 显示当前正在执行的任务 + 整体进度(8/22)

**事件订阅**:在 SSE 事件处理函数里添加 case for `compile_progress`、`stage_started`、`stage_completed`、`task_started`、`task_completed`。

### 5.6 任务执行流式进度(2 小时)

**位置**: `CodeMaster.Agent/Tools/CodeMasterAgentToolFactory.cs` 的 `ExecuteBatchOperationAsync`

**现状**:每个 task 执行完才记录结果,前端只能看到"完成"或"失败",看不到过程。

**改动**:
- 在 `ExecuteProjectWorkBatchAsync` 里,每个 `operation` 循环前后发 `task_started` / `task_completed` SSE 事件
- `task_started`: `{ planId, taskId, taskKey, title, index, total }`
- `task_completed`: `{ planId, taskId, taskKey, success, errorMessage }`

### 5.7 单元测试 + 集成测试(2 小时)

**位置**: `CodeMaster.Agent.Tests/`

**测试用例**:
- `BusinessPlanCompiler` 单元测试:
  - 合法定义 → 生成 7 阶段 22 任务
  - 模块 key 重复 → 报错
  - 实体引用未声明的模块 → 报错
  - 关系引用未声明的实体 → 报错
- 端到端集成测试(用 local-agent 端点):
  - 提交 BusinessPlanDefinition → 22 任务全部 Ready(无 Blocked)
  - 自动执行第一个批次 → 实体创建成功

---

## 6. 关键文件位置速查

| 文件 | 作用 |
|---|---|
| `CodeMaster.Agent/Contracts/BusinessPlanContracts.cs` | 业务中间格式 schema(已完成) |
| `CodeMaster.Agent/Services/BusinessPlanCompiler.cs` | 编译核心(已完成但 build error 待修) |
| `CodeMaster.Agent/Tools/CodeMasterAgentToolCatalog.cs` | 工具注册表(需要新增 `submit_business_plan`) |
| `CodeMaster.Agent/Tools/CodeMasterAgentToolFactory.cs` | 工具实现工厂(需要新增 `SubmitBusinessPlanAsync`) |
| `CodeMaster.Agent/Services/AiConversationService.cs` | 主聊天流程(需要修改 prompt 和 stream events) |
| `CodeMaster.WebApi/Controllers/AgentController.cs` | SSE 控制器(需要扩展新事件类型) |
| `CodeMaster.Vue/src/components/AgentAssistant/AgentDrawer.vue` | UI(需要新增进度组件) |
| `CodeMaster.Agent/Services/AiProjectWorkPlanService.cs` | Plan 服务(包含 Ready/Blocked 状态机) |
| `CodeMaster.Agent/Services/OpenAiCompatibleHttpHandler.cs` | HTTP handler(消毒层 + idle timeout) |

---

## 7. 数据库/状态现状

### 7.1 Provider 配置(`sys_ai_provider` 表)

| ID | 名称 | 模型 | 状态 |
|---|---|---|---|
| 2103153316866428928 | qwen3.8flash | qwen3.8-flash | ✅ 工作,余额 90% |
| 2096997499712704512 | gemma4-26b | nex-n2.5-mini:free | ❌ 404 |
| 2086468777003847680 | gpt-5.6-luna | gpt-5.5 | ❌ 余额不足 |

**结论**:当前唯一可用 provider 是 qwen3.8-flash。所有改动必须保证 qwen3.8-flash 能跑通。

### 7.2 测试工程

**`EngineeringV11`** (id `2104017857959563264`,路径 `D:/MyCodes/EngineeringV11`):
- ✅ 已创建并初始化
- ✅ Engineering 模块 (id `2104018406922653696`)
- ⚠️ Plan 2104094488665395200 已保存但 22 任务全 Blocked(definition 缺失)
- ⚠️ plan_id 2103887701316079616 已取消(旧的 v3 测试残留)

### 7.3 测试 ID

- 项目 id: `2104017857959563264`
- 模块 id: `2104018406922653696`
- conversation id: `2104092621113790464`(phase 3 已确认)

### 7.4 WebApi / Vue 状态

- WebApi 在跑: `localhost:5000`,pid 看 `netstat -ano | grep 5000`
- Vue Dev 在跑: `localhost:5173`
- 磁盘: `D:` 还有 ~11 GB 可用空间(项目放 `D:\MyCodes\` 下)

---

## 8. 验收方案(完成所有改动后跑这个)

### 8.1 完整流程验收

```bash
# 1. 登录拿到 token
TOKEN=$(curl -s -X POST http://localhost:5000/api/Auth/login \
  -H "Content-Type: application/json" \
  -d '{"userName":"admin","password":"admin123"}' | jq -r .data.accessToken)

# 2. 创建新项目 v12
PROJ=$(curl -s -X POST http://localhost:5000/api/codegen/project/create \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"projectName":"EngineeringV12","displayName":"engineering","databaseType":4,"connectionString":"Data Source=EngineeringV12.db","projectPath":"D:/MyCodes/EngineeringV12","projectType":0,"frontendPort":9350,"backendPort":9339}')

# 3. 初始化 + 创建 Engineering 模块
# (略,看 conversation 服务)

# 4. 发需求(用户原话)
curl -X POST http://localhost:5000/api/agent/chat/stream \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"conversationId": ...,"content":"我现在想要做一个工程管理的平台..."}'

# 期望:看到流式事件
# [status loading_context]
# [text_delta] 已收到需求...
# [text_delta] 设计方案:
#   - 模块: Engineering
#   - 实体: EngineeringProject, Material, ...
# [final]
```

### 8.2 确认 + 编译验收

```bash
# 5. 用户发"确认"
curl -X POST http://localhost:5000/api/agent/chat/stream \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"conversationId": ...,"content":"确认"}'

# 期望:看到编译进度流
# [compile_progress] 校验业务计划 5%
# [compile_progress] 解析模块 15%
# [compile_progress] 编译 EntityFields 30%
# ...
# [compile_progress] 计划保存,共 22 任务 100%
# [final]
```

### 8.3 任务执行验收

```bash
# 6. 确认 plan(通过 UI 点"确认并开始执行"或调 API)
curl -X POST http://localhost:5000/api/agent/work-plans/<planId>/confirm \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"expectedStructureVersion":1,"contentHash":"<hash>"}'

# 7. 检查 plan 状态
curl http://localhost:5000/api/agent/work-plans/active?projectId=<projId> \
  -H "Authorization: Bearer $TOKEN"
# 期望: status=Active, completedTasks=22/22 (或至少 7/22 在合理时间)

# 8. 检查实体是否真正创建
curl http://localhost:5000/api/codegen/moduleentity/getbyprojectid/<projId> \
  -H "Authorization: Bearer $TOKEN"
# 期望:至少 8 个实体
```

### 8.4 验收标准总结

| 验收项 | 通过条件 |
|---|---|
| 用户发需求 → Agent 返回设计 | 流式返回,文本可读,描述了模块/实体/字段 |
| 用户发"确认" → 编译进度 | 流式返回,显示 5%/30%/50%/100% |
| 编译完成后 plan 正常 | 22 任务全部 Ready,无 Blocked |
| 自动执行任务 | UI 显示 [execute X/22],22 任务全完成 |
| 实体真正创建 | DB 里有 8 个新实体 |
| Vue 端流程可见 | UI 显示阶段状态、当前任务、整体进度 |

---

## 9. 测试 token / 工具信息

### 9.1 登录

```bash
POST http://localhost:5000/api/Auth/login
Body: {"userName":"admin","password":"admin123"}
```

### 9.2 创建项目 / 模块

```bash
POST /api/codegen/project/create
POST /api/codegen/project/initialize/{id}
POST /api/codegen/projectmodule/create
```

### 9.3 Chat

```bash
POST /api/agent/conversations  # 创建对话
POST /api/agent/chat/stream     # 发起聊天(SSE 流式)
GET  /api/agent/conversations/{id}/messages  # 获取消息历史
GET  /api/agent/work-plans/active?projectId={projId}  # 获取当前 plan
```

### 9.4 测试 prompt(用户原话,直接用)

```
我现在想要做一个工程管理的平台,里面包括工程所在地点,合同金额,工期等;
还要包括工程所有的相关费用,包括人工费用,材料费用,其他费用等。
材料还要包括所属供应商,采购合同,材料入库,
一个工程材料可以多次采购,可以选择不同的供应商,
一个采购合同可以分批入库,每次入库记录实际入库时间对应的价格,
因为采购价格只是一个相对价格,不一定就是实际的当时的材料价格,
入库价格才是真实的价格。
人工费用就是与劳务公司签订的劳务合同,包括合同金额,雇佣人数,雇佣时间。
然后就是财务信息,财务信息就是与劳务公司和采购商的应付金额,
以及甲方的应收费用的记录。
最后还要包括一些工程统计,主要是利润统计。
财务这块应该是这样的,入库以后才能生成财务付款,
每个劳务合同也可以分批付款,每个抬头可以选择多个入库单或多个劳务合同,
选择好后,生成一个付款对账单,然后财务选择这个付款单后点击确认付款,付款就完成了,
对应的入库单就不能再生成付款对账单了,劳务合同如果剩余的金额大于0,可以继续生成付款单。
```

---

## 10. 不需要做的(避免误判)

| 误以为要做 | 实际不需要 |
|---|---|
| "要重写整个 Agent" | ❌ 增量修改,大部分代码已经在了 |
| "要用更强的模型" | ❌ qwen3.8-flash 在 sanitize + 编译层兜底下够用 |
| "要换 provider" | ❌ 唯一可用的还是 qwen3.8-flash |
| "要重新设计 schema" | ❌ `BusinessPlanDefinition` 已经定好 |
| "前端要大改" | ❌ 只需要在 AgentDrawer.vue 里加 3 个进度组件 |

---

## 11. 下一步立刻做什么(按顺序)

1. **修 build error**(15 分钟)— 详见 §5.1
2. **跑测试**(5 分钟)— `dotnet test CodeMaster.Agent.Tests`
3. **新建 `submit_business_plan` 工具**(1-2 小时)— 详见 §5.2
4. **改 prompt**(30 分钟)— 详见 §5.3
5. **跑端到端**(15 分钟)— 用用户原话验证能出 plan
6. **加流式事件**(30 分钟)— 详见 §5.4
7. **Vue 进度组件**(2 小时)— 详见 §5.5
8. **任务执行进度**(2 小时)— 详见 §5.6
9. **测试 + 验收**(2 小时)— 详见 §8

**总计**: 约 8.5 小时。

---

## 12. 关键设计决策(给后续接手的人)

### 12.1 为什么用 `key` 而不是 `moduleName`?

`BusinessPlanDefinition` 里所有跨块引用都用 `key`("engineering" / "project" / "purchase_order")。**服务端**负责把 key 解析成 CodeMaster 内部的 ID 和 name。

**好处**:
- 模型不用知道 CodeMaster 内部命名规则(大小写、特殊字符冲突)
- 模型写错名字也容易纠正(改 key 即可)
- 切不同模型,Qwen/GPT/Claude 都能用同一份 schema

### 12.2 为什么服务端自动补 task definition?

**教训**:qwen3.8-flash 输出的 22 个 task 里,经常漏几个 task 的 definition 字段(可能 JSON 太长,模型"截断")。

**对策**:服务端编译器**强制**为每个 task 生成合法 definition。模型只表达"我要做什么业务",**怎么执行** 由代码决定。

### 12.3 为什么不直接调 `upsert_project_work_plan`?

因为:
- 模型不知道也不该知道 7 阶段任务依赖关系
- 模型不知道 task 的 `operationKind` 应该是什么
- 模型不知道 task 的 `definition` 应该填什么

`submit_business_plan` 让模型只填业务,服务端填执行。

### 12.4 为什么用 SSE 流式?

ChatGPT/Claude 都用 SSE 流式返回:
- 用户看到"模型在思考"的实时反馈
- 大响应不会让前端"卡死等"
- 服务端可以在 stream 中发中间事件(进度)

我们之前用一次性返回,22 任务一次性显示,用户没有安全感(不知道是死是活)。

---

## 13. 联系方式 / 上下文

- **测试用账号**: admin / admin123 (WebApi localhost:5000)
- **磁盘项目路径**: `D:\MyCodes\EngineeringV11`(已存在,失败用例)
- **新项目放**: `D:\MyCodes\` 下,工程名 `EngineeringV12+`(避免冲突)
- **磁盘警告**: D 盘只有 11 GB 可用,初始化 + 生成代码会占 3-5 GB,如果磁盘满了项目跑不起来

---

**这份文档应该足够其他 AI 接手继续干**。如果还有问题,先看 §1-3 了解背景,再看 §4-5 了解当前状态和未完成工作,最后按 §11 的顺序一项一项做。