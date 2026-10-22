# Agent 只做元数据：与源代码彻底解耦计划

制定日期：2026-10-20
状态：**P0 / P1 / P2 已执行完成**（2026-10-20），P3 兼容层按计划保留
前置决策（用户 2026-10-20 明确）：**Agent 与实际源代码没有关系。**
生成代码是用户在项目页面的手动动作（全量生成 / 增量生成），Agent 既不写源码，
也不编排生成、构建、测试；需要核对现状时只读元数据（API / MCP），只改元数据。

---

## 1. 目标形态

```
用户 ──对话──> Agent ──写元数据──> 平台库（sys_module_entity / sys_entity_field / sys_project_page …）
                                                        │
                                          用户点「生成代码」按钮（项目页面）
                                                        │
                                      生成 .cs / .vue → 复杂页面 → 编译 → 测试
```

- Agent 的终点是**元数据落库**，工作计划止于 `ConsistencyCheck`。
- Agent 不得发出任何 `generateProjectCode` / `generateProjectIncrementalCode` / `generateProjectSmartCode` /
  `generateComplexPage` / `buildProject` / `testProject` / `migrateDatabase` 客户端动作。
- 生成流程的可靠性（断线续跑）归**项目页面那条链路**管，与 Agent 无关。

## 2. 现状：2026-10-17 已做一半

10-17 的「架构纠正」已完成编译器与约定层，今天四轮 E2E 也验证了
计划实际只有 6 个阶段（EntityFields / Views / Relations / FieldControls / ComplexPages / ConsistencyCheck）：

| 已改 | 文件 |
|---|---|
| 删除 Phase 9 + GenerateCode stage + `CompileGenerateCodeTask` | `CodeMaster.Agent/Services/BusinessPlanCompiler.cs` |
| 删除 `generateStage`，空计划占位改 ConsistencyCheck | `CodeMaster.Agent/Services/EngineeringFallbackTemplate.cs` |
| `StageOrder` 7→6；硬规则「agent 从不生成代码」 | `CodeMaster.Agent/PlatformConstraints/WorkflowConvention.cs` |
| 生成/构建管线标注 `NOT performed by the agent` | `CodeMaster.Agent/PlatformConstraints/GenerationConvention.cs` |
| 单测同步（7→6，`DoesNotContain` GenerateCode） | `BusinessPlanCompilerTests` / `PlatformConstraintsTests` |

**但只改了这一层。** 提示词、工具目录、DTO 契约、前端抽屉、四个客户端动作发射点
仍在告诉模型「建七阶段计划、最后一阶段生成代码、元数据完成后自动跑生成/构建/测试」。
这些是自相矛盾的指令，模型会照旧走生成路径。

## 3. 剩余耦合点清单

### P0 提示词与工具描述仍在命令模型生成代码（矛盾源头，最高优先）

| # | 位置 | 现状 | 改为 |
|---|---|---|---|
| 1 | `AiConversationService.cs:2120` | 「create one durable **seven-stage** plan … ConsistencyCheck, **and GenerateCode**」 | 六阶段，删除 GenerateCode，明确 agent 不生成代码 |
| 2 | `AiConversationService.cs:2196-2199` | 「**seven-stage** plan … fixed stages … **GenerateCode** … final GenerateCode task must use GenerationMode=Incremental」 | 同上，implementation planner 提示词同步 |
| 3 | `AiConversationService.cs:2121` | 「the client run the final incremental **code-generation pipeline**」 | 删除；元数据完成后流程即结束 |
| 4 | `AiConversationService.cs:2183, 2202` | 「**seven-stage** workflow rules (including the GenerationMode=Incremental + no-migration rule for the final task)」 | 六阶段，删除 final task 生成规则 |
| 5 | `AiConversationService.cs:2161, 2162, 2123` | 「final **materialization pipeline**」「**seven-stage** plan」 | 改为「元数据准备完成后由用户手动生成」表述 |
| 6 | `CodeMasterAgentToolCatalog.cs:160,165,202` | 工具 description 列 GenerateCode 阶段，且 upsert 描述写着「then one project generation, migration, complex-page generation, build, and test pipeline」 | 六阶段；删除生成管线描述 |
| 7 | `AiProjectPlanTurn.cs:163` | 「compiles the **seven-stage** executable plan」 | six-stage |
| 8 | `AiProjectWorkPlanContracts.cs:159` | `StageKind` Description 含 GenerateCode | 六阶段（保留 Legacy 兼容说明） |
| 9 | `BusinessPlanContracts.cs:13-14`、`BusinessPlanCompiler.cs:8,62`、`EngineeringFallbackTemplate.cs:7`、`AiProjectWorkSpecialOperationAdapters.cs:70` | 注释/告警文案「seven-stage」 | 文案同步 |

