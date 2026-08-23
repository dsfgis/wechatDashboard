# 手动添加待办与自定义分组设计

## 1. 设计目标与非目标

### 1.1 设计目标

- 在现有 Todo 分层中增加不依赖消息的手动创建用例，并复用现有待办生命周期、提醒和刷新机制。（REQ-001、REQ-003）
- 用独立、可持久化、可并发去重的自定义分组模型组织待办，不与项目或截止时间分类混用。（REQ-004、REQ-006）
- 让手动、消息转入和自动生成的待办共享同一种可空分组关联，并能在活动待办详情中修改。（REQ-005）
- 通过原子事务、数据库约束、忙碌状态和可恢复错误保证创建过程不会产生重复或半成品数据。（REQ-002、REQ-003、REQ-007）
- 使用向后兼容的 SQLite 迁移保留全部旧待办和既有自动/消息待办行为。（REQ-008）

### 1.2 非目标

- 不实现分组重命名、删除、排序、筛选、折叠、统计或批量移动。
- 不用分组替代“已置顶、已逾期、今日到期、后续到期、无截止时间”。
- 不把自定义分组映射到 `projects`、`followed_projects` 或项目关键字。
- 不在本功能中增加项目选择、循环待办、子任务、附件、标签或云同步。
- 不改变自动建待办和消息转待办的触发条件或基于来源消息的幂等语义。

## 2. 当前系统上下文与拟议架构

### 2.1 当前上下文

现有 Todo 功能已按以下边界组织：

- `TodoItem` 表达待办领域数据，`SourceMessageId` 已允许为空。
- `TodoApplicationService` 校验用例输入，当前只有从消息创建和更新用例。
- `ITodoUnitOfWork` / `SqliteTodoUnitOfWork` 负责待办、提醒及状态变更的事务边界。
- `ITodoRepository` / `SqliteTodoRepository` 负责列表、详情、完成和置顶持久化。
- `TodoWorkbenchViewModel` 负责活动/已办列表、截止时间分类和批量操作。
- `TodoDetailViewModel` 负责标题、描述、优先级、状态、截止时间和提醒编辑。
- `TodoFeatureCoordinator` 组装仓储、服务、工作台、消息流和提醒后台工作器。
- `SqliteDatabaseInitializer` 以带版本、名称和校验和的事务迁移演进 schema；当前迁移版本为 1 和 2。

当前缺口是：没有手动创建请求/事务方法、没有分组领域模型与仓储、没有 `todo_items.group_id`，也没有创建窗口和分组编辑边界。

### 2.2 拟议架构

```text
TodoPendingView
   |
   | AddTodoCommand
   v
TodoCreateWindow / TodoCreateViewModel
   |                         |
   | CreateManualAsync       | CreateGroupAsync / GetGroupsAsync
   v                         v
TodoApplicationService   TodoGroupApplicationService
   |                         |
   v                         v
ITodoUnitOfWork          ITodoGroupRepository
   |                         |
   +-----------+-------------+
               v
       SQLite repositories
        |              |
   todo_items      todo_groups
        |
   todo_reminders

TodoDetailViewModel ------> TodoGroupApplicationService
          |
          +---- UpdateTodoAsync(GroupId)

TodoWorkbenchViewModel ---- one group-list query per refresh
          |
          +---- existing pinned/due bucket classification remains unchanged
```

WPF 只维护表单状态和用户交互；输入校验与用例结果在 Application 层表达；唯一性、外键和原子性由 Infrastructure/SQLite 最终保证。（REQ-002、REQ-004、REQ-007）

## 3. 核心设计决策

### D-001：分组是独立聚合，不复用项目

新增 `TodoGroup`，只包含分组标识、显示名称、规范化名称和创建时间。`TodoItem` 新增可空 `GroupId`。分组与 `projects`、`followed_projects` 没有外键或同步关系。（REQ-004、REQ-005、AC-019 至 AC-025）

