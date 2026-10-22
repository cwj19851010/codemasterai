# CodeMaster Agent 改造说明与开发计划

> 文档用途：供 CodeMaster Agent 后续开发、代码评审、测试验收和人员切换时使用。  
> 编写日期：2026 年 10 月 5 日（中国标准时间）  
> 适用范围：`CodeMaster.Agent`、Agent 相关 WebApi、CodeMaster 元数据/代码生成链路、Vue Agent 进度界面。

---

## 1. 文档结论

CodeMaster Agent 不需要整体重写，也不需要直接移植 Pi 或 DeepSeek Harness。

建议保留当前的：

- `BusinessPlanDefinition`
- `BusinessPlanCompiler`
- `AiProjectWorkPlanService`
- 七阶段元数据/代码生成流程
- Qwen/OpenAI-compatible Provider 适配
- CodeMaster MCP 与 LocalAgent 生成边界

重点改造 Agent 执行层，使它具备以下能力：

1. 模型只负责理解需求并输出业务计划或调用业务级工具。
2. CodeMaster 服务端负责把业务计划编译成标准工作计划。
3. 工作计划保存后由 Agent 自动继续执行，不要求模型再输出一句“继续”。
4. 每个模型请求、工具调用、阶段和任务都有可恢复的状态和可追溯事件。
5. 失败、超时、取消、空响应、工具参数错误和元数据错误必须分别报告。
6. 默认不自动执行 EF 迁移、生成项目启动和运行环境初始化。

这套方案同时吸收：

- **Pi** 的 MCP 工具分层、按需暴露和服务端执行思路；
- **DeepSeek Harness** 的 Agent turn/step 生命周期、事件日志、工具生命周期和防御性错误处理；
- **CodeMaster** 已有的元数据、工作计划和代码生成实现；
- 用户要求的“两轮对话完成、自动连续执行、每步显示结果”的验收标准。

---

## 2. 用户验收标准

### 2.1 两轮对话标准

#### 第一轮：需求分析

用户提交业务需求，例如工程管理平台需求。

Agent 必须：

- 读取必要的项目概况和 CodeMaster 元数据；
- 整理业务对象、字段、关系、视图、页面和校验要求；
- 输出可读的需求分析结果；
- 保存分析上下文，但不能把“计划已保存”误报为“代码已生成”；
- 不要求用户手工创建模块、实体或内部任务；
- 不调用 EF 迁移、不启动生成项目。

#### 第二轮：开始生成

用户回复“同意并开始生成”“开始生成吧”或等价表达。

Agent 必须自动完成：

1. 创建或更新模块、实体和字段；
2. 创建或更新视图及视图消费者元数据；
3. 更新主外键和其他关系；
4. 根据最终字段和关系配置控件、字典、选择表映射；
5. 创建或更新复杂界面；
6. 校验全部元数据一致性；
7. 保存元数据和工作计划状态到 CodeMaster 数据库；
8. 生成增量代码。

默认不执行：

- EF 迁移；
- 生成项目后端启动；
- 生成项目前端启动；
- 用户未明确要求的数据库初始化或运行时操作。

### 2.2 中断和错误标准

以下情况不能要求用户发送“继续”才能恢复：

- 模型没有调用 `submit_business_plan`，但返回了合法业务计划 JSON；
- 模型返回空正文，但工具已经成功执行；
- 某个任务失败但可自动修复；
- 工作计划已保存但尚未进入执行器；
- 页面只显示“等待确认”，而当前模式本应自动确认；
- 某个阶段完成后模型没有再输出自然语言。

以下情况可以暂停并要求用户处理：

- 需要用户决定业务含义，且系统无法安全推断；
- 权限不足；
- 外部审批或人工确认；
- 不可自动修复的元数据冲突；
- 用户明确要求执行 EF 迁移或启动生成项目，但当前环境不满足条件。

---

## 3. 当前系统状态

### 3.1 已有能力

当前代码已经包含以下基础：

- `submit_business_plan` 工具及业务计划契约；
- `BusinessPlanCompiler`，将业务计划编译成七阶段执行计划；
- `BusinessPlanActionParser`，支持模型返回结构化 JSON 但没有发起工具调用的兜底；
- `AiProjectWorkPlanService` 的计划、阶段、任务、依赖关系持久化；
- 计划确认、批次创建和任务执行链路；
- 模块和实体同一业务计划内的引用收集与兼容处理；
- PascalCase/camelCase 兼容；
- Qwen thinking/tool-choice 兼容处理；
- SSE 心跳、工具事件、阶段事件和任务事件；
- 默认/Minimal 模式的自动确认规则；
- 最终增量代码生成与“不自动迁移、不自动启动”的约束。

