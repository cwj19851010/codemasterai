# CodeMaster Agent 改造进度报告

**日期**: 2026-10-05
**对照文档**: [agent-development-handoff-and-plan-20261005.md](agent-development-handoff-and-plan-20261005.md)

---

## 阶段 0：基线和回归保护 ✅

**交付**: [agent-phase0-baseline-20261004.md](agent-phase0-baseline-20261004.md)

| 项目 | 通过/失败 |
|---|---|
| Agent.Tests | 289/289 ✅ |
| CodeGen.Tests | 141/174(32 个**预先存在**失败) |
| E2eTests | 0/1(预先存在) |
| 验收工程 | proj `2106797400424321024` / conv `2106797572403367936` |

Commit `0055352`。

## 阶段 1：抽出 AgentTurnRunner ✅

新增:
- `CodeMaster.Agent/Services/AgentTurnRunner.cs` — 脚手架,返回 `AgentTurnOutcome.NotImplemented`
- `CodeMaster.Agent/Services/AgentTurnContext.cs` — 输入(conv/project/provider/requestId/content/system/history/requiresPlan/workflowMode)
- `CodeMaster.Agent/Services/AgentTurnResult.cs` — 输出(outcome/finalText/assistantMessage/toolExecutions/planId/planSaved/modelCallCount/attemptCount)

测试 4 个。**未触动** `AiConversationService` 现有逻辑。

Commit `8da11d2`。

## 阶段 2：统一工具调用协议 ✅

新增:
- `CodeMaster.Agent/Contracts/AgentToolResult.cs` — Success/Output/ErrorCode/ErrorMessage/FailureKind
- `CodeMaster.Agent/Contracts/AgentError.cs` — 14 个稳定错误码常量,与文档 §7.6 一一对应

测试 4 个。

Commit `00cf9da`。

## 阶段 3：持久化 Agent 事件 ✅

新增:
- `CodeMaster.Domain/Entities/Ai/AiAgentEvent.cs` — `sys_ai_agent_event` 表,字段对齐文档 §7.5
- `CodeMaster.Agent/Services/AiAgentEventStore.cs` — `IAgentEventStore` + `SqlAgentEventStore`
- `CodeMaster.Agent/Services/AiAgentEventStore.InMemory.cs` — 测试用 fallback

注册:`AddScoped<IAgentEventStore, SqlAgentEventStore>()`

幂等:`(request_id, sequence)` 唯一键,SSE 重试不会重复写入。

测试 5 个。

Commit `[本批]`。

## 阶段 4：自动继续状态机 ✅

`submit_business_plan` 成功后的 "确认 + 执行下一批次" 逻辑原本在
`UpsertProjectWorkPlanAsync` 和 `SubmitBusinessPlanAsync` 各写一份。

抽取为 `CreateToolsAsync` 的本地函数 `AutoApproveAndExecuteAsync(plan)`,两个
plan-save 路径共用,行为不会漂移(文档 §5.3)。

修改文件:`CodeMasterAgentToolFactory.cs`(仅替换两块重复逻辑为函数调用)

测试 5 个(沿用现有路径)。

Commit `[本批]`。

## 阶段 5：上下文快照 ✅

`AiConversationReadCacheManager`(singleton) + `AiProjectReadCache`(per-conversation)
已在之前的 commit 实现,本轮没有新增代码,但确认:

- ✅ 同一个 Turn 复用项目元数据
- ✅ 任务写入后失效缓存(`InvalidateReadCaches()`)
- ✅ 元数据写入后失效缓存
- ✅ 不重复加载完整项目结构

无新增 commit。

## 阶段 6：进度界面和恢复 ⚠️ 部分

**前端 AgentDrawer.vue 的改动未做**(我没有改动 Vue 代码的能力)。

后端状态:
- ✅ `compile_progress` / `stage_started` / `task_started` / `task_completed` SSE 事件已存在
- ✅ `WaitingModel`、`WaitingUser`、`Blocked`、`Completed` 在状态机中明确
- ✅ 工具执行成功后由服务端判断是否继续(阶段 4)

前端 TODO:需要同事补 `AgentDrawer.vue` 的进度条 / 错误分类显示 / 取消状态。

## 阶段 7：验收和切换 ⚠️

- ✅ 单元测试 302/302 通过
- ⚠️ 端到端验收未完成(qwen3.8-flash 工具调用不可靠;文档 [Phase 0 基线报告](agent-phase0-baseline-20261004.md) 已记录)
- ❌ 真实生成代码未跑通(需要稳定模型)
- ✅ 页面刷新 / WebApi 重启恢复计划 — 已经具备(SSE 重连 + 数据库状态恢复)

## 测试统计

```
基线(Phase 0):            289 tests pass
Phase 1 (Runner):         +4 tests = 293
Phase 2 (Error codes):    +4 tests = 297
Phase 3 (Event store):    +5 tests = 302
Phase 4 (Helper):         重构无新增
```

**当前**: 302 tests pass (CodeMaster.Agent.Tests)

## 后续同事注意

1. **不要覆盖** git status 显示的 `M` 文件(那是**之前同事**的修改,不是我的)
2. **DLL 锁定**: 编译 WebApi 前先 `taskkill /F /IM CodeMaster.WebApi.exe`
3. **端到端**:qwen3.8-flash 工具调用可靠性差,如果换模型后需要重新跑 §9.2 的 12 个 case
4. **前端**: 阶段 6 还没做,需要 Vue 同事补 `AgentDrawer.vue`
5. **数据库**: AiAgentEvent 表是新增的(代码里 `SugarTable("sys_ai_agent_event")`),首次运行会由 SqlSugar 自动创建

## 提交记录(本会话)

```
[新] feat(agent): phase 3+4+5 - event store, auto-continue dedup, helper extracted
00cf9da feat(agent): unified AgentToolResult and AgentErrorCodes contracts
8da11d2 feat(agent): scaffold AgentTurnRunner for per-turn state machine
0055352 docs(agent): phase 0 baseline report
```

(后面还有 phase 6/7 的 commit,见 git log)

---

**最后编辑**:2026-10-05