原因：项目用于消息分类和看板聚合，自定义分组用于用户自己的 Todo 组织。如果复用项目，删除/关键字匹配/自动分类会意外改变待办分组，并让“未分组”语义不清。

### D-002：“未分组”使用空外键，不创建特殊记录

`TodoItem.GroupId=null` 是唯一的未分组状态，界面把它格式化为“未分组”。数据库中不创建保留 ID 或名为“未分组”的系统分组。（REQ-004、REQ-005、AC-020、AC-021）

这避免特殊记录被重命名、重复或误选，也使旧待办升级后自然处于未分组状态。

### D-003：分组名称以显示值和规范化值双字段保存

显示名称使用 `Name.Trim()` 后的文本；唯一键使用应用层统一生成的 `NormalizedName`：先做 Unicode Form C 规范化，再调用 `ToUpperInvariant()`。数据库对 `normalized_name` 建立唯一索引。（REQ-004、REQ-007、AC-015 至 AC-018、AC-032）

应用层先查询并返回友好错误；数据库唯一约束处理两个并发请求都通过预检查的竞争条件。不得仅依赖 SQLite `NOCASE`，因为它的默认大小写折叠不能完整覆盖非 ASCII 名称。

### D-004：新增分组和创建待办是两个独立提交边界

用户在创建窗口中点击“新增分组”时立即调用 `TodoGroupApplicationService.CreateAsync` 并提交。成功后刷新分组集合并选中新分组。随后取消创建待办不会删除该分组。（REQ-004、AC-015、AC-018）

原因：用户已确认分组本身支持手动增加，且取消待办后分组仍应保留。把两者放入一个长事务会让分组是否存在取决于窗口后续操作，并增加锁持有时间。

### D-005：手动待办与首次提醒保持单事务创建

`ITodoUnitOfWork` 新增 `CreateManualAsync`。它在一个连接和事务中插入 `todo_items`，并在用户填写首次提醒时插入 `todo_reminders`；任一步失败都回滚。手动待办固定使用 `SourceMessageId=null`、`IsAutoCreated=false`、`Status=Pending`、`ProjectId=null`、`IsPinned=false`。（REQ-003、AC-010 至 AC-012）

分组必须在事务开始后按 ID 验证存在；若不存在，返回 `GroupMissing`，不得依赖外键异常作为用户提示。

### D-006：手动创建使用独立请求，不伪装成“从消息创建”

新增 `CreateManualTodoRequest`，不向现有 `CreateTodoFromMessageRequest` 填充虚假 `MessageId`。`TodoApplicationService.CreateManualAsync` 负责标题规范化、默认优先级、时间校验并调用工作单元。（REQ-001 至 REQ-003、AC-004 至 AC-010）

现有 `CreateFromMessageAsync` 保持原签名和幂等流程，自动/消息待办仍默认 `GroupId=null`。（REQ-008、AC-036）

### D-007：表单级忙碌状态阻止同一次提交重复执行

`TodoCreateViewModel` 使用现有 `AsyncRelayCommand`，在执行中禁用保存和关闭后的重复命令。成功结果只处理一次：刷新工作台、通知外层计数变更、请求关闭窗口。失败时保持输入并恢复命令。（REQ-003、REQ-007、AC-013、AC-031、AC-033）

这保证“一次表单提交最多一条”。系统不对两个独立窗口中内容相同的手动待办做全局去重，因为它们可能是两个真实任务。

### D-008：详情更新把分组纳入现有乐观并发

`UpdateTodoRequest` 新增 `GroupId`，`SqliteTodoUnitOfWork.UpdateTodoAsync` 在同一个带 `ExpectedUpdatedAt` 条件的更新中写入 `group_id`。分组更换、标题和状态更新共享同一乐观并发结果。（REQ-005、AC-022 至 AC-025）

活动待办详情加载全部分组并提供“未分组”。已完成/已忽略详情只显示分组，不在本期开放修改。

### D-009：列表每次刷新只读取一次分组集合