### P1 前端抽屉仍在自动跑生成/构建/测试（用户直接可见的行为）

| # | 位置 | 现状 |
|---|---|---|
| 10 | `AgentDrawer.vue:963-964` | 元数据全部应用后 `ElMessage.info('开始统一生成、迁移、构建和测试')` + `await materializeCurrentProject()` |
| 11 | `AgentDrawer.vue:1947-1948` | 自动批准流里同样调用 |
| 12 | `AgentDrawer.vue:144-153` | 「重新生成并验证」按钮，文案「备用入口；正常流程会在元数据完成后自动执行」 |
| 13 | `AgentDrawer.vue:2144-2163` + `projectMaterialization.js` | localStorage 物化状态机与刷新恢复（`PROJECT_MATERIALIZATION_ACTIONS` = 增量生成/复杂页面/构建/测试） |
| 14 | `AgentDrawer.vue:77,104,148,707` | `materializingProject` 禁用态与 ref |

改为：元数据完成后只提示「元数据已就绪，请到项目页面点『生成代码』」（可带跳转），
删除物化状态机。这也顺带消除了原评审第 3 条「物化依赖浏览器存活」在 Agent 侧的部分。

### P2 后端仍可发生成类客户端动作（flag 门控，默认关但路径存在）

| # | 位置 | 现状 |
|---|---|---|
| 15 | `UiCompositionAgentToolExtension.cs:323-333` | view 计划 `GenerateAfterApproval` → `generateIncrementalCode`；`BuildAfterGeneration` → `buildProject` |
| 16 | `UiCompositionAgentToolExtension.cs:367-377` | OpenAPI 导入计划同样两个 flag |
| 17 | `UiCompositionAgentToolExtension.cs:392-400` | 复杂页面计划 `GenerateAfterApproval` → `generateComplexPage` |
| 18 | `CodeMasterProjectChangeSetService.cs:372-418` | change_set `GenerationMode != None` → `generateProjectSmartCode` + `buildProject` + `testProject`；`RequiresBuildPermission` 同样按生成模式放行 |
| 19 | `ComplexPageBatchOperationAdapter.cs:89-100` | `plan.GenerateAfterApproval` → `generateComplexPage` + `buildProject` |
| 20 | `CodeMasterAgentToolFactory.cs:1896-1903` | UI 页面增强 `BuildAfterApply` → `buildProject` |

改为：删除这些发射点；`CodeMasterProjectChangeSetRules` 的 `GenerationModes`
收敛为只允许 `None`（模型侧不再有生成模式可选）。

### P3 兼容层（10-17 决定保留，本次不动）

- `AiProjectWorkPlanService.cs:39-41,1339-1362`：`SevenStageOrder` / `isGenerateStage` 校验，
  顺带保证「非 GenerateCode 阶段不得请求 generation/build」——留着当护栏。
- `AiProjectWorkSpecialOperationAdapters.GenerateProjectBatchOperationAdapter`：`generate_project` 操作适配器。
- `AiConversationService.cs:64-79`（`AllowedFailedActions`）与 `1812-1881`（失败回流分类）：
  真出现这类动作时仍能归类，属于诊断编目而非发起路径。