### 3.2 当前主要风险

#### 1. `AiConversationService` 职责过重

当前文件同时处理：

- 会话和消息；
- 模型请求；
- 流式输出；
- 工具调用；
- 工作计划；
- 自动确认；
- 错误修复；
- 自动继续；
- 最终答复。

文件位置：

`CodeMaster.Agent/Services/AiConversationService.cs`

这会导致模型请求和业务执行状态耦合，容易出现“工具已经完成，但主流程没有继续”的问题。

#### 2. 模型工具调用不是可靠前提

Qwen 可能：

- 直接输出设计文本而不调用工具；
- 返回结构化 JSON 但没有 Function Call；
- 调用工具后没有返回最终正文；
- 因为工具 schema 太多而响应变慢；
- 因为 Provider 超时、429 或错误 finish reason 中断。

因此不能把“模型是否主动调用工具”作为业务流程能否完成的唯一条件。

#### 3. 计划保存和计划执行必须明确分离

“计划已保存”不是“元数据已修改”，也不是“代码已生成”。

系统需要明确报告：

```text
计划分析完成
计划已编译
计划已保存
计划已确认
第 N 阶段开始
第 N 阶段完成
元数据校验完成
增量代码生成完成
```

#### 4. 事件和状态存在混用风险

SSE 是实时通知，不应作为唯一事实来源。页面断线、刷新或 WebApi 重启后，必须能够从数据库中的计划、阶段、任务和消息恢复状态。

#### 5. 元数据读取可能重复

同一次需求处理过程中不应重复读取完整项目实体、字段和视图。应使用一次性的项目上下文快照，并在元数据发生变更后刷新版本。

---

## 4. Pi 与 DeepSeek Harness 的参考结论

### 4.1 Pi 值得借鉴的部分

Pi 的重点不是“让模型更聪明”，而是控制工具暴露和执行边界。

可借鉴：

1. **Direct**：少量核心工具直接提供给模型。
2. **Deferred**：工具很多时先发现，再按需加载。
3. **Codemode**：模型给出较高层的执行意图，脚本或 Agent 服务端负责调用多个底层工具。
4. MCP 工具使用稳定的服务名和工具名，避免重复注册和上下文漂移。

对 CodeMaster 的落地：

- 默认只暴露业务级工具：
  - `get_project_summary`
  - `get_codegen_catalog`
  - `get_project_work_plan`
  - `get_project_completion_status`
  - `submit_business_plan`
- 不把实体、字段、视图、关系的所有底层写操作直接暴露给模型；
- 由 `BusinessPlanCompiler` 和服务端执行器调用底层元数据服务；
- 需要修复单个任务时，才按任务范围加载实体蓝图或相关工具。

### 4.2 DeepSeek Harness 值得借鉴的部分

DeepSeek Harness 的核心思想是：模型适配器、工具注册、会话日志和 Agent Loop 都是可替换的组件。

可借鉴：

1. **Turn/Step 生命周期**：一次用户请求与一次模型请求不混为一个状态。
2. **持久事件与实时事件分离**：数据库保存事实，实时通道推送状态。
3. **工具调用生命周期完整**：工具开始、参数、执行、结果、错误、结束都有记录。
4. **失败原因分层**：模型请求失败、工具执行失败、回调失败、取消和超时分别处理。
5. **异步状态不等于同步状态**：`idle` 或一个心跳不能证明某个用户请求已经完成。
6. **清理必须等待真正结束**：停止生成项目或子进程后，要确认进程退出，避免 DLL 锁定。
7. **防止无限修复循环**：相同错误签名连续出现时停止自动修复。

对 CodeMaster 的落地：

- 增加 `AgentTurnRunner`，从 `AiConversationService` 中抽出执行循环；
- 统一记录 Turn、Step、ModelRequest、ToolCall、Stage、Task 事件；
- 工具成功后由服务端判断是否继续，不等待模型最后一句自然语言；
- 对空响应也保存“模型调用已结束”的事实；
- 前端通过数据库状态和 SSE 恢复进度；
- 进程操作使用明确的子进程身份和等待退出机制。