`TodoWorkbenchViewModel.RefreshAsync` 在读取待办时并行或顺序读取一次全部分组，构建 `GroupId -> Name` 字典，再生成列表行。不得为每条待办调用一次分组查询。（REQ-006、NFR-004）

分组只作为行展示字段，不参与 `TodoDueBucketPolicy`，所以改变分组不会改变现有时间桶。（AC-026、AC-028 至 AC-030）

### D-010：手动来源与“来源消息丢失”分开表达

列表行和详情根据以下状态格式化来源：（REQ-006、AC-027）

1. `SourceMessageId=null`：来源显示“手动添加”，消息上下文显示“无来源消息”；内容区域显示待办描述，描述为空时显示标题。
2. `SourceMessageId` 有值且消息可读取：显示真实平台、群聊、发送人、消息时间和正文。
3. `SourceMessageId` 有值但消息不可读取：显示“原始消息已不存在”，不得误标为手动添加。

### D-011：用变更事件刷新 MainWindow 聚合计数

`TodoFeatureCoordinator` 在手动创建成功后先刷新 `Workbench`，再发出无负载的 `TodoChanged` 事件或调用等价的注入回调。`MainWindow` 复用现有聚合刷新路径更新待办理数和高优先级数。（REQ-007、AC-033）

事件不传递表单内容，避免 MainWindow 重新承载 Todo 业务。消息转待办、完成等已有路径可逐步复用同一通知，但本功能只要求手动创建路径接入。

### D-012：采用追加式迁移，旧版本可忽略新列

新增 schema migration v3：先创建 `todo_groups`，再向 `todo_items` 添加可空 `group_id` 外键并创建索引。迁移及 `schema_migrations` 记录在同一事务中提交。（REQ-008、AC-035、AC-038）

迁移不回填、不重写旧待办。旧应用使用显式列清单且可忽略新增表/列，因此应用版本回滚不需要破坏性降级迁移；新版再次启动仍通过迁移校验和验证 schema。

## 4. 组件职责

| 组件 | 层 | 新增或调整职责 | 追溯 |
|---|---|---|---|
| `TodoGroup` | Domain | 表达分组 ID、显示名、规范化名、创建时间 | REQ-004 |
| `TodoItem` | Domain | 增加可空 `GroupId` | REQ-005、REQ-008 |
| `TodoGroupNamePolicy` | Application | Trim、Unicode Form C、Invariant 大写规范化 | REQ-004、REQ-007 |
| `ITodoGroupRepository` | Application | 读取全部、按 ID 读取、创建分组 | REQ-004 |
| `TodoGroupApplicationService` | Application | 名称校验、重复结果和新增用例 | REQ-004、REQ-007 |
| `TodoApplicationService` | Application | 手动创建校验与默认值 | REQ-001 至 REQ-003 |
| `ITodoUnitOfWork` | Application | 新增手动待办与首次提醒原子创建 | REQ-003 |
| `SqliteTodoGroupRepository` | Infrastructure | 分组查询、唯一冲突映射 | REQ-004、REQ-007 |
| `SqliteTodoUnitOfWork` | Infrastructure | 插入手动待办/提醒、更新 GroupId | REQ-003、REQ-005 |
| `SqliteTodoRepository` | Infrastructure | 持久化/读取 `group_id`，保留完成路径 | REQ-005、REQ-008 |
| `SqliteDatabaseInitializer` | Infrastructure | 执行 v3 分组迁移 | REQ-008 |
| `TodoCreateViewModel` | App | 表单状态、命令、错误展示和成功关闭 | REQ-001、REQ-002、REQ-007 |
| `TodoCreateWindow` | App | 模态创建界面和可访问性标签 | REQ-001、REQ-004 |
| `TodoDetailViewModel/Window` | App | 活动待办分组选择，历史待办分组只读显示 | REQ-005 |
| `TodoWorkbenchViewModel` | App | 添加命令、批量解析分组名、列表刷新 | REQ-001、REQ-006 |
| `TodoFeatureCoordinator` | App | 组装服务并通知外层聚合刷新 | REQ-007 |

## 5. 接口与内部契约