- `AiProjectWorkPlanServiceTests.cs:365`：校验测试用的 GenerateCode stage。

## 4. 验收标准

1. 全库 grep：`CodeMaster.Agent` / `CodeMaster.Private/UiComposition.Agent` 中不再有
  向 `AiClientActionDto.Action` 写入 generate*/build*/test*/migrate* 的代码路径。
2. 提示词与工具 description 中不再出现 `GenerateCode` 阶段与 `GenerationMode=Incremental`。
3. 前端抽屉无「生成/构建/测试」入口；元数据完成只提示去项目页面手动生成。
4. `dotnet build` 0 错误；`CodeMaster.Agent.Tests` / `CodeMaster.OpenSpec.Tests` 编译通过
   （本机 `dotnet test` 不可用，见记忆 2026-10-19）。
5. 回归一轮 E2E：`agent_e2e_full.py` 跑到计划 45/45 即止，**不**自动生成代码；
   再单独走项目页面 `generateprojectcode`，确认生成 + 复杂页面 + 编译 0 错误仍然成立
   （该链路今天已验证：EngPlatform2/3/4）。

## 5. 不在本次范围

- 打印模板 Agent 化（用户 2026-10-19 明确暂不做）。
- 项目页生成链路自身的断线续跑（原评审第 3 条的另一半，独立课题）。
- `sys_project_page` 的 `generateAfterApproval` 硬编码四处（`ProjectWorkOperationDefinitionNormalizer`）
  ——它本来就是「计划只做元数据」的正确行为，保留。

---

## 6. 执行结果（2026-10-20）

### P0 提示词与工具描述（已改）

- `AiConversationService.cs`：主计划提示词、implementation planner 提示词、
  `submit_business_plan` / `upsert_project_work_plan` 说明、视图与变更集的两处
  "materialization pipeline" 表述，全部改为**六阶段、止于 ConsistencyCheck**，
  并明确「生成源代码是用户在项目页面的手动动作」。
- `CodeMasterAgentToolCatalog.cs`：三处工具 description 去掉 GenerateCode 阶段与
  「then one project generation, migration, complex-page generation, build, and test pipeline」。
- `AiProjectPlanTurn.cs`、`AiProjectWorkPlanContracts.cs`（StageKind Description）、
  `BusinessPlanContracts.cs` / `BusinessPlanCompiler.cs` / `EngineeringFallbackTemplate.cs` /
  `AiProjectWorkSpecialOperationAdapters.cs` 的 seven-stage 注释与告警文案同步。

### P1 前端抽屉（已改）

- 删除元数据完成后的自动 `materializeCurrentProject()`（两处调用点），
  改为 `ElMessage.success('元数据已全部应用；源代码请在项目页面手动生成')`。
- 删除「重新生成并验证」按钮，换成常驻说明「元数据由 Agent 准备；源代码请在项目页面手动生成」
  （CSS 类改名 `agent-scope-hint`）。
- 删除整套 localStorage 物化状态机与刷新恢复（约 133 行）、
  `materializingProject` / `materializationRecoveryRunning` 状态与禁用表达式。
- 删除 `projectMaterialization.js`（无其他引用）。
- `AgentDrawer.vue` 通过 `@vue/compiler-sfc` 解析 + `compileScript` 校验（本机 vite 起不来）。
- 客户端动作分发器（`executeClientActions`）**保留**：服务端不再发射这些动作，
  留着只为兼容历史计划，与 P3 立场一致。

### P2 生成/构建/测试动作出口（已全部关闭）