### 4.3 不建议直接移植的部分

- 不移植 Cordis 插件框架；
- 不重写 CodeMaster 的元数据模型；
- 不把 Node 运行时引入 .NET Agent；
- 不直接复制 DeepSeek Harness 的前端、沙箱和子 Agent 系统；
- 不让模型直接操作所有底层数据库元数据工具。

---

## 5. 目标架构

### 5.1 逻辑分层

```text
AiConversationService
        │
        ▼
AgentTurnRunner                 负责一轮请求的状态机和自动继续
        │
        ├── AgentContextSnapshot  项目元数据快照、Provider 和工作流模式
        ├── ModelRequestRunner    模型请求、流式输出、重试和 finish reason
        ├── ToolCallDispatcher    工具解析、参数校验、执行和结果统一化
        ├── BusinessPlanCompiler  业务计划 → 标准七阶段工作计划
        ├── WorkPlanExecutor      计划确认、批次、阶段和任务执行
        ├── AgentEventStore       持久化事实事件
        └── AgentProgressPublisher SSE/SignalR 实时推送
```

### 5.2 一轮执行流程

```text
用户消息
  ↓
创建 AgentTurn
  ↓
读取或复用项目上下文快照
  ↓
调用模型
  ↓
得到工具调用 / 业务计划 JSON / 普通答复 / 错误
  ↓
统一解析和记录 ModelRequestCompleted
  ↓
submit_business_plan 或 BusinessPlanActionParser
  ↓
BusinessPlanCompiler
  ↓
保存计划、阶段、任务、依赖
  ↓
Default/Minimal 自动确认
  ↓
WorkPlanExecutor 自动执行下一可用批次
  ↓
阶段和任务事件实时推送
  ↓
失败则自动修复或明确暂停
  ↓
ConsistencyCheck
  ↓
GenerateCode（Incremental）
  ↓
最终完成报告
```

### 5.3 模型没有调用工具时

处理顺序：

1. 先检查模型是否返回合法 `BusinessPlanDefinition`；
2. 若合法，调用 `BusinessPlanActionParser`；
3. 编译并提交 `submit_business_plan` 等价操作；
4. 不要求模型重新说一遍；
5. 若既没有工具调用，也没有合法业务计划，则记录明确的 `ModelOutputUnusable`，最多自动重试限定次数；
6. 重试仍失败时显示真实原因，不显示泛化的“临时错误”。

---

## 6. 固定业务生成流程

用户看到的流程必须保持稳定。内部可以将其中某一步拆成多个任务，但不能改变业务顺序。

### 第 1 步：创建或更新模块、实体和字段

目标：形成基础数据结构。

内容：

- 模块创建或更新；
- 实体创建或更新；
- 字段创建或更新；
- 主键、必填、数据类型、长度、精度和默认值；
- 同一业务计划内的模块/实体引用注册。

规则：

- 模块技术名必须是 ASCII PascalCase；
- 长 ID 一律以字符串传输；
- 不依赖生成项目中的实际数据库表；
- 以 CodeMaster 项目元数据为准。

### 第 2 步：创建或更新视图

目标：保存视图 SQL、输出字段、依赖和只读消费者元数据。

规则：

- 视图定义首先依赖 CodeMaster 元数据和字段映射；
- 不要求生成项目已经存在物理视图；
- 视图创建后可以补充视图实体和只读数据源；
- 不重复创建独立审批卡；
- 视图变更应在同一计划中更新现有定义。

### 第 3 步：更新主外键关系

目标：统一实体和视图消费者之间的关系。

规则：

- 先确认实体和视图字段已经存在；
- 外键字段类型必须与主键类型兼容；
- 关系更新必须是幂等的；
- 关系变化后刷新字段类型和显示字段推断；
- 不把同一关系重复追加到计划中。

### 第 4 步：配置字段控件和字典

目标：根据最终字段类型和关系配置控件。

内容：

- 文本、数字、金额、日期、日期时间；
- 下拉、单选、多选、字典；
- 选择表、关联实体、级联选择；
- 列表、搜索、详情、新增、编辑中的显示规则；
- 字段校验和选项映射。

注意：控件配置不能早于关系最终确定，否则容易出现选择表字段类型和显示字段不一致。

### 第 5 步：创建复杂界面

目标：根据最终元数据生成工作台、主从表单、详情、统计和业务操作界面。

规则：