以下为建议契约。实现时可以按仓库命名规范微调类型名，但字段语义和结果分类不得改变。

```csharp
public sealed record TodoGroup(
    long Id,
    string Name,
    string NormalizedName,
    DateTimeOffset CreatedAt);

public interface ITodoGroupRepository
{
    Task<IReadOnlyList<TodoGroup>> GetAllAsync(CancellationToken cancellationToken);
    Task<TodoGroup?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task<CreateTodoGroupResult> CreateAsync(
        string name,
        string normalizedName,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed record CreateManualTodoRequest(
    string Title,
    string? Description,
    PriorityLevel? Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? FirstReminderAt,
    long? GroupId);

public enum CreateManualTodoOutcome
{
    Created,
    GroupMissing
}

public sealed record CreateManualTodoResult(
    CreateManualTodoOutcome Outcome,
    TodoItem? Todo);
```

`UpdateTodoRequest` 增加 `long? GroupId`。`TodoItem` 增加 `long? GroupId`，所有从消息或自动创建的工厂调用显式或默认传入 `null`，避免旧路径语义漂移。（REQ-005、REQ-008）

分组创建结果至少区分 `Created` 与 `AlreadyExists`；输入为空由应用服务抛出或返回明确的 validation 结果，数据库冲突必须映射为 `AlreadyExists`，不能向 UI 泄露原始 SQL。（AC-017、AC-032）

## 6. 数据模型、验证规则与生命周期

### 6.1 SQLite 模型

迁移 v3 建议 SQL 语义：

```sql
CREATE TABLE todo_groups (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE(normalized_name)
);

ALTER TABLE todo_items
    ADD COLUMN group_id INTEGER NULL REFERENCES todo_groups(id);

CREATE INDEX idx_todo_items_group_id ON todo_items(group_id);
```

本期没有删除分组接口，因此不定义级联删除。未来若增加删除功能，必须先重新确定有关联待办时的产品语义，不能默认 `CASCADE` 删除待办。（REQ-008）

### 6.2 验证规则

| 字段/操作 | 规则 | 失败行为 | 追溯 |
|---|---|---|---|
| 标题 | Trim 后非空 | 保留表单并提示“标题必填” | AC-004、AC-005 |
| 描述 | 可空；不冒充消息正文 | 原值保留 | AC-003、AC-027 |
| 优先级 | 未选择时 P2 | 应用默认，不依赖数据库默认 | AC-006 |
| 截止时间 | 可空；必须能由统一控件/格式解析 | 保留表单并提示格式 | AC-007 |
| 首次提醒 | 可空；存在时必须晚于 `IClock.Now` | 保留表单并提示未来时间 | AC-008、AC-009 |
| 分组 ID | 可空；非空时必须存在 | 返回 GroupMissing，刷新分组选项 | AC-019、AC-020、AC-031 |
| 分组名称 | Trim 后非空；规范化名唯一 | 提示为空或已存在 | AC-015 至 AC-017、AC-032 |

日期时间继续使用 `DateTimeOffset`，显示和输入按本地时区，持久化使用 `O` 格式。所有“当前时间”判断使用注入的 `IClock`，保证边界测试可重复。

### 6.3 待办生命周期

手动创建的待办从 `Pending` 开始，之后完全复用现有 `Pending/InProgress/Waiting/Done/Ignored` 生命周期。分组不是状态：

- 设置、更换或清除分组不会改变状态、截止时间桶、提醒、完成时间或置顶。
- 完成/忽略保留 `group_id`，但继续执行现有清除置顶和取消开放提醒逻辑。
- 本期只允许活动待办修改分组；历史待办展示但不编辑分组。

## 7. 控制流与数据流

### 7.1 打开创建表单

1. 用户在 `TodoPendingView` 点击“添加待办”。
2. `TodoWorkbenchViewModel.AddTodoCommand` 请求对话框服务打开创建窗口。
3. 创建 ViewModel 加载分组列表；成功时加入一个 UI 专用“未分组”选项，失败时显示读取错误而不是空列表。
4. 标题为空、优先级 P2，其余字段为空；此时没有数据库写入。