| 位置 | 处理 |
|---|---|
| `UiCompositionAgentToolExtension`（view / openapi / complexpage） | 删除 3 处生成动作、3 处 `buildProject`、3 处按需授权、2 处 `BuildAfterGeneration requires GenerateAfterApproval` 校验 |
| `ViewDefinitionBatchOperationAdapter` / `ComplexPageBatchOperationAdapter` | 删除生成动作发射与按需授权（`ResolveAsync` 早在 10-17 就把两个 flag 强制为 false，此处为清理死代码） |
| `CodeMasterProjectChangeSetService` | `AddClientActions` 改为空操作（保留签名），`RequiresBuildPermission` 恒为 false |
| `CodeMasterProjectChangeSetRules` | `GenerationModes` 收敛为只允许 `None`，错误文案同步 |
| `CodeMasterAgentToolFactory`（ui_page_enhancement） | 删除 `BuildAfterApply` → `buildProject` |
| `GenerateProjectBatchOperationAdapter`（`generate_project`） | `ExecuteAsync` 改为元数据-only no-op，只回报「元数据已就绪，请在项目页面手动生成代码」；不再需要 codegen/build 权限 |
| 契约开关 `GenerateAfterApproval` / `BuildAfterGeneration` / `BuildAfterApply` | 默认值 `true` → `false`，并加 `[Description]` 说明「Ignored by the Agent」 |

同步更新的测试：`AgentClientActionTests`（4 条 → 2 条：不再有生成/构建动作、
`RequiresBuildPermission` 恒 false）、`CodeMasterProjectChangeSetRulesTests`
（`Incremental` 用例改 `None`）。

### 编译验证

`CodeMaster.Agent`、`CodeMaster.Agent.Tests`、`CodeMaster.Private/UiComposition`、
`CodeMaster.Private/UiComposition.Agent`、`CodeMaster.WebApi` 全部 0 错误 0 警告。

最终核查：`CodeMaster.Agent` + `CodeMaster.Private/UiComposition.Agent` 中
已无任何向 `AiClientActionDto.Action` 写入 generate* / build* / test* / migrate* 的路径。

### 唯一保留的源码触碰能力（需用户知晓）

`inspect_codemaster_source` / `apply_test_source_repair` 两个工具会读写
**CodeMaster 平台自身的源码**（白名单工作区 + SHA-256 前置镜像 + 具名校验档），
仅在显式授权的 Development/TestSourceRepair 模式下的 `includeSourceRepair` 分支注册，
与业务 Agent 流程无关，也不碰生成项目的源码。

### 源码修复工具已按用户决定禁用（2026-10-20 追加）

`inspect_codemaster_source` / `apply_test_source_repair` 原本可读写 **CodeMaster 平台自身源码**
（白名单工作区 + SHA-256 前置镜像）。用户确认后已切断全部接线：

| 位置 | 处理 |
|---|---|
| `CodeMasterAgentToolCatalog.Create` | 删除 `includeSourceRepair` / `inspectCodeMasterSource` / `applyTestSourceRepair` 三个参数与整个注册块——**结构上不可能再注册** |
| `CodeMasterAgentToolFactory` | 删除两个处理器、`RequireTestSourceRepairPlanAsync`、`canSourceRepair` 计算、`_sourceRepairExecutor` 字段与构造参数 |
| `AgentServiceCollectionExtensions` | 移除 `IAiProjectSourceRepairExecutor` 的 DI 注册 |
| `AiConversationService` | 两处提示词改为「The agent never reads or patches CodeMaster source code…」 |
| `AiProjectWorkPlanService` | 计划快照 `SourceRepairEnabled` 恒为 false；提示文案改为「已禁用」 |
| `AiProjectWorkPlanContracts` | `RepairMode` / `AiProjectRepairPolicyDto` 描述同步（TestSourceRepair 仍被接受，仅为兼容存量计划） |
| `AiProjectSourceRepairExecutor.cs` | 实现保留为**休眠代码**（文件头注明禁用原因），单测仍孤立覆盖它 |
| `CodeMasterAgentToolCatalogTests` | 原「opt-in」测试改为 `Source_repair_tools_are_never_registered` |

`CodeMaster.Agent` / `CodeMaster.Agent.Tests` / `CodeMaster.WebApi` 均 0 错误 0 警告。