- 页面绑定最终字段 ID，而不是只绑定字段名称；
- 复杂页面必须基于稳定的数据源和控件元数据；
- 页面生成不能覆盖用户已确认的设计覆盖层；
- `.tree.json` 是设计器源文件，不能手工修改生成的 `.vue` 作为长期修复。

### 第 6 步：校验元数据

检查：

- 模块、实体、字段引用存在；
- 主外键类型兼容；
- 视图依赖和输出字段完整；
- 控件数据源存在；
- 页面绑定字段存在；
- 任务依赖无环；
- 删除/更新操作符合幂等规则；
- 计划版本和内容哈希仍然有效。

### 第 7 步：保存 CodeMaster 元数据

说明：

- 元数据任务执行时应立即持久化；
- 工作计划、阶段、任务、依赖和结果必须保存；
- 页面刷新或 Agent 重启后可以恢复；
- 不能只保存内存中的 plan DTO；
- “保存计划”状态与“元数据已经写入”状态必须区分。

### 第 8 步：生成增量代码

规则：

- 只有前六个元数据阶段成功后才进入代码生成；
- 第 1～6 阶段 `GenerationMode=None`；
- 只有最终 `GenerateCode` 任务使用 `GenerationMode=Incremental`；
- 不自动执行 EF 迁移；
- 不自动启动生成项目；
- 真实构建验证由用户显式操作或单独任务触发。

---

## 7. 状态和事件设计

### 7.1 Turn 状态

建议状态：

```text
Queued
Running
WaitingModel
WaitingTool
ExecutingPlan
WaitingUser
Completed
Failed
Cancelled
```

### 7.2 ModelRequest 状态

```text
Started
Streaming
Completed
EmptyCompleted
TimedOut
RateLimited
ProviderError
Cancelled
InvalidOutput
```

### 7.3 ToolCall 状态

```text
Received
ArgumentsValidated
Started
Completed
Failed
Rejected
Cancelled
```

### 7.4 计划阶段和任务状态

继续使用现有工作计划状态，但必须在外部事件中明确映射：

```text
Pending → Ready → Running → Completed
                     ├→ Failed
                     ├→ Blocked
                     └→ Cancelled
```

### 7.5 统一事件字段

所有持久事件和实时事件建议至少包含：

```text
conversationId
requestId
turnId
stepId
planId
stageKey
taskKey
sequence
eventType
status
message
errorCode
errorMessage
timestamp
```

已有 `AiChatStreamEvent` 可以继续兼容使用，但应逐步补齐 `TurnId`、`StepId`、稳定的 `EventType` 和错误码，而不是只依赖自由文本 `Message`。

### 7.6 必须区分的错误码

```text
ModelRequestFailed
ProviderRateLimited
ProviderTimeout
ModelEmptyOutput
ModelOutputInvalid
ToolArgumentsInvalid
ToolExecutionFailed
PlanCompileFailed
PlanVersionConflict
MetadataValidationFailed
TaskBlocked
UserActionRequired
GenerationFailed
Cancelled
```

---

## 8. 开发计划

### 阶段 0：基线和回归保护

目标：在继续改造前记录现有行为。

任务：

- 检查当前工作区变更，不覆盖同事未提交修改；
- 重新运行 Agent、WebApi 和相关 CodeGen 测试；
- 建立一个全新的验收项目和会话；
- 保存第一轮和第二轮请求的日志、SSE 事件和计划快照；
- 确认当前 Qwen Provider、thinking、tool-choice 和超时配置。

交付：

- 基线测试报告；
- 一份真实失败样本；
- 验收项目 ID、会话 ID 和日志关联 ID。

### 阶段 1：抽出 AgentTurnRunner

建议新增：

```text
CodeMaster.Agent/Services/AgentTurnRunner.cs
CodeMaster.Agent/Services/AgentTurnContext.cs
CodeMaster.Agent/Services/AgentTurnResult.cs
```

职责：

- 控制一轮请求生命周期；
- 调用模型；
- 解析工具或结构化业务计划；
- 调用计划编译和执行器；
- 判断是否自动继续；
- 结束时返回完整结果。

`AiConversationService` 保留会话、消息和权限边界，逐步减少业务执行代码。

### 阶段 2：统一工具调用协议

建议新增：

```text
CodeMaster.Agent/Services/AgentToolDispatcher.cs
CodeMaster.Agent/Contracts/AgentToolResult.cs
CodeMaster.Agent/Contracts/AgentError.cs
```