### 7.2 在表单中新增分组

1. 用户输入分组名称并点击“新增分组”。
2. ViewModel 调用 `TodoGroupApplicationService.CreateAsync`。
3. 服务规范化名称，仓储在数据库唯一约束保护下插入。
4. 成功：把新分组加入选项并选中；重复/无效：保留名称输入并显示错误。
5. 创建窗口之后取消，只取消待办创建；已提交分组继续存在。

### 7.3 创建手动待办

1. 保存命令进入 busy，阻止重复执行。
2. ViewModel 把控件值转换为 `CreateManualTodoRequest`。
3. `TodoApplicationService` 验证标题和提醒时间、应用 P2 默认值。
4. `SqliteTodoUnitOfWork` 验证 GroupId，并在单事务内插入待办及可选首次提醒。
5. 成功：刷新工作台和分组解析，通知 MainWindow 刷新计数，关闭窗口。
6. 失败：回滚事务，恢复命令，保留表单输入并显示分类后的错误。

### 7.4 修改待办分组

1. 打开活动待办详情时加载全部分组并选中当前 GroupId 或“未分组”。
2. 保存时把 GroupId 与其他字段一起放入 `UpdateTodoRequest`。
3. 工作单元使用 `ExpectedUpdatedAt` 做单行乐观并发更新。
4. 成功刷新工作台；冲突提示用户关闭详情并重新打开；分组已不存在时刷新选项并保留其他输入。

## 8. 错误分类、恢复与用户可见行为

| 分类 | 示例 | 数据结果 | 用户可见行为 |
|---|---|---|---|
| Validation | 空标题、无效日期、过去提醒、空分组名 | 无写入 | 定位字段并保留输入 |
| DuplicateGroup | 同名或仅大小写不同、并发唯一冲突 | 只保留已有分组 | 提示“分组已存在”，可选择已有项 |
| GroupMissing | 表单/详情引用已不存在的 GroupId | 待办不写入/不更新 | 刷新分组列表，保留其他输入 |
| ConcurrencyConflict | 详情 `ExpectedUpdatedAt` 不匹配 | 不覆盖新版本 | 提示重新打开详情 |
| Persistence | SQLite 忙、磁盘或 schema 错误 | 当前事务回滚 | 显示保存失败，可重试 |
| Cancelled | 窗口关闭或取消令牌触发 | 未开始或事务回滚 | 安静关闭或恢复可操作状态 |
| Migration | v3 SQL 或校验和失败 | 迁移事务回滚 | 初始化失败，不进入可写状态 |

UI 不直接显示 SQL、数据库路径或堆栈。诊断可记录错误类型和操作阶段，不记录标题/描述全文。（REQ-007、NFR-006）

## 9. 安全、隐私、性能、兼容性与可观测性

### 9.1 安全与隐私

- 所有 SQL 值参数化，分组名称不得拼接进 SQL。
- 唯一冲突只映射为业务结果，不把原始 SQLite 错误直接显示给用户。
- 日志仅记录操作类型、结果和实体 ID；默认不记录待办标题、描述或分组原文。
- 不新增网络、账号、权限或消息读取边界。

### 9.2 性能

- 分组数量预期远小于待办数量，按名称排序一次读取并构建字典。
- 每次工作台刷新最多增加一次分组查询，不产生按待办 N+1 查询。
- `todo_items.group_id` 索引支持未来关联检查；当前时间桶查询继续使用原有索引和排序。

### 9.3 兼容性

- v3 迁移只新增表、可空列和索引，不修改旧列值。
- 自动待办、消息转待办显式保持 GroupId 为空；按 `SourceMessageId` 的查询与唯一行为不变。
- 完成、批量完成、置顶、提醒弹窗完成和清空已办 SQL 必须继续使用显式列/条件，新增列不会改变其状态转换。
- WPF 样式、命令和对话框沿用现有组件，不引入新 UI 框架或依赖。

