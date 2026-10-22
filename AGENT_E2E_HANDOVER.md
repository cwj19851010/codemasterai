# Agent 端到端生成链路：排查结论与交接

> 记录时间：2026-10-17
> 结论：**对话 → 计划 → 建模 → 生成 → 可编译且有测试的工程** 这条链路已经跑通。

## 一、怎么复现 / 验证

```bash
# 干净项目跑完整流程（推荐：能区分"逻辑错"和"遗留数据干扰"）
bash tools/agent-e2e.sh run-fresh

# 在已有模型的项目上迭代（保留元数据，只清计划与会话）
SKIP_RESET=1 PROJECT_ID=<项目id> bash tools/agent-e2e.sh run

# 只看当前状态（含任务状态分布）
PROJECT_ID=<项目id> bash tools/agent-e2e.sh status
```

需要 WebApi 在 `http://localhost:5000` 运行，且模型配置已通过连接测试。

**验证生成物**（只能在普通终端跑，原因见第五节）：

```bash
cd /d D:\MyHomeWorks\AgentModeE2E
dotnet build AgentModeE2E.sln
dotnet test  AgentModeE2E.GeneratedTests\AgentModeE2E.GeneratedTests.csproj
```

## 二、已知结果（实测）

| 环节 | 结果 |
|---|---|
| 干净项目全流程 | `plan Completed (38/38)` |
| 生成代码文件 | 61 个 `.cs`，含视图消费实体 |
| 已有模型上迭代 | `14/14`，未撞"已存在"守卫 |
| 生成工程编译 | `0 个错误` |
| 生成工程测试 | `4/4 通过` |
| 二次增量生成 | `action ok`，不再撞 Vue 重复锚点 |
| **全新初始化 + 重新生成** | 编译 `0 错误`，测试 **4/4 通过**（模板全新展开、迁移补跑、代码重新生成） |

## 三、修掉的问题（按发现顺序）

### 1. 僵尸实体的悬空字段引用
`CodeMasterProjectChangeSetService` 新增 `RepairDanglingFieldLinksAsync`。
软删实体被"复活"后，其历史字段里指向已删除实体的 `select-table` 会让整批失败。
现在解析不出的关联降级为 `input`。

### 2. 手写 SQL 的视图计划永远过不了 `KeyColumn` 校验
`ProjectViewBuilderService.PreviewSqlAsync` 在 `input.Sql` 非空时**恒定**返回空字段列表
（`UiComposition.Tests` 里有断言，属有意设计）。而适配器要求 `KeyColumn` 必须命中某个声明字段 →
手工 SQL 的视图必然失败。
→ `ViewDefinitionBatchOperationAdapter` 新增 `DeriveFieldsFromManualSql`，按括号深度拆顶层 `SELECT` 投影。

### 3. 视图消费实体撞名
模型把只读消费实体命名成与可写实体同名 → 整批失败，而视图阶段很靠后，后面全部被跳过。
→ 确定性改名（`{name}View`，被占则加序号），并记入 `AutomaticRepairs`。
**坑**：`ResolveAsync` 组装返回 DTO 时用的是**局部变量** `entityName`，只改 `plan.EntityName` 会被覆盖。

### 4. 主键列被 SQL 起别名
`SELECT p.Id AS ProjectId` 时视图暴露的是 `ProjectId`，`keyColumn=Id` 永远匹配不上。
→ `ResolveKeyColumnAlias` 跟随 SQL 命名；必须在构造字段列表**之前**执行，否则主键字段类型不对。

### 5. DbContext 源生成器两个缺陷（`Templates/CodeMaster.SourceGenerator/`）
- **CS0104 类名歧义**：两个模块可以有同名实体，生成器把所有命名空间都 `using` 进来又用简单类名引用。
  ⚠️ 歧义必须按**去重前**的全部实体判断——同名实体往往连表名都相同，会被按表名去重掉。
- **CS0759 + DbSet 没挂上**：类名/命名空间写死成 `CodeMaster.Migrator...CodeMasterDbContext`，
  而模板按项目名生成 `AgentModeE2E.Migrator...AgentModeE2EDbContext`。
  后果不只是编译错误——**所有 DbSet 挂到了没人用的类上，迁移看不到生成的实体**。
  → 改为 `FindDbContextType(compilation)` 在编译里找真实 DbContext。

### 6. Vue 区域保护器把生成打断
`GetRegionHashes` 直接调 `ExtractRegions` **没有兜底**，而 `CollectRegions` 遇到重复 `data-gen-id` 会抛异常。
实测 54 个 `.vue` 里**没有**重复锚点 → 重复来自**解析树把同一节点走了两遍**。
→ 按对象引用去重；真重复不再抛异常（现有页面就近删多余副本自愈，生成结果降级为冲突）。

### 7. 重复的一对多关系 → 导航属性重复（CS0102）
同一对父子存在两条关系行时，实体模板会输出两个同名导航属性。
→ `RepairDanglingOneToManyRelationsAsync` 扩成自愈：按 `(父:子)` 收敛，保留 Id 最小的。

### 7.5 变更集把"等价的遗留关系"判为无效，整批失败
模型常把已存在的一对多关系换个显示名再提一次（例如把 `Supplier.PurchaseContract`
写成「供应商合同」），校验器判 `duplicates existing legacy one-to-many relation`，
**整个变更集作废**，后面排队的任务全部阻塞。
→ `CodeMasterProjectChangeSetRules` 改为：等价关系从提案里**摘掉**并记 `Warnings`，
不再报错——既不阻断，也不会插重复行。
配套测试 `Validate_DropsNewRelationEquivalentToLegacyOneToMany` 已同步修改并通过。