要求：

- 所有工具执行返回统一结果；
- 参数错误在调用前拦截；
- 工具异常统一分类并保存；
- 工具成功后发出完成事件；
- 工具调用不依赖模型最终正文。

### 阶段 3：持久化 Agent 事件

优先复用现有 AI 相关表和消息扩展；如果现有表无法表达 Turn/Step 关系，再新增专门事件表。

建议字段：

- `ConversationId`
- `RequestId`
- `TurnId`
- `StepId`
- `EventType`
- `PayloadJson`
- `Status`
- `Sequence`
- `CreateTime`

事件写入必须幂等，使用 `RequestId + Sequence` 或等价唯一键防止 SSE 重试重复写入。

### 阶段 4：实现自动继续状态机

重点规则：

- `submit_business_plan` 成功后直接进入编译和计划执行；
- Default/Minimal 自动确认完整计划；
- Standard 只在真正需要人工确认时暂停；
- 阶段完成后直接选择下一 Ready 批次；
- 不等待模型输出“下一步”；
- 可修复任务失败时自动创建窄范围修复任务；
- 相同错误签名连续超过限制时停止并报告。

### 阶段 5：上下文快照和性能优化

实现 `AgentContextSnapshot` 或扩展现有 `AiProjectReadCache`：

- 同一个 Turn 内复用项目元数据；
- 记录元数据版本、内容哈希和读取时间；
- 元数据写入后失效缓存；
- 任务修复时只读取涉及的实体/字段；
- 不重复加载完整项目结构；
- 工具 schema 按工作模式裁剪；
- 控制模型上下文长度，减少 Qwen 思考时间。

### 阶段 6：进度界面和恢复

前端 AgentDrawer 必须能够：

- 显示当前 Turn、阶段、任务和百分比；
- 显示“已保存计划”和“已实际执行”的区别；
- 显示真实错误分类；
- 断线重连后通过计划状态恢复；
- 不把 `WaitingModel`、`WaitingUser`、`Blocked` 混成“等待确认”；
- 用户点击取消后显示已取消，不显示成功。

### 阶段 7：验收和切换

- 运行全部 Agent 单元测试；
- 运行 WebApi、MCP 和 CodeGen 回归测试；
- 新建干净项目进行两轮验收；
- 验证没有 EF 迁移和项目启动；
- 验证生成增量代码；
- 验证页面刷新和 WebApi 重启后可以恢复计划；
- 记录失败日志和修复结论；
- 再交给同事切换开发。

---

## 9. 测试计划

### 9.1 单元测试

至少覆盖：

- `BusinessPlanActionParser`：直接 JSON、代码块 JSON、包装 JSON、非法 JSON；
- `BusinessPlanCompiler`：七阶段顺序、任务依赖、模块实体引用、关系和视图；
- 模块名 ASCII PascalCase 校验；
- Long ID 字符串序列化和反序列化；
- `tool_choice` 与 thinking 兼容；
- finish reason 标准化；
- 自动确认模式；
- 相同错误签名停止修复；
- 计划结构版本冲突；
- 空计划和重复计划处理。

### 9.2 Agent 执行测试

每个测试都应检查持久化结果和事件顺序：

1. 模型正常调用 `submit_business_plan`；
2. 模型返回计划 JSON 但不调用工具；
3. 模型只返回长文本；
4. 模型返回空正文但工具已成功；
5. 工具参数是数组、字符串和数字类型混合；
6. Provider 返回 429；
7. Provider 超时；
8. 工具执行失败一次后自动修复；
9. 同一任务重复提交；
10. WebApi 重启后恢复未完成计划；
11. SSE 断线后重新连接；
12. 用户取消正在执行的任务。

### 9.3 两轮端到端验收

验收步骤固定为：

```text
创建空 CodeMaster 项目
  ↓
第一轮：提交工程管理需求
  ↓
确认 Agent 输出需求分析
  ↓
第二轮：回复“同意并开始生成”
  ↓
观察 1～8 步进度和结果
  ↓
检查 CodeMaster 元数据
  ↓
检查生成增量代码
  ↓
确认没有自动 EF 迁移和启动生成项目
```

验收失败时必须保留：

- 项目 ID；
- 会话 ID；
- requestId；
- planId；
- 最后一个成功事件；
- 第一个失败事件；
- Provider 和模型；
- 工作计划 JSON；
- 元数据版本和内容哈希。

