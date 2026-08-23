# 手动添加待办与自定义分组实施任务

## 1. 执行规则

- 本文件只有在 `requirements.md`、`design.md`、`tasks.md` 和 `check_list.md` 四份当前版本被用户整体确认后才可执行。
- 实施必须遵循仓库 TDD 规则：先加入能证明目标行为的失败测试并记录失败原因，再实施最小变更，最后运行完整回归。
- 工作区已有大量无关未提交修改。不得执行 `git reset --hard`、`git checkout --` 或批量覆盖；只编辑当前任务明确列出的文件，并在交付前复核目标 diff。
- 每个任务只有在其验证方法得到可复核证据后才能标记完成；编译成功不能替代行为测试，自动化测试也不能替代明确列出的 WPF 手工交互。
- 仓库测试是控制台自定义运行器。标准验证必须先串行 Release 构建，再运行：

  ```powershell
  dotnet build WechatDashboard.sln --configuration Release --no-restore --disable-build-servers -m:1 -v minimal
  dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-build
  ```

- `dotnet test` 不能作为本仓库的回归通过证据。
- 若实施发现已审批的字段、交互、事务或迁移设计需要实质变化，立即停止实现，修订受影响的 SDD 文档并重新取得四份文档确认。

## 2. 任务列表

### TASK-001：建立分组领域模型与 v3 数据库迁移

- 交付物：新增 `TodoGroup`；为 `TodoItem` 增加可空 `GroupId`；在 `SqliteDatabaseInitializer` 增加 v3 `todo-groups` 迁移，创建 `todo_groups`、`todo_items.group_id` 外键和索引；更新所有实体构造和 SQLite 行映射使解决方案恢复编译。
- 需求/设计引用：REQ-005、REQ-008；D-001、D-002、D-012，设计 6.1、10.1。
- 覆盖验收：AC-021、AC-025、AC-035、AC-038。
- 依赖：四份 SDD 文档已整体确认。
- TDD 步骤：先增加“旧数据库迁移保留待办”和“迁移失败不记录 v3”测试并取得失败证据；再实现迁移和模型变更。
- 验证方法：运行串行 Release 构建和控制台回归运行器；在测试中比较迁移前后旧待办除新增空 GroupId 外的全部字段，并重复执行初始化两次；运行 `git diff --check`。
- 预期结果：构建 0 错误且无新增警告；全部测试通过；旧待办字段不变且 `GroupId=null`；v3 只记录一次；失败事务不留下 `todo_groups`、`group_id` 或 v3 记录的半完成组合。
- 并行：否，后续任务依赖该模型和 schema。

### TASK-002：实现分组名称策略、仓储和应用服务

- 交付物：新增 `TodoGroupNamePolicy`、`ITodoGroupRepository`、`SqliteTodoGroupRepository`、`TodoGroupApplicationService` 和可枚举创建结果；实现一次读取全部分组、按名称排序、按 ID 查询及新增；把 SQLite 唯一冲突映射为 `AlreadyExists`。
- 需求/设计引用：REQ-004、REQ-007；D-003、D-004，设计第 4、5、6.2、8 节。
- 覆盖验收：AC-014 至 AC-018、AC-020、AC-032、AC-034。
- 依赖：TASK-001。
- TDD 步骤：先加入“规范化分组名称并拒绝空值/重复值/并发重复”的失败测试；再实现策略、仓储和服务。
- 验证方法：运行串行 Release 构建和控制台回归运行器；测试首尾空白、Unicode Form C、仅大小写不同、两个并发创建、空库、读取失败和重启读取；检查数据库 `normalized_name` 唯一约束。
- 预期结果：合法分组持久化一次并可重启读取；显示名保留 Trim 后文本；规范化等价名称最多一条；失败被分类而非暴露原始 SQL；读取失败不会伪装成空列表。
- 并行：否，为创建和详情界面提供共同契约。

### TASK-003：实现手动待办创建用例与原子事务