注：`ProjectChangeSetIssueRepairs.NormalizeOneToManyRelations` 做不了这件事（拿不到 blueprint），
`ProjectChangeSetDefinitionNormalizer` 有 blueprint 但只覆盖工作计划路径。

### 8. 回归脚本的口径问题
- 客户端动作有**三种**响应形状（`code=200` / `success:true` / 裸 `true`），原来只认第一种 → 把成功报成失败。
- `run_client_action` 原来无条件 `return 0`，失败计数永远为 0。
- `plan tasks=5/41` 读起来像失败 36 个，实际是模型自己用 `CancelTaskKeys` 取消了已存在的页面任务。
  接口**没有** `cancelledTasks` 字段，需从 `stages[].tasks[].status` 自行统计。

## 四、容易踩的坑（给下一个人）

1. **`AutomaticRepairs` 不写进后端日志**。不能用 grep 判断修复有没有生效，要看数据/产物的最终状态。
   （曾因此误判"改名没生效"，其实生效了。）
2. **`reset` 只做软删**，每轮都继承上一轮残骸 → 用 `run-fresh` 才能判断逻辑对错。
3. **项目 SQLite 路径** = `{ProjectPath}/{ProjectName}/{ProjectName}.Migrator/{dbFile}`，
   且 `ProjectPath` 必须以 `ProjectName` 结尾。目录不存在时报 `SQLite Error 14: unable to open database file`。
4. **`obj/GeneratedFiles` 下的转储不一定最新**，判断生成器落点要看配置类的命名空间。
5. `GET /api/codegen/project/getbyid/{id}` **直接返回对象**，没有 `data` 包装。

## 五、环境限制（代理侧做不到的）

### 「启动编译好的应用」会被拒，「SDK 进程内」不会

同一次初始化里，`dotnet ef migrations add` **成功**、`dotnet run` **失败**，差别在进程类型：

| 操作 | 结果 | 原因 |
|---|---|---|
| `dotnet build` | ✅ | 跑在 SDK 进程内 |
| `dotnet ef migrations add` | ✅ | dotnet-ef 工具跑在 SDK 进程内 |
| `dotnet run` / `dotnet test` | ❌ `0x80070005` / `0x80008082` | 需要启动编译好的应用（apphost → hostfxr），被拒 |
| **`dotnet exec <dll>` + 免沙箱** | ✅ | 宿主已加载，不走 apphost 加载路径 |

→ 跑编译好的 .NET 应用可以用：`dotnet exec bin/Debug/net10.0/<Name>.dll`（需免沙箱）。
用这招补跑了 Migrator（`AgentModeE2E.db` 生成、迁移+种子数据完成）。
但**测试仍然只能由人跑**——它要 testhost，这招不适用。

- **`dotnet test` 跑不了**：testhost 子进程加载 `hostfxr.dll` 报 `0x80070005`。→ e2e 里的
  `testProject` 在代理侧是**假阴性**，必须由人在普通终端执行。
- **干净项目不产出完整解决方案**（未走初始化 Step1–11），所以 `run-fresh` 只能验到
  "计划+元数据+代码文件"，验不了编译与测试。

## 六、待办

1. **孤儿生成文件清理（代码已回退，需要重新设计）**
   实体/模块消失后，生成的代码不会被删除，工程里会一直留着死代码。

   ⚠️ **踩过的坑（务必先看）**：曾按"只删 `{Entity}.auto.cs`"实现并实测，
   结果**把工程弄成编译不过**——基接口 `IEntity<long>` 声明在 `.auto.cs` 里，
   删掉后留下的空用户 partial 类变成裸类，而 `Application` 层的
   `{Entity}Dto.cs` / `I{Entity}Service.cs` / `{Entity}Service.cs` 还在引用它，报 `CS0311`。

   **正确单位是"实体"整条纵向切片，不是"文件类型"**：Domain + Dtos + Services 一起处理。
   用户文件（`{Entity}.cs`，内容通常只有 `// 在此添加自定义属性…` 样板注释）要么一起删，
   要么整切片跳过；框架目录 `System/`、`Monitor/` 不含 `.auto.cs`，天然不会命中。
   删前应校验用户文件是否仍是样板，避免误删手写代码。
2. 视图定义的幂等性：孤儿只读消费实体已有认领逻辑，但仍建议补回归用例。
3. `tools/agent-e2e.sh` 的 `testProject` 可在检测到 hostfxr 失败时明确标注为"环境跳过"，
   避免把环境问题当成产品失败。

4. **项目初始化不会清目录**
   `POST /api/codegen/project/initialize` 在项目已初始化的状态下会跳过模板重建；
   想真正"从模板重来一遍"，必须**先删掉目标目录**再初始化。

   另外它的迁移步骤（`dotnet run`）在受限 shell 里跑不了。补跑方式：
   ```
   cd /d D:\MyHomeWorks\AgentModeE2E\AgentModeE2E.Migrator
   dotnet exec bin/Debug/net10.0/AgentModeE2E.Migrator.dll    # 需免沙箱
   ```
   注意：手工补跑后 `getinitializationstate` 里的 `migrationApplied` 仍是 `false`——
   该状态记在项目文件里（目录被清时一并丢失），不是按产物推导，不影响实际功能。

5. **删除被"占用"的目录时的判定手法**
   若所有子目录都能改名、只有根目录改不了 → 不是文件句柄，而是**某进程把该目录当作 CWD**。
   常见持有者：停在那个路径的终端/编辑器、Windows 搜索索引、资源管理器窗口。
   另外本机 `Remove-Item` 被 safe-delete 守卫接管（走回收站），
   两万多个文件一次进回收站会报 `Some operations were aborted`，
   **别把这个报错当成"被占用"**。