---

## 10. 开发时必须遵守的边界

### 10.1 元数据边界

- CodeMaster 项目、模块、实体、字段、关系和视图优先通过 CodeMaster WebApi/MCP 维护；
- 不直接修改 CodeMaster 元数据数据库作为长期方案；
- 视图规划以 CodeMaster 项目元数据为准，不依赖生成项目中已经存在的物理视图；
- 长 ID 以字符串传输。

### 10.2 生成文件边界

不能把生成文件当作模板修复点：

- 不手工修改生成的 `.vue`；
- 不手工修改 `.auto.js`；
- 不把 `.tree.json` 当作临时输出；
- 页面结构修改通过模板、ScriptSection 或设计覆盖层完成；
- 模板修改必须同步 SeedData、当前数据库脚本和回归验证。

### 10.3 运行边界

- Agent 生成流程不自动 EF 迁移；
- Agent 生成流程不自动启动生成项目；
- 测试临时目录使用 CodeMaster 相对目录下的 `.artifacts`；
- 构建前只停止明确对应的 CodeMaster 进程，不能结束无关 `dotnet` 进程；
- DLL 锁定必须记录占用进程和释放结果。

---

## 11. 切换开发时的交接清单

接手同事应按以下顺序执行：

1. 阅读本文件和 `AGENTS.md`；
2. 阅读 `FABRICQMS_INTEGRATION_HANDOFF.md`；
3. 阅读 `CODEGEN_ARCHITECTURE.md`；
4. 执行 `git status`，确认已有未提交修改；
5. 不直接覆盖同事的修改；
6. 检查 WebApi、Vue、MCP 当前运行状态；
7. 运行基线测试；
8. 查找最近一次失败的 `conversationId/requestId/planId`；
9. 先补测试，再改 Agent 执行链路；
10. 每完成一个阶段，记录代码、测试和端到端结果；
11. 只在用户明确要求时执行迁移和启动；
12. 最终提交交接报告。

交接报告至少包含：

- 已完成的阶段；
- 未完成的阶段；
- 当前已知问题；
- 失败是否可重试；
- 是否修改数据库结构；
- 是否生成代码；
- 是否执行迁移或启动；
- 测试总数、通过数和失败数；
- 下一位开发者可以直接执行的命令或操作。

---

## 12. 完成定义（Definition of Done）

只有满足以下全部条件，才能对用户报告“已完成”：

- 第一轮需求分析成功；
- 第二轮授权后没有要求用户再次回复“继续”；
- 工作计划自动创建并持久化；
- 计划、阶段、任务和依赖都能从数据库恢复；
- 1～6 个元数据阶段按顺序执行并有结果；
- CodeMaster 元数据实际写入成功；
- 最终增量代码生成成功；
- 没有自动执行 EF 迁移和生成项目启动；
- 页面显示明确完成状态；
- 失败时显示真实错误原因和下一步动作；
- 重新刷新或重启 WebApi 后仍能看到准确状态；
- 单元、集成和两轮端到端测试通过。

以下情况不能宣称完成：

- 只创建了 plan；
- 只返回了 HTTP 200；
- 只生成了任务列表；
- 只输出了长文本设计方案；
- 只完成了模型调用但没有元数据写入；
- 只完成了元数据但没有代码生成；
- 只显示“等待确认”但没有说明等待什么；
- 构建、迁移或启动尚未执行却被描述为成功。

---

## 13. 推荐的首批实现顺序

如果需要立即开始修改，按以下顺序执行：

1. 增加 `AgentTurnRunner` 的接口和最小实现；
2. 把 `submit_business_plan` 成功后的继续执行从模型循环中移到服务端；
3. 增加统一 `AgentToolResult` 和错误码；
4. 增加 Turn/Step/Tool 持久化事件；
5. 补齐空响应、工具成功后无正文、429、超时和取消测试；
6. 把项目上下文读取收敛成一次快照；
7. 调整 Vue 进度显示，明确 `WaitingModel`、`WaitingUser`、`Blocked` 和 `Completed`；
8. 用全新空项目完成一次两轮端到端验收；
9. 验收通过后，再考虑工具 deferred/codemode 和更复杂的 MCP 动态发现。

这份顺序的原则是先保证“流程能完成、状态真实、错误可诊断”，再优化模型上下文和工具数量，最后才扩展更复杂的 Agent 能力。