- 交付物：新增 `CreateManualTodoRequest/Result/Outcome`；扩展 `TodoApplicationService` 和 `ITodoUnitOfWork`；在 `SqliteTodoUnitOfWork` 中实现手动待办与可选首次提醒的单事务创建；验证可选 GroupId 存在。
- 需求/设计引用：REQ-002、REQ-003、REQ-004、REQ-007；D-005、D-006，设计第 5、6.2、7.3、8 节。
- 覆盖验收：AC-004 至 AC-012、AC-019、AC-031。
- 依赖：TASK-001、TASK-002。
- TDD 步骤：先加入“手动待办应用默认值并与提醒原子提交”的失败测试；覆盖空标题、Trim、无效提醒、GroupMissing 和提醒插入失败回滚；再实施最小用例。
- 验证方法：运行串行 Release 构建和控制台回归运行器；查询 SQLite 证明成功时只有一条 `SourceMessageId=null`、`IsAutoCreated=false`、`Pending`、未置顶、`ProjectId=null` 的待办，失败时待办与提醒均未残留。
- 预期结果：所有校验和默认值测试通过；合法创建可带或不带分组/提醒；不存在的 GroupId 返回业务结果；待办和首次提醒不会部分提交。
- 并行：否，创建 UI 依赖本用例。

### TASK-004：让现有待办读写和生命周期完整携带 GroupId

- 交付物：扩展 `UpdateTodoRequest`、Todo 工厂、`SqliteTodoRepository` 和 `SqliteTodoUnitOfWork.UpdateTodoAsync`；所有 SELECT/INSERT/UPDATE 行映射明确处理 `group_id`；自动创建和消息转待办显式保持空分组；完成/忽略保留分组。
- 需求/设计引用：REQ-005、REQ-008；D-002、D-006、D-008、D-012，设计 6.3、9.3。
- 覆盖验收：AC-021 至 AC-025、AC-035 至 AC-037。
- 依赖：TASK-001、TASK-002、TASK-003。
- TDD 步骤：先加入“更新分组不复制待办”和“完成后保留分组”的失败测试；补充旧自动/消息转换幂等回归；再调整持久化路径。
- 验证方法：运行串行 Release 构建和控制台回归运行器；测试设置、更换、清除分组、乐观并发冲突、完成、忽略、批量完成、提醒弹窗完成和重复消息转换。
- 预期结果：GroupId 随正常读取/更新持久化；状态生命周期不因分组改变；完成后分组保留、置顶清除、开放提醒取消；来源消息幂等仍只返回原待办。
- 并行：否，先统一数据契约，避免 UI 在不完整模型上开发。

### TASK-005：实现可测试的手动创建 ViewModel

- 交付物：新增 `TodoCreateViewModel` 及创建对话框所需状态/命令；加载分组和 UI 专用“未分组”项；支持现场新增并自动选中新分组；解析标题、描述、优先级、截止/提醒时间；实现 busy、防重复提交、错误保留和成功关闭事件。
- 需求/设计引用：REQ-001、REQ-002、REQ-003、REQ-004、REQ-007；D-004、D-006、D-007，设计 6.2、7.1 至 7.3、8。
- 覆盖验收：AC-001 至 AC-009、AC-013 至 AC-020、AC-031、AC-034。
- 依赖：TASK-002、TASK-003、TASK-004。
- TDD 步骤：先为 ViewModel 增加不启动真实窗口的失败测试，覆盖加载成功/失败、取消、分组新增、验证、重复保存和服务失败；再实现命令状态。
- 验证方法：运行串行 Release 构建和控制台回归运行器；测试 fake clock、fake services 和可控并发保存；断言失败后所有有效字段保留，成功事件只触发一次。
- 预期结果：默认 P2，其余可选字段为空；所有字段错误可恢复；分组新增立即可选且取消待办不删除分组；同一 ViewModel 的并发保存只调用一次创建服务。
- 并行：完成 TASK-004 后，可与 TASK-007、TASK-008 并行，前提是不编辑相同文件。

### TASK-006：实现创建窗口、待办理入口和对话框接线

- 交付物：新增 `TodoCreateWindow.xaml/.cs`；在 `TodoPendingView` 增加“添加待办”按钮；扩展对话框服务/工作台命令打开模态窗口；提供字段标签、Tab 顺序、默认焦点、保存/取消、加载与错误状态；现场新增分组后保持当前表单。
- 需求/设计引用：REQ-001、REQ-002、REQ-004、REQ-007；D-004、D-007，设计第 4、7.1、7.2 节。
- 覆盖验收：AC-001 至 AC-003、AC-007、AC-008、AC-014 至 AC-020、AC-031、AC-034。
- 依赖：TASK-005。
- TDD 步骤：先增加对 ViewModel/命令绑定和窗口服务的可自动验证断言；再创建 XAML 和接线。真实点击留到 TASK-012。
- 验证方法：运行串行 Release 构建和控制台回归运行器；以 XAML/反射测试验证关键控件、Binding 和命令存在；人工代码审查确认按钮位于“待办理”页且未占用消息转换入口。
- 预期结果：创建窗口可由待办理页打开；必填/可选字段和分组操作完整；取消不写入；加载失败明确显示；键盘可完成主要操作。
- 并行：否，依赖创建 ViewModel；可在 TASK-007/008 已由独立工作完成后接线。