### 9.4 可观测性

- 创建窗口显示加载、保存、成功和失败状态。
- 应用服务返回可枚举结果，便于测试和 UI 映射。
- 迁移失败包含版本、名称和校验和上下文，但不把用户内容写入日志。

## 10. 迁移、发布与回滚

### 10.1 迁移

1. `SchemaSql` 继续创建旧基础表，保证既有初始化顺序不变。
2. `schema_migrations` 表建立后依次验证/执行 v1、v2、v3。
3. v3 在同一事务中创建 `todo_groups`、添加 `group_id` 和索引、写入迁移记录。
4. 旧待办不回填，读取时 `GroupId=null` 显示“未分组”。
5. 新数据库同样经过 v1 至 v3，避免新库与升级库 schema 漂移。

### 10.2 发布验证

- 使用旧版 schema fixture 验证升级前后待办逐字段一致。
- 使用新库验证迁移幂等、校验和和外键开启状态。
- 验证所有创建/更新/列表 SQL 都显式处理 `group_id`。
- 构建 WPF 与测试项目并运行仓库控制台回归测试器；实际窗口交互需单独执行手工 GUI 验收。

### 10.3 回滚

v3 是追加式兼容迁移。回滚应用版本时保留 `todo_groups` 和 `group_id`，不执行删除列/表的破坏性降级。旧版本忽略新增对象，用户升级回新版后分组数据仍可读取。

若 v3 未提交则事务整体回滚；若新版功能故障但迁移已成功，可回滚应用二进制并保留数据库。任何未来真正删除分组 schema 的操作都需要独立审批和备份方案。

## 11. 备选方案与权衡

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| 复用项目作为分组 | 无需新表 | 混淆消息分类与 Todo 组织，项目删除/匹配会产生副作用 | 拒绝 |
| 在 `todo_items` 保存分组名称字符串 | 查询简单 | 重复名称、改名困难、并发唯一性弱、无法稳定引用 | 拒绝 |
| 特殊“未分组”实体 | 查询统一 | 需要保留 ID/名称，可能被重复或误操作 | 拒绝，使用 NULL |
| 只在 UI 内保存分组 | 实现快 | 重启丢失、无法满足详情与历史展示 | 拒绝 |
| 分组与待办一次事务创建 | 可一起取消 | 不符合新增分组独立保留要求，事务和交互耦合 | 拒绝 |
| 独立提交分组，待办+提醒原子提交 | 语义清晰，满足取消后保留与提醒一致性 | 分成两个可观察步骤 | 采用 |
| 按分组重排活动待办 | 分组更醒目 | 破坏已确认的截止时间分类和现有操作习惯 | 拒绝 |

## 12. 需求到设计追踪

| 需求 | 主要设计决策与章节 |
|---|---|
| REQ-001 | D-006、4、7.1、7.3 |
| REQ-002 | D-006、5、6.2、7.3、8 |
| REQ-003 | D-005、D-007、5、7.3 |
| REQ-004 | D-001 至 D-004、5、6.1、7.2 |
| REQ-005 | D-001、D-002、D-008、5、6.3、7.4 |
| REQ-006 | D-009、D-010、6.3、9.2 |
| REQ-007 | D-003、D-007、D-011、8、9.4 |
| REQ-008 | D-005、D-006、D-012、9.3、10 |

## 13. 设计完成条件

本设计只有在以下条件全部满足时才可作为实现依据：

- `requirements.md` 中 REQ-001 至 REQ-008 和 AC-001 至 AC-038 均能映射到本设计，没有引入范围外能力。
- 数据库迁移、手动创建、首次提醒、分组并发唯一性和详情乐观并发均有明确事务边界。
- 手动来源、来源消息丢失和真实消息来源三种状态不会混淆。
- 时间分类不读取 GroupId，旧待办升级后保持未分组且其他字段不变。
- 后续 `tasks.md` 和 `check_list.md` 覆盖本设计及所有验收标准，并由用户统一确认四份当前版本后才修改产品代码。