### TASK-007：在工作台批量解析并诚实展示分组和来源

- 交付物：向 `TodoWorkbenchViewModel` 注入分组仓储/服务；每次刷新只读取一次分组集合并建立字典；扩展列表行展示 GroupName；将手动添加、真实来源和来源消息丢失三种情况分开格式化；更新待办理和已办理卡片/列。
- 需求/设计引用：REQ-003、REQ-005、REQ-006、REQ-007；D-009、D-010，设计第 7.1、9.2 节。
- 覆盖验收：AC-012、AC-021、AC-025 至 AC-030、AC-034。
- 依赖：TASK-002、TASK-004。
- TDD 步骤：先加入“工作台一次读取分组并区分三种来源”的失败测试；再调整行模型、刷新和 XAML。
- 验证方法：运行串行 Release 构建和控制台回归运行器；使用计数 fake 断言一次刷新只有一次分组查询；测试有分组/未分组、手动来源、真实消息、消息丢失、无截止时间、完成/忽略展示。
- 预期结果：列表显示真实分组或“未分组”；手动待办显示“手动添加”而不伪造群聊/发送人/消息时间；消息丢失不被误判为手动；时间桶只由原策略决定。
- 并行：完成 TASK-004 后，可与 TASK-005、TASK-008 并行，前提是不编辑相同文件。

### TASK-008：在详情中设置分组并保持乐观并发

- 交付物：扩展 `TodoDetailViewModel` 和详情窗口，活动待办可选择、更换或清除分组；已完成/已忽略待办只读显示分组；GroupId 与其他字段一起通过 `ExpectedUpdatedAt` 保存；GroupMissing 时刷新选项并保留输入。
- 需求/设计引用：REQ-005、REQ-006、REQ-007；D-008、D-010，设计 6.3、7.4、8。
- 覆盖验收：AC-022 至 AC-025、AC-026、AC-027、AC-030、AC-031。
- 依赖：TASK-002、TASK-004。
- TDD 步骤：先加入详情 ViewModel 的选择、更换、清除、只读历史、并发冲突和 GroupMissing 失败测试；再实现 ViewModel/XAML。
- 验证方法：运行串行 Release 构建和控制台回归运行器；断言 UpdateTodoRequest 携带正确 GroupId 和 ExpectedUpdatedAt；重启仓储读取证明关联持久化。
- 预期结果：活动待办分组可安全维护；历史待办保留且只读展示；更新冲突不覆盖其他操作；分组缺失不丢失标题等表单内容。
- 并行：完成 TASK-004 后，可与 TASK-005、TASK-007 并行，前提是不编辑相同文件。

### TASK-009：组装功能并刷新工作台与外层聚合计数

- 交付物：在 `TodoFeatureCoordinator` 组装分组仓储/服务和创建用例；实现 `TodoChanged` 事件或等价回调；手动创建成功后刷新工作台，并由 `MainWindow` 复用现有聚合刷新路径更新待办理数和高优先级数；避免重复刷新循环。
- 需求/设计引用：REQ-001、REQ-007；D-011，设计第 4、7.3、9.4 节。
- 覆盖验收：AC-001、AC-013、AC-033。
- 依赖：TASK-005、TASK-006、TASK-007、TASK-008。
- TDD 步骤：先增加“成功通知一次、失败不通知、刷新后计数变化”的失败测试；再完成协调器和 MainWindow 接线。
- 验证方法：运行串行 Release 构建和控制台回归运行器；使用可计数回调验证通知次数；创建 P1/P2 手动待办分别核对活动数和高优先级数。
- 预期结果：创建成功无需重启即可同时看到新卡片和正确计数；失败/取消不改变计数；一次创建只触发一次外层通知且无递归刷新。
- 并行：否，是 UI 和服务汇合任务。

### TASK-010：补齐迁移、失败、并发和既有路径回归矩阵

- 交付物：扩展控制台测试矩阵，覆盖旧库升级、v3 幂等/校验和失败、并发分组唯一性、分组读取失败、待办/提醒事务失败、重复提交、详情冲突、自动待办、消息转待办、完成/批量完成/提醒完成及时间桶不变。
- 需求/设计引用：REQ-002 至 REQ-008；D-003、D-005、D-007 至 D-012，设计第 8 至 10 节。
- 覆盖验收：AC-004 至 AC-013、AC-015 至 AC-038。
- 依赖：TASK-001 至 TASK-009。
- TDD 步骤：审计前述任务测试与 AC 矩阵，先加入任何缺失的失败测试，再修复遗漏；不得为了通过测试削弱验收断言。
- 验证方法：运行串行 Release 构建和控制台回归运行器至少两次；第二次复用已迁移 fixture；运行 `git diff --check`；搜索 `todo_items` 的 SELECT/INSERT/UPDATE，确认所有实体行映射对 `group_id` 一致。
- 预期结果：两次完整运行均全部通过；迁移可重复；无新增重复待办/分组、半事务、错误时间桶或历史分组丢失；既有 45 项基线测试及所有新增测试均通过。
- 并行：否，作为自动化验收收口。

### TASK-011：执行完整构建、静态审查与范围核对

- 交付物：Release 构建日志、完整控制台测试日志、diff whitespace 结果、变更文件清单、需求/设计/任务追踪审计和无范围外实现声明。
- 需求/设计引用：REQ-001 至 REQ-008；设计第 9、10、12、13 节。
- 覆盖验收：AC-001 至 AC-038 的自动化部分。
- 依赖：TASK-010。
- TDD 步骤：本任务不主动增加产品行为；若完整验证发现缺陷，先增加能稳定复现该缺陷的失败测试，再返回对应实施任务修复并重新执行 TASK-010、TASK-011。
- 验证方法：执行以下命令并保存退出码及摘要：

  ```powershell
  dotnet build WechatDashboard.sln --configuration Release --no-restore --disable-build-servers -m:1 -v minimal
  dotnet run --project tests\WechatDashboard.Tests\WechatDashboard.Tests.csproj --configuration Release --no-build
  git diff --check
  git status --short
  ```

  另外逐文件审查目标 diff，确认未实现分组重命名/删除/排序/筛选、项目选择或时间桶重构。
- 预期结果：Release 构建 0 警告、0 错误；测试器输出 `All <最终注册数> tests passed.`；`git diff --check` 无输出；工作区原有无关修改被保留且目标变更可明确列出；全部 REQ/AC 有证据位置。
- 并行：否。

### TASK-012：执行真实 WPF 交互、重启与回滚验收

- 交付物：实际窗口操作记录或截图、SQLite 脱敏查询结果、应用重启结果、旧版数据库升级结果以及回滚应用版本后的数据保留记录；把证据回填到 `check_list.md`。
- 需求/设计引用：REQ-001 至 REQ-008；D-004、D-005、D-007 至 D-012，设计第 7、8、10 节。
- 覆盖验收：AC-001 至 AC-003、AC-007、AC-008、AC-012 至 AC-030、AC-033、AC-035 至 AC-038。
- 依赖：TASK-011。
- TDD 步骤：本任务不主动增加产品行为；若 GUI 验收发现缺陷，优先先加入可自动化的失败测试；确实无法自动化时，先记录可重复的前置条件、点击步骤和错误结果，再修复并重跑相关自动化与 GUI 步骤。
- 验证方法：在真实 WPF 中依次执行：打开/取消创建；创建未分组待办；现场新增并选择分组；创建带未来提醒的待办；重复快速点击保存；重启应用；在详情更换/清除分组；完成待办；验证手动/真实消息/消息丢失来源；验证所有时间桶；从旧库副本升级；回滚旧应用二进制读取同一数据库后再升级回来。
- 预期结果：界面行为与 AC 完全一致；无重复项、数据丢失、假来源或错误计数；提醒按时进入既有生命周期；旧版本忽略新 schema，新版恢复后分组仍存在。无法自动化的 GUI 项必须有截图或操作记录，不能用“编译通过”替代。
- 并行：否，最终串行验收。

## 3. 增量执行顺序

```text
TASK-001
   |
TASK-002
   |
TASK-003
   |
TASK-004
   |
   +------------+-------------+
   v            v             v
TASK-005     TASK-007      TASK-008
   |            |             |
TASK-006 -------+-------------+
                |
             TASK-009
                |
             TASK-010
                |
             TASK-011
                |
             TASK-012
```

TASK-005、TASK-007、TASK-008 只有在执行者能保证文件不重叠时才可并行；默认单代理实施时按编号顺序执行更安全。

## 4. 需求到任务追踪

| 需求 | 任务 |
|---|---|
| REQ-001 | TASK-005、TASK-006、TASK-009、TASK-011、TASK-012 |
| REQ-002 | TASK-003、TASK-005、TASK-010 至 TASK-012 |
| REQ-003 | TASK-003、TASK-005、TASK-007、TASK-010 至 TASK-012 |
| REQ-004 | TASK-002、TASK-003、TASK-005、TASK-006、TASK-010 至 TASK-012 |
| REQ-005 | TASK-001、TASK-004、TASK-007、TASK-008、TASK-010 至 TASK-012 |
| REQ-006 | TASK-007、TASK-008、TASK-010 至 TASK-012 |
| REQ-007 | TASK-002、TASK-003、TASK-005 至 TASK-010、TASK-011、TASK-012 |
| REQ-008 | TASK-001、TASK-004、TASK-010 至 TASK-012 |

## 5. 验收标准到任务逐项映射

| 验收标准 | 主要任务 |
|---|---|
| AC-001 | TASK-005、TASK-006、TASK-009、TASK-012 |
| AC-002 | TASK-005、TASK-006、TASK-012 |
| AC-003 | TASK-005、TASK-006、TASK-012 |
| AC-004 | TASK-003、TASK-005、TASK-010 |
| AC-005 | TASK-003、TASK-005、TASK-010 |
| AC-006 | TASK-003、TASK-005、TASK-010 |
| AC-007 | TASK-003、TASK-005、TASK-006、TASK-012 |
| AC-008 | TASK-003、TASK-005、TASK-006、TASK-012 |
| AC-009 | TASK-003、TASK-005、TASK-010 |
| AC-010 | TASK-003、TASK-010 |
| AC-011 | TASK-003、TASK-010 |
| AC-012 | TASK-003、TASK-007、TASK-012 |
| AC-013 | TASK-005、TASK-009、TASK-010、TASK-012 |
| AC-014 | TASK-002、TASK-005、TASK-006、TASK-012 |
| AC-015 | TASK-002、TASK-005、TASK-006、TASK-010、TASK-012 |
| AC-016 | TASK-002、TASK-005、TASK-010 |
| AC-017 | TASK-002、TASK-005、TASK-006、TASK-010 |
| AC-018 | TASK-002、TASK-005、TASK-010、TASK-012 |
| AC-019 | TASK-003、TASK-005、TASK-006、TASK-012 |
| AC-020 | TASK-002、TASK-005、TASK-006、TASK-012 |
| AC-021 | TASK-001、TASK-004、TASK-007、TASK-010 |
| AC-022 | TASK-004、TASK-008、TASK-012 |
| AC-023 | TASK-004、TASK-008、TASK-012 |
| AC-024 | TASK-004、TASK-008、TASK-012 |
| AC-025 | TASK-001、TASK-004、TASK-007、TASK-008、TASK-012 |
| AC-026 | TASK-007、TASK-008、TASK-012 |
| AC-027 | TASK-007、TASK-008、TASK-012 |
| AC-028 | TASK-007、TASK-010、TASK-012 |
| AC-029 | TASK-007、TASK-010、TASK-012 |
| AC-030 | TASK-007、TASK-008、TASK-012 |
| AC-031 | TASK-003、TASK-005、TASK-006、TASK-008、TASK-010 |
| AC-032 | TASK-002、TASK-010 |
| AC-033 | TASK-009、TASK-012 |
| AC-034 | TASK-002、TASK-005、TASK-007、TASK-010 |
| AC-035 | TASK-001、TASK-004、TASK-010、TASK-012 |
| AC-036 | TASK-004、TASK-010 |
| AC-037 | TASK-004、TASK-010、TASK-012 |
| AC-038 | TASK-001、TASK-010、TASK-012 |

所有 REQ-001 至 REQ-008 与 AC-001 至 AC-038 均由至少一个任务覆盖。最终完成状态和证据位置以 `check_list.md` 为准。
