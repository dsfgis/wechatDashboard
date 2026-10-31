# 待办管理、项目归属、日程概览与桌面日历设计

## 1. 设计目标与非目标

### 1.1 设计目标

- 在现有 Todo 分层中增加不依赖消息的手动创建用例，并复用现有待办生命周期、提醒和刷新机制。（REQ-001、REQ-003）
- 保留原自定义分组数据作为只读兼容来源，移除其生产和展示路径；用户可见归属只使用现有项目设置记录。（REQ-004、REQ-006、REQ-015 至 REQ-017）
- 让手动、消息转入和自动生成的待办共享同一种可空项目关联，并能在活动待办详情中修改。（REQ-005）
- 通过原子事务、数据库约束、忙碌状态和可恢复错误保证创建过程不会产生重复或半成品数据。（REQ-002、REQ-003、REQ-007）
- 使用向后兼容的 SQLite 迁移保留全部旧待办和既有自动/消息待办行为。（REQ-008）
- 用固定可读色板为新项目增加颜色、允许既有项目在项目列表中改色，并让迁移前项目安全获得默认灰色。（REQ-009、REQ-016）
- 以注入时钟和用户时区统一计算自动待办的“当天最后一刻”，并保持其他创建路径不变。（REQ-010）
- 用稳定 Todo ID 保持刷新前后的有效勾选，并让桌面日历复用同一仓储与详情流程。（REQ-011、REQ-012）
- 将日历几何/显示偏好与 Todo 数据分离，在当前显示器工作区内实现可恢复的停靠和缩进状态机。（REQ-012）
- 用日期选择器和有限时/分集合维护可空 `DueAt`，消除自由文本格式依赖。（REQ-013）
- 抽取可复用 `DesktopCalendarView`，让主窗口日程概览与桌面窗口共享同一 ViewModel 和详情回调。（REQ-014）
- 让现有项目关键字、自动待办归属和项目看板共享一次项目快照及同一匹配器，消除页面间归属漂移。（REQ-018）
- 保持现有项目设置和项目关键字行为，包括新项目自动创建项目名关键字、每个项目至少一个关键字；只增加颜色创建/修改和待办引用保护。（REQ-015、REQ-016、REQ-018）

### 1.2 非目标

- 不改变现有项目新增/删除和关键字新增/删除语义；不新增项目重命名、排序、折叠或批量移动。
- 不用分组替代“已置顶、已逾期、今日到期、后续到期、无截止时间”。
- 不把旧 `projects.id`、`followed_projects.id`、`todo_groups.id` 的数值相等视为同一实体；迁移必须按规范化名称显式映射。
- 不增加循环待办、子任务、附件、标签或云同步。
- 不改变自动建待办和消息转待办的触发条件或基于来源消息的幂等语义。
- 不在日历中直接创建、拖拽改期、批量编辑或复制待办。
- 不实现任意颜色选择器、用户自定义色值、日历云同步、开机自启或 Windows 小组件平台集成；既有项目改色只允许固定七色。
- 不保留第二套独立“待办分组设置”，也不在待办创建/详情中现场创建项目。

## 2. 当前系统上下文与架构基线

### 2.1 当前上下文

当前 Todo 功能已按以下边界组织：

- `TodoItem` 表达待办领域数据，`SourceMessageId` 已允许为空。
- `TodoApplicationService` 校验手动创建、从消息创建和更新用例。
- `ITodoUnitOfWork` / `SqliteTodoUnitOfWork` 负责待办、提醒及状态变更的事务边界。
- `ITodoRepository` / `SqliteTodoRepository` 负责列表、详情、完成和置顶持久化。
- `TodoWorkbenchViewModel` 负责活动/已办列表、截止时间分类和批量操作。
- `TodoDetailViewModel` 负责标题、描述、优先级、状态、截止时间和提醒编辑。
- `TodoFeatureCoordinator` 组装仓储、服务、工作台、消息流、提醒后台工作器与提醒通知队列。
- `SqliteDatabaseInitializer` 以带版本、名称和校验和的事务迁移演进 schema；当前迁移版本为 4。
- `TodoGroupColorPolicy` 固定可读色板并规范化持久化色值。
- `TodoDueBucketPolicy` 同时负责互斥时间桶和用户时区当天最后一刻计算；时间桶按本地自然日比较，今天任意时刻到期均属于“今日到期”。
- `DesktopCalendarViewModel` 从同一 Todo 仓储聚合活动与已办理记录；`DesktopCalendarView` 承载共享月历 UI；`MainWindow` 内嵌日程概览，`DesktopCalendarWindow` 负责独立窗口、停靠、缩进和状态保存。

本节原始缺口已经实现。2026-08-30 增量设计用于固化 v4 分组颜色、自动到期、刷新选择恢复和桌面日历边界；后续实现不得绕过这些契约另建平行数据模型。

### 2.2 当前 v3/v4 架构（迁移输入，不是目标交互）

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

TodoService ---- TodoDueBucketPolicy.EndOfToday(IClock, TimeZoneInfo)

MainWindow ---- ScheduleOverview/Navigation
    |                 |
    |                 v
    +---- DesktopCalendarWindow ---- DesktopCalendarView ---- DesktopCalendarViewModel
                    |                                      |
                    | state                                +---- ITodoRepository
                    v                                      +---- existing Todo detail callback
       %LocalAppData%\WechatDashboard\desktop-calendar.json
```

上图用于解释现有分组代码和数据来源。目标架构见 D-022；实现后，WPF 仍只维护表单状态和用户交互，输入校验与用例结果在 Application 层表达，唯一性、外键和原子性由 Infrastructure/SQLite 最终保证。（REQ-002、REQ-004、REQ-007）

## 3. 核心设计决策

> D-001 至 D-013 记录 v3/v4 已落地的历史分组设计，便于理解现有数据库和回滚边界；它们不是本次目标交互。凡涉及新写入、设置入口和用户展示，均由 D-022 至 D-028 取代。

### D-001：原始分组是独立聚合（历史设计，由 D-022 至 D-028 增量取代）

新增 `TodoGroup`，只包含分组标识、显示名称、规范化名称和创建时间。`TodoItem` 新增可空 `GroupId`。分组与 `projects`、`followed_projects` 没有外键或同步关系。（REQ-004、REQ-005、AC-019 至 AC-025）

原因：这是 v3/v4 已落地模型的历史依据。2026-09-01 修订不直接复用旧项目数值 ID，而是引入指向现有项目设置记录的明确关联和保守迁移，避免删除、匹配和 ID 混淆风险。

### D-002：“未分组”使用空外键，不创建特殊记录

`TodoItem.GroupId=null` 是唯一的未分组状态，界面把它格式化为“未分组”。数据库中不创建保留 ID 或名为“未分组”的系统分组。（REQ-004、REQ-005、AC-020、AC-021）

这避免特殊记录被重命名、重复或误选，也使旧待办升级后自然处于未分组状态。

### D-003：分组名称以显示值和规范化值双字段保存

显示名称使用 `Name.Trim()` 后的文本；唯一键使用应用层统一生成的 `NormalizedName`：先做 Unicode Form C 规范化，再调用 `ToUpperInvariant()`。数据库对 `normalized_name` 建立唯一索引。（REQ-004、REQ-007、AC-015 至 AC-018、AC-032）

应用层先查询并返回友好错误；数据库唯一约束处理两个并发请求都通过预检查的竞争条件。不得仅依赖 SQLite `NOCASE`，因为它的默认大小写折叠不能完整覆盖非 ASCII 名称。

### D-004：管理组织项和创建待办是两个独立提交边界

组织项在独立管理设置中调用应用服务并立即提交。待办窗口只重新加载并选择已存在项；随后取消创建待办不会删除已经提交的组织项。v3/v4 的创建窗口内新增方式由 D-026 在 v5 UI 中移除。（REQ-004、REQ-015、AC-015、AC-018、AC-068）

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
4. 一条待办可被多个群的多条消息共同触发（跨群去重合并，见迁移 v6 的 `todo_source_messages`）：列表行显示“N 条来源”角标，详情页原始消息框按“[序号] 群聊 / 发送人 / 时间 + 正文”逐条展示，主来源消息始终排在首位。

### D-011：用变更事件刷新 MainWindow 聚合计数

`TodoFeatureCoordinator` 在手动创建成功后先刷新 `Workbench`，再发出无负载的 `TodoChanged` 事件或调用等价的注入回调。`MainWindow` 复用现有聚合刷新路径更新待办理数和高优先级数。（REQ-007、AC-033）

事件不传递表单内容，避免 MainWindow 重新承载 Todo 业务。消息转待办、完成等已有路径可逐步复用同一通知，但本功能只要求手动创建路径接入。

### D-012：采用追加式迁移，旧版本可忽略新列

新增 schema migration v3：先创建 `todo_groups`，再向 `todo_items` 添加可空 `group_id` 外键并创建索引。迁移及 `schema_migrations` 记录在同一事务中提交。（REQ-008、AC-035、AC-038）

迁移不回填、不重写旧待办。旧应用使用显式列清单且可忽略新增表/列，因此应用版本回滚不需要破坏性降级迁移；新版再次启动仍通过迁移校验和验证 schema。

### D-013：分组颜色使用固定策略和追加式 v4 迁移

`TodoGroup` 增加 `Color`。`TodoGroupColorPolicy` 只接受灰、红、橙、绿、蓝、紫、粉七个常量，空值规范化为灰色，其他色值在 Application 层拒绝。`TodoGroupApplicationService.CreateAsync` 在持久化前完成颜色规范化；重复名称返回已有分组，不改写已有颜色。（REQ-009、AC-039 至 AC-042）

schema migration v4 仅向 `todo_groups` 追加 `color TEXT NOT NULL DEFAULT '#4B5563'`。SQLite 为既有行填入默认值，迁移记录与 D-012 使用同一校验和/事务机制。（AC-043）

### D-014：自动待办到期时间由统一时区策略计算

`TodoService.CreateFromMention` 接受可测试的 `now` 与 `TimeZoneInfo` 重载，通过 `TodoDueBucketPolicy.EndOfToday(message.SentAt)` 计算消息发送日（微信/石化通消息的 `SentAt`）本地日期下一天零点前一 tick，而非采集时刻——补采历史消息时待办落在发送当天。`now` 仅用于 `CreatedAt`/`UpdatedAt`。默认重载使用 `DateTimeOffset.Now` 和 `TimeZoneInfo.Local`。只有自动 `@我` 路径传入该 `DueAt`；手动待办和用户主动消息转待办不复用此默认值。（REQ-010、AC-044 至 AC-046）

### D-015：刷新选择以活动 Todo ID 为唯一恢复键

`TodoWorkbenchViewModel.RefreshAsync` 在替换集合前快照当前已选活动 ID，读取最新活动/已办理集合并重建行，只对仍存在于活动 ID 集合中的相同 ID 恢复 `IsSelected=true`。随后统一重算 `SelectedCount`、全选文案和批量命令可用状态。（REQ-011、AC-047 至 AC-049）

选择状态是进程内 UI 状态，不写 SQLite。批量完成再次从当前活动行提取唯一 ID，仓储仍以活动状态条件更新，防止过期选择误操作。

### D-016：桌面日历是现有 Todo 仓储的只读投影视图

`DesktopCalendarViewModel.RefreshAsync` 各读取一次活动与已办理集合，合并后过滤 `DueAt != null`，按用户时区本地日期分组。组内排序契约为固定四级键：第一优先级 `Priority`（P0 最高）；第二是否在建项目（`ManagedProjectId` 指向的用户项目仍处于启用状态者优先）；第三消息提醒时间（优先取仍待触发即 `Scheduled`/`Dispatching` 的最早提醒，按时间升序；无活动提醒者排后）；第四消息发送时间（来源消息 `SentAt` 升序，无来源消息的手动待办排后）。四级键相同时再以 `DueAt`、`Id` 保证稳定排序。完成状态不参与排序，因此勾选完成前后待办位置不变，待办与已办只通过复选框勾选状态和删除线区分；已办不会因完成被降级到末尾而挤出日期格子的两行预览。完成会取消该待办的开放提醒，此时第三级键回退到最后一次提醒时间，避免已办待办位置跳动。日期格子两行预览（`PreviewTodos`）与右侧“当日日程”共用同一排序结果；无截止时间只进入状态计数，不进入日期格。月历固定生成从星期一开始的 42 个日期单元。（REQ-012、AC-051 至 AC-053、NFR-009）

日历行保存 Todo ID、标题、优先级、本地截止时间、展示时间 `DisplayAt`（有来源消息时取消息发送时间，否则回退截止时间）、是否已办理、项目名/颜色和优先级色。提醒与来源消息发送时间按待办 ID 批量读取一次，不逐条查询。点击标题通过注入回调复用 `TodoWorkbenchViewModel.OpenTodoAsync(long)` 的详情边界，详情返回后重新刷新，不复制或直接更新 Todo。（AC-054）

### D-017：主窗口拥有唯一日历窗口生命周期

`MainWindow` 延迟创建并持有单个 `DesktopCalendarWindow`。左侧按钮调用 `ShowWidgetAsync`：隐藏时显示、缩进时展开、随后激活并刷新。普通关闭和“隐藏”只隐藏；主程序清理时调用 `Shutdown`，停止两个计时器、保存状态并允许真正关闭。可见期间每分钟刷新一次。（REQ-012、AC-050、AC-057）

日历使用 `ShowInTaskbar=false`，避免成为独立应用入口。打开详情仍由已有对话框服务负责，日历不成为第二个待办工作台。

### D-018：日历偏好是无业务内容的可恢复本地状态

`DesktopCalendarStateStore` 只保存 `Left`、`Top`、`Width`、`Height`、`Opacity`、`IsEdgeSnapEnabled` 和 `DockEdge` 到 `%LocalAppData%\WechatDashboard\desktop-calendar.json`。保存先写 `.tmp` 再以替换方式移动到正式路径；读取失败回退默认值。宽高、透明度和非有限坐标在加载时规范化，文件不含 Todo 标题、描述、消息或联系人数据。（REQ-012、AC-055、NFR-008）

### D-019：停靠和缩进使用显式几何状态机

`DesktopCalendarDockLayout` 以当前显示器工作区、窗口尺寸、当前位置和 `DesktopCalendarDockEdge` 计算完整停靠位置与缩进位置。自动吸附阈值为 32 DIP；四边共用 2 秒离开计时器，并保留 18 DIP 厚、104 DIP 长的可点击把手。缩进只改变位置和可见表面，不改窗口保存尺寸；展开后恢复完整表面和可调整尺寸状态。（REQ-012、AC-056、NFR-007）

工作区由窗口句柄对应的显示器获得，并通过 WPF 设备到 DIP 变换处理 DPI。位置变化若不再贴边，则清除停靠状态，避免错误自动缩进。

### D-020：截止日期使用结构化选择状态构造本地时间

`TodoCreateViewModel` 与 `TodoDetailViewModel` 分别维护可空 `DueDate`、`DueHour` 和 `DueMinute`。小时与分钟来自有限整数集合；日期为空时输出 `DueAt=null`，日期非空时以 `DateTimeKind.Unspecified` 组合本地时间、拒绝时区无效时间，再使用 `TimeZoneInfo.Local` 偏移构造 `DateTimeOffset`。新表单默认时分为 `23:59`，详情则从持久化值反向填充。（REQ-013、AC-058 至 AC-060）

### D-021：内嵌与桌面日历复用视图和 ViewModel

月历 XAML 抽取到 `DesktopCalendarView`，所有日期、待办和命令绑定相对于 `UserControl` 数据上下文解析。`MainWindow.ScheduleOverview` 是唯一共享 `DesktopCalendarViewModel`：左侧 Tag=8 导航选中后立即刷新，内嵌页直接绑定；桌面窗口只增加标题栏、停靠表面和状态边界，并接收同一实例。这样两个入口共享月份、所选日期、仓储读取和详情回调。（REQ-014、AC-061 至 AC-064）

### D-022：`followed_projects` 继续作为项目设置事实源

不合并、重命名或重做现有项目设置与项目关键字功能。`followed_projects` 继续提供稳定项目 ID、名称和活动状态，`followed_project_keywords` 继续保存每个项目的一个或多个关键字；只向项目记录追加固定色板 `color`。`todo_groups` 不再是可写或用户可见事实源，仅保留为历史兼容数据。（REQ-015、REQ-016、REQ-019，AC-065 至 AC-072、AC-083）

目标结构：

```text
关注设置
  +---- 项目设置（新增时选择颜色；列表颜色列可改；原新增/删除行为保持）
  +---- 项目关键字（原初始关键字、多关键字、至少一个关键字保持）
                    |
                    v
followed_projects (project_name, color, is_active)
          |                         ^
          +---- todo_items.managed_project_id
          +---- 关键字匹配 ---------+---- followed_project_keywords
          +---- 待办理 / 详情 / 已办理 / 日历

兼容只读来源：todo_groups、todo_items.group_id、todo_items.project_id、projects
```

### D-023：待办项目关联与旧内部分类 ID 明确分离

`TodoItem` 新增可空 `ManagedProjectId`（最终命名可按代码规范调整），外键指向 `followed_projects.id`。现有 `ProjectId` 继续表示历史 `projects`/`message_classifications` 分类结果，`GroupId` 仅作为 v5 迁移来源。领域构造、请求和 SQL 必须使用不同字段名，禁止把三个 ID 通过数值相等直接互换。（REQ-016、REQ-017，AC-069 至 AC-078）

UI 的“未指定项目”对应 `ManagedProjectId=null`。时间桶、提醒和状态生命周期不读取该字段；列表、详情、已办理和日历通过一次项目快照解析名称和颜色。（AC-073 至 AC-076）

### D-024：v5 迁移只追加项目颜色和待办项目关联

v5 在现有迁移事务和校验和机制内执行，不改写项目名称、项目 ID、活动状态或关键字：（REQ-019、NFR-011）

1. 向 `followed_projects` 追加非空 `color`，既有项目统一回填默认灰色。
2. 向 `todo_items` 追加可空 `managed_project_id` 及查询索引，外键指向 `followed_projects.id`。
3. 对旧 `todo_items.group_id`：只有当对应 `todo_groups.name` 与一个现有项目名称经 Trim、Unicode Form C、忽略大小写后相同时，才写入该项目 ID；不同名旧分组不创建项目、不创建关键字，`managed_project_id` 保持空。
4. 对没有旧分组但有历史 `project_id` 的待办，仅当 `projects.name` 与现有项目按同一名称规则唯一匹配时才回填；禁止比较三张表的数值 ID。
5. 保留 `todo_groups`、`group_id` 和旧 `project_id` 作为只读审计/回滚数据，记录同名映射数、未映射分组待办数和名称冲突数；新代码不再写这些分组字段。
6. 最后写入迁移版本、名称和校验和。

迁移重复执行不得新增项目、关键字或重复关联。任何一步失败都回滚迁移事务并阻止以不完整 schema 进入可写状态。（AC-071、AC-083 至 AC-085）

### D-025：保持项目关键字契约并统一匹配快照

现有项目新增后以项目名作为初始关键字、允许继续增加多个关键字、至少保留一个关键字的行为必须保持。匹配器接收一次活动项目和关键字快照，对群名与消息正文执行当前语义：最长关键字优先，其次按项目名称不区分大小写排序，返回唯一项目。自动 Todo 和项目看板必须复用这一规则。（REQ-018、NFR-010、NFR-012）

手动待办的显式项目选择优先于任何推测。自动待办只用关键字匹配结果；未命中保持 `ManagedProjectId=null`。项目/关键字设置改变后影响后续匹配，不后台改写既有待办。（AC-077 至 AC-082）

### D-026：设置页保留现有两块项目功能，只移除分组

“关注设置”继续保留现有“项目设置”和“项目关键字”区域，不合并两个区域。项目设置的新建输入旁增加固定色板选择；项目列表的颜色列使用绑定固定七色的 `ComboBox`，选择变化调用独立颜色更新命令并立即持久化。成功后刷新项目快照、待办工作台、已办理和日历；失败时恢复该行修改前颜色并显示明确错误。颜色修改不得更新项目名、关键字或待办关联。项目关键字区域及其初始关键字、增删和至少一个关键字限制保持。移除独立“待办分组设置”。手动待办创建和详情只加载活动项目，加一个 UI 专用“未指定项目”，不提供现场新增项目或分组。（REQ-015、REQ-016、REQ-017，AC-065 至 AC-076、AC-086）

### D-027：手动选择与自动匹配汇入同一待办项目字段

手动创建/详情保存时验证所选项目存在且可用，再在现有待办事务中写入 `managed_project_id`；更换或清空只更新同一待办，不改状态、时间桶或提醒。自动创建路径在创建前使用 D-025 的关键字结果写入相同字段；消息转待办若当前路径没有匹配结果则保持“未指定项目”。“待办理”原分组名称/颜色展示绑定改为项目名称/颜色，项目名称使用独立 `TextBlock` 直接绑定 `ProjectColor`，避免内联文字继承父级前景色；详情、已办理和日历使用同一解析规则。工作台先把置顶项放入独立集合，再把其余记录按本地截止日期放入“已逾期、今日到期、后续到期、无截止时间”，顶部摘要直接读取这五个互斥集合的数量，不另行统计重叠维度。（REQ-005、REQ-006、REQ-017、REQ-018，AC-021 至 AC-030、AC-073 至 AC-082）

### D-028：保持项目删除语义、兼容只读和失败恢复

项目删除继续沿用现有物理删除入口和关键字清理流程。`managed_project_id` 外键使用 `ON DELETE SET NULL`：删除项目只清空相关待办的用户可见项目关联，不删除待办、不阻塞删除，也不留下悬空 ID。v5 成功后新代码只写 `managed_project_id` 和现有项目表，旧 `group_id`/`todo_groups` 只读，避免双写漂移。（REQ-016、REQ-019，AC-068、AC-072、AC-076、AC-083 至 AC-085）

迁移和项目/待办写入使用现有事务边界；初始化失败不进入可写状态。若旧二进制无法理解新事实源，发布回滚必须恢复迁移前数据库副本，或只读诊断当前数据库，不能让两个版本交替写入后再合并。

### D-031：提醒以右下角队列非模态展示，投递与用户处理解耦

`ReminderWorker` 原本逐条 `await` 发布，而 `IUserNotificationPublisher` 的实现用 `ShowDialog` 阻塞到用户点按钮，导致一轮内多条到期提醒串行卡住后续轮询。改为：`InAppReminderNotificationPublisher` 只把 `ReminderDispatchItem` 投入 `ReminderNotificationQueue` 并立即返回，`ReminderWorker` 随即 `MarkDeliveredAsync` 并处理同轮下一条；用户选择由 `ReminderResolutionHandler` 落地为 `MarkSelectedCompletedAsync`、`SnoozeAsync` 或仅刷新工作台。

队列在屏幕工作区右下角按“最新在下、旧卡上移”叠放，最多同时显示 3 张，其余按到期顺序排队并在处理完一张后补位；可见卡片显示“+N 条待查看”角标。卡片为非模态 `Show`、`Topmost`、`ShowActivated=False`、不显示在任务栏，标题栏关闭等价于“知道了”。单卡高度上限取 `(工作区高 − 2×24 − 2×12) / 3`，保证三张叠放不越出工作区，超出内容在卡片内滚动。由于 `SizeToContent="Height"` 的窗口在 `Loaded` 时高度尚未定型，队列在 `SizeChanged`/`ContentRendered` 重排。`TodoFeatureCoordinator.StopAsync` 释放队列，关闭卡片但不把它们记为用户处理。

代价：入队即视为已投递，重启后仍在内存队列中的提醒不会重发；若要保留，需要持久化“已投递未处理”状态，超出本决策范围。（REQ-010、REQ-013）

### D-030：桌面日历滚动条由日历主题驱动，不使用系统默认样式

`DesktopCalendarView` 的当日日程列表启用溢出滚动时，系统默认 `ScrollBar` 会固定绘制浅色轨道与箭头按钮，在暗色主题下形成一条与面板割裂的亮带。因此 `CalendarControls.xaml` 提供隐式 `ScrollBar` 样式：轨道透明、滑块为 6 DIP 圆角胶囊、无增减箭头按钮，垂直方向宽 8 DIP、水平方向高 8 DIP；滑块颜色由 `DesktopCalendarTheme` 的 `CalendarScrollThumb`/`CalendarScrollThumbHover`/`CalendarScrollThumbPressed` 三组明暗色值驱动，随主题切换即时更新。该样式只作用于合并 `CalendarControls.xaml` 的日历视图（桌面日历窗口与主窗口“日程概览”），不改变主窗口其他页面的滚动条外观。（REQ-012、REQ-014）

### D-029：待办说明沿用现有 `description` 字段和同一条保存路径

详情窗口新增的“待办说明”不引入新列，直接编辑既有 `todo_items.description`。`TodoDetailViewModel` 新增 `SaveDescriptionAsync`，与“保存待办”共用同一次 `UpdateTodoRequest` 提交和 `ExpectedUpdatedAt` 乐观并发检查，只是反馈文案不同；空白说明在 ViewModel 内规范化为 `null` 后再落库，保存成功后回写界面。（REQ-007、REQ-013）

### D-032：详情与提醒卡片紧凑化，状态只经显式操作流转

详情窗口不再提供“状态”下拉：状态只能由“完成”等显式操作改变，避免绕过生命周期直接改写 `todo_items.status`；`TodoDetailViewModel.Status` 仍随保存请求原样回写，并继续驱动完成/置顶可用性。详情同时移除“查看原始消息”按钮，原始消息仍在只读卡片中展示并支持复制，`NavigateToSourceCommand` 保留但不再有界面入口。

界面密度统一收紧：详情默认 560×600（最小 480×420），基础字号 12，优先级与项目同行；提醒卡片宽 380、MaxHeight 360，`ReminderNotificationQueue.MinCardHeight` 降为 170。提醒队列的叠放、补位、角标和处理语义不变。（REQ-007、REQ-010、REQ-013）

### D-033：关注设置只保留一层滚动，项目与关键字合并为可折叠树

关注设置原先有四个固定高度 `DataGrid`（别名 240、关注群 180、项目 130、关键字 180），各自带内部滚动条，与外层 `ScrollViewer` 形成多层嵌套。改为：

- 别名与关注群 `DataGrid` 去掉固定高度，交由外层滚动条统一滚动；两者仍以选中行 + “删除选中”操作，行为不变。
- 项目与关键字不再用两张平铺表（关键字表重复“项目”列）。`FollowedProjectTreeRow` 把项目与其关键字投影为一个节点，界面用自绘标题的可折叠 `Expander` 渲染：标题为颜色圆点、项目名、关键字数量、颜色下拉和删除项目；展开后缩进显示每条关键字（左侧引导线），逐条删除；“添加关键字”输入框位于节点内部，按钮通过 `Tag` 指向同节点输入框，`DataContext` 定位项目。
- 删除项目/关键字从“表格选中项”改为按 `sender.DataContext` 定位，移除 `ProjectKeywordProjectComboBox` 这一独立添加入口；每个项目至少保留一个关键字的约束保持。
- 展开状态不跨刷新保存，重新加载后默认全部展开，避免删除或改色后节点折叠导致用户丢失上下文。（REQ-015、REQ-016、REQ-018）

### D-034：滚动条外观统一为全局共享资源，不再各窗口各写一份

上一版本只在 `TodoDetailWindow` 内声明隐式 `ScrollBar` 样式，其余页面仍是系统 Aero 滚动条：消息流、采集源设置的 `DataGrid` 带 17 DIP 宽的浅色轨道与增减箭头，关注设置的外层 `ScrollViewer` 同样如此，与卡片的白底圆角风格割裂。

改为 `Styles/ScrollbarStyles.xaml` 单一资源字典，在 `App.xaml` 的 `MergedDictionaries` 中合并，隐式样式作用于全应用；`TodoDetailWindow` 内的重复声明删除。样式规范：

- 滚动条宽/高 12 DIP（滑块可见宽度 6 DIP），轨道透明，无增减箭头按钮；`RepeatButton` 保留透明模板以维持“点击轨道翻页”。
- 滑块为 `CornerRadius=3` 的圆角胶囊，常态 `#C7D2DE`、悬停 `#94A3B8`、拖动 `#64748B`、禁用 `#E2E8F0`；纵向 `MinHeight=24`、横向 `MinWidth=24`，避免滑块退化成小点。
- 鼠标进入滚动条时轨道位置显示 `#EFF3F7` 圆角槽，离开即恢复透明，不做常驻轨道底色。
- 垂直/水平分别使用独立模板并按 `Orientation` 触发切换；水平分支必须把 `Width` 改为 `Auto`、`MinWidth` 归零再设 `Height`，否则垂直滚动条会被压成正方形而不渲染滑块。

`CalendarControls.xaml` 中由 `DesktopCalendarTheme` 驱动的日历滚动条保持不变：日历有自己的明暗主题，窗口级隐式样式优先于应用级，二者互不覆盖。（REQ-012、REQ-014、REQ-015）

### D-035：置顶待办同时保留在截止时间分组，不再从“已逾期”等组消失

上一版本把置顶当作排他分组：`RefreshAsync` 用 `!row.IsPinned` 把置顶待办从已逾期/今日到期/后续到期/无截止时间四组里全部剔除。后果是置顶一条已逾期待办后，它只出现在页面顶部的“已置顶”组，用户按“已逾期”去找会认为待办丢失或被误判——这是可预期的用户困惑，因为“逾期”本身与“是否置顶”无关。

改为：置顶是**第二个展示维度**（与优先级维度同构），置顶待办同时出现在“已置顶”组与其所属截止时间组。

- 截止时间四组仍互斥且覆盖全部活动待办，组内 `OrderByDescending(IsPinned)` 让置顶项排最前，卡片继续显示 `[置顶]` 标记。
- 两处引用同 `TodoListItemViewModel` 实例，勾选框、`IsSelected` 与批量完成天然同步；`ActiveRows()` 追加 `Distinct()`，`SelectedCount`/全选/批量完成不会重复计数。
- `ActiveCount` 改为 `RefreshAsync` 中按 `activeRows.Length` 记录的去重值，不再对集合长度求和；顶部摘要文案改为“活动待办 N 条（去重）：已置顶 X 条（同时计入所属截止时间分组）…”，明确置顶计数与四组计数重叠。
- 取舍：分组计数之和不再等于活动总数。这是“置顶可见性”优先于“计数可累加”的代价；已通过文案标注和去重总数降低误解，优先级分组本就是重叠维度，用户已习惯该呈现方式。（REQ-006；AC-028、AC-029）

### D-036：正文口语钟点进入截止时间，日终兜底不再生成临近午夜的提醒

问题案例：15:13 收到“请问5点半左右方便么”，自动待办截止时间回退到当天 23:59:59.999，提醒按“截止前一小时”算成 22:59——已经远晚于需要回复的 17:30，提醒失去意义。

`TodoService` 的本地时间规则此前只识别两类时间：`会议时间：yyyy/M/d HH:mm` 卡片和“本周三”这类本周星期，其余一律回退日终。改动两点：

- **截止时间**：新增 `ExplicitClockTimeRegex` 识别正文口语钟点——`5点半`、`17:30`、`下午3点`、`16点左右`、`明天上午10点`；时段词（上午/下午/中午/晚上/凌晨）折算 24 小时制，`明天/后天` 偏移日期，与“本周三”同时出现时组合成该星期几的该时刻。无时段无日期的 `5点半` 取“不早于消息发送时刻”的解释（15:13 的“5点半”判为 17:30）。`第3点要求` 这类条目编号用负向后行断言排除；正文已含完整年月日时跳过该分支，仍交给会议/星期规则。
- **提醒时间**：优先级改为 Agent 明确时间 → 会议卡片提前 5 分钟（`MeetingReminderLead`）→ 正文钟点提前 15 分钟（`ClockTimeReminderLead`）→ 用户显式截止提前 1 小时（`TaskEffortLead`）→ 日终兜底：截止日早上 9 点（`TaskMorningReminderOffset`），若已过则收到后 1 小时（`FallbackReminderLead`）。用 `IsDayEndSentinel`（本地 23:59 之后）区分“日终兜底”与用户真实选择的截止时刻，手动创建的待办仍按截止前一小时。所有分支都要求提醒晚于当前时间且早于截止时间，否则不建提醒。

代价与边界：口语钟点解析可能误判（例如正文同时出现多个时间时只取第一个匹配），但比“日终 + 深夜提醒”更接近真实意图；模型识别出的 `dueAt`/`reminderAt` 仍优先于本地规则。（REQ-004、REQ-006；AC-003、AC-008、AC-009）

### D-037：确认时间（那就5点半/改到16点）允许把既有待办的时间改早

跨群去重合并长期遵守“只在更晚时改期”：先到消息给出 20:00，后续消息给出 18:00，提醒保持 20:00。这条规则假设后续消息只是**又一次询问**，不能倒推削减已承诺的时间。但群里常见的后续形态恰恰相反——“好吧，那就5点半”“改到16点吧”“就定明天上午10点”——这些是**大家最终认同的事件时间**，沿用“只取更晚”会让待办停留在已被否定的旧时间上：用户看到提醒写着 20:00，而实际约定是 17:30。

因此把“提问”与“确认”区分开：

- `TodoService.ConfirmedClockTimeRegex` 只匹配带确认动词或确认语气的时间表达（`那就/就/定在/定于/定了/改为/改到/改成/调整到/挪到/推迟到/提前到/确认/确定/敲定/好吧`），复用 D-036 的 `ExplicitClockTimeRegex` 时段与日期折算，入口为 `TryResolveConfirmedClockTime`；`请问5点半方便么` 这类询问不匹配，仍走“只取更晚”。
- 合并链路新增 `TimeConfirmed` 标记，从 `MessageCapturePipeline` 与 `MessageFeedViewModel` 两条入口传入，经 `MergeTodoRequest` → `SqliteTodoMerge.ApplyAsync`。`RescheduleAsync`/`ApplyDueAtAsync` 在标记为 true 时不再比较“是否更晚”，直接以确认时间覆盖截止与提醒；两条路径仍要求确认时间晚于当前时间，来不及提醒时保持原提醒，不让待办失去提醒。
- 确认时间的提醒提前量为 `ClockTimeReminderLead`（15 分钟），与新建自动待办一致；模型给出的 `DueAt`/`FirstReminderAt` 优先级不变，只有模型没给时间时才用本地确认时间补位。
- 合并仍然只把消息追加为来源、不新建待办，`ReminderRescheduled=true` 的文案改为“按确认的时间更新提醒”，与普通“按更晚时间更新提醒”区分。

边界与代价：确认动词清单是白名单，口语里未覆盖的改期表达（例如“换个时间，5点吧”）暂不识别，会退回“只取更晚”——宁可保守，也不能把一条询问误当成承诺而把提醒提前。（REQ-004、REQ-006；AC-008、AC-009、AC-044）

## 4. 组件职责

| 组件 | 层 | 新增或调整职责 | 追溯 |
|---|---|---|---|
| `TodoGroup` / `ITodoGroupRepository` | 历史兼容 | v3/v4 旧分组数据只读保留，不再进入创建、详情或设置生产路径 | REQ-015、REQ-019 |
| `TodoItem` | Domain | 增加可空 `ManagedProjectId`，保留 `ProjectId`/`GroupId` 作为不同含义的兼容字段 | REQ-005、REQ-008、REQ-017、REQ-019 |
| `TodoApplicationService` / `TodoService` | Application | 手动创建校验与默认值；自动识别待办当天到期 | REQ-001 至 REQ-003、REQ-010 |
| `TodoDueBucketPolicy` | Application | 互斥时间桶和用户时区当天最后一刻 | REQ-006、REQ-010 |
| `ITodoUnitOfWork` | Application | 新增手动待办与首次提醒原子创建 | REQ-003 |
| `SqliteTodoUnitOfWork` | Infrastructure | 插入手动待办/提醒、验证并更新 `ManagedProjectId` | REQ-003、REQ-005、REQ-017 |
| `SqliteTodoRepository` | Infrastructure | 持久化/读取 `managed_project_id`，保留完成路径和兼容字段 | REQ-005、REQ-008、REQ-019 |
| `SqliteDatabaseInitializer` | Infrastructure | 保留 v3/v4 历史迁移并追加 v5 项目颜色/待办项目迁移 | REQ-008、REQ-009、REQ-019 |
| `TodoCreateViewModel` | App | 表单状态、结构化截止日期/时分、命令、错误展示和成功关闭 | REQ-001、REQ-002、REQ-007、REQ-013 |
| `TodoCreateWindow` | App | 模态创建界面、日期/时分选择和可访问性标签 | REQ-001、REQ-004、REQ-013 |
| `TodoDetailViewModel/Window` | App | 活动待办项目及日期/时分编辑，历史待办项目只读显示；原始消息上方提供可编辑保存的待办说明 | REQ-005、REQ-013、REQ-017 |
| `TodoWorkbenchViewModel` | App | 添加命令、批量解析项目名/颜色、列表刷新和选择恢复 | REQ-001、REQ-006、REQ-011 |
| `TodoFeatureCoordinator` | App | 组装服务、提醒队列并通知外层聚合刷新 | REQ-007、REQ-010 |
| `ReminderNotificationQueue` | App | 右下角叠放、FIFO 排队、去重、排队角标与退出清理 | REQ-010 |
| `ReminderResolutionHandler` | App | 把完成/延后/知悉落地为用例调用并刷新工作台 | REQ-010、REQ-013 |
| `DesktopCalendarViewModel` | App | 月视图、日期分组、排序、详情跳转和刷新 | REQ-012 |
| `DesktopCalendarView` | App | 内嵌页与桌面窗口共享的月历视觉和命令绑定；当日日程列表使用主题化滚动条 | REQ-014 |
| `CalendarControls.xaml` / `DesktopCalendarTheme` | App | 日历共用样式与明暗色值，含 `CalendarScrollThumb` 系列滚动条颜色 | REQ-012、REQ-014 |
| `App.xaml` / `Styles/ScrollbarStyles.xaml` | App | 应用级隐式 `ScrollBar` 样式（细轨道、圆角滑块、无箭头），统一消息流、关注设置、采集源设置等页面滚动条 | REQ-014、REQ-015 |
| `DesktopCalendarWindow` | App | 唯一桌面窗口、停靠/缩进状态机和计时器生命周期 | REQ-012 |
| `DesktopCalendarStateStore` / `DesktopCalendarDockLayout` | App | 临时文件替换保存偏好和纯几何计算 | REQ-012 |
| `FollowedProject` | Domain | 在现有稳定 ID、名称和活动状态上增加固定色板颜色 | REQ-009、REQ-015、REQ-016 |
| 现有项目仓储/服务 | Application/Infrastructure | 保持新增/删除与关键字行为，增加颜色创建和按 ID 更新；项目删除由待办外键置空 | REQ-015、REQ-016、REQ-019 |
| 项目关键字匹配器 | Application | 对一次现有项目/关键字快照执行确定性匹配 | REQ-018 |
| `TodoItem.ManagedProjectId` | Domain/Application | 用户可见项目关联；与旧 `ProjectId`/`GroupId` 分离 | REQ-016、REQ-017 |
| `SqliteDatabaseInitializer` v5 | Infrastructure | 增加项目颜色/待办项目列，按名称保守映射旧数据并保留审计计数 | REQ-019 |
| `MainWindow` 关注设置 | App | 保留项目设置和项目关键字区域、增加项目颜色创建及行内下拉修改、移除待办分组区域 | REQ-015、REQ-016 |

## 5. 接口与内部契约

以下为建议契约。实现时可以按仓库命名规范微调类型名，但字段语义和结果分类不得改变。

```csharp
public sealed record CreateManualTodoRequest(
    string Title,
    string? Description,
    PriorityLevel? Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? FirstReminderAt,
    long? ManagedProjectId);

public enum CreateManualTodoOutcome
{
    Created,
    ProjectMissing
}

public sealed record CreateManualTodoResult(
    CreateManualTodoOutcome Outcome,
    TodoItem? Todo);

public sealed record FollowedProject(
    long Id,
    string ProjectName,
    string Color,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record FollowedProjectSnapshot(
    IReadOnlyList<FollowedProject> Projects,
    IReadOnlyList<FollowedProjectKeyword> Keywords);

```

`UpdateTodoRequest` 和 `TodoItem` 增加 `long? ManagedProjectId`。手动路径传入显式选择；自动路径传入关键字匹配结果；未匹配为 `null`。旧 `GroupId` 不再出现在新建或更新请求中。（REQ-005、REQ-015、REQ-017、REQ-018）

现有项目创建、物理删除和关键字维护结果不重定义，只为创建请求增加颜色并为仓储增加 `UpdateColorAsync(projectId, color)`；颜色更新先规范化固定色板值，再以项目稳定 ID 执行单行更新，不调用项目创建或关键字写入。删除项目时由外键把相关 Todo 的 `managed_project_id` 设为 `NULL`。（AC-069 至 AC-072、AC-076、AC-086）

日历内部状态契约：

```csharp
public sealed record DesktopCalendarWindowState(
    double? Left,
    double? Top,
    double Width = 980,
    double Height = 620,
    double Opacity = 0.9,
    bool IsEdgeSnapEnabled = true,
    DesktopCalendarDockEdge DockEdge = DesktopCalendarDockEdge.None);
```

该契约不包含任何 Todo 内容；日历数据每次从 `ITodoRepository` 重新投影。（REQ-012、AC-052、AC-055）

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

迁移 v4 SQL：

```sql
ALTER TABLE todo_groups
    ADD COLUMN color TEXT NOT NULL DEFAULT '#4B5563';
```

v4 不改写 `todo_items.group_id`，也不增加颜色自由输入列。（REQ-009、AC-043）

迁移 v5 的目标 schema 语义：

```sql
ALTER TABLE followed_projects ADD COLUMN color TEXT NOT NULL DEFAULT '#4B5563';
ALTER TABLE todo_items ADD COLUMN managed_project_id INTEGER NULL
    REFERENCES followed_projects(id) ON DELETE SET NULL;

CREATE INDEX idx_todo_items_managed_project_id
    ON todo_items(managed_project_id);
```

以上 SQL 只表达最终结构；实现必须在同一迁移事务内按 D-024 回填，且不得改变现有项目唯一性或关键字 schema。`todo_groups` 和 `group_id` 不删除，仅停止新写入。（REQ-019）

### 6.2 验证规则

| 字段/操作 | 规则 | 失败行为 | 追溯 |
|---|---|---|---|
| 标题 | Trim 后非空 | 保留表单并提示“标题必填” | AC-004、AC-005 |
| 描述 | 可空；不冒充消息正文 | 原值保留 | AC-003、AC-027 |
| 优先级 | 未选择时 P2 | 应用默认，不依赖数据库默认 | AC-006 |
| 截止时间 | 日期可空；时/分来自有限选择项；本地时间必须有效 | 清空日期写入 null；无效本地时间保留表单并提示 | AC-007、AC-058 至 AC-060 |
| 首次提醒 | 可空；存在时必须晚于 `IClock.Now` | 保留表单并提示未来时间 | AC-008、AC-009 |
| 项目 ID | 可空；非空时必须存在且可用于选择 | 返回 ProjectMissing，刷新项目选项 | AC-019 至 AC-024、AC-073 至 AC-076 |
| 项目名称 | 保持现有 Trim、重复检查和持久化规则 | 提示为空或已存在 | AC-015 至 AC-018、AC-032 |
| 项目颜色 | 创建空值为灰色；创建和修改只允许固定七色；修改必须对应现有项目 ID | 非法值拒绝；更新失败恢复原颜色并提示 | AC-039 至 AC-043、AC-069 至 AC-072、AC-086 |
| 自动待办 DueAt | 用户时区中消息发送日（SentAt）最后一刻 | 使用消息发送时间与时区计算，不用采集时刻 | AC-044、AC-045 |
| 日历偏好 | 尺寸、透明度和坐标必须可规范化 | 损坏或越界值回退安全默认 | AC-055、NFR-008 |
| 项目关键字 | 保持现有规则：新项目自动添加项目名关键字、每项目可多个且至少一个 | 沿用现有校验提示，不允许删空 | AC-069、AC-077 至 AC-082 |
| ManagedProjectId | 可空；非空必须对应现有可用项目 | ProjectMissing/Persistence 分类结果 | AC-073 至 AC-076、AC-085 |

日期时间继续使用 `DateTimeOffset`，显示和输入按本地时区，持久化使用 `O` 格式。所有“当前时间”判断使用注入的 `IClock`，保证边界测试可重复。

### 6.3 待办生命周期

手动创建的待办从 `Pending` 开始，之后完全复用现有 `Pending/InProgress/Waiting/Done/Ignored` 生命周期。项目不是状态：

- 设置、更换或清除项目不会改变状态、截止时间桶、提醒、完成时间或置顶。
- 完成/忽略保留 `managed_project_id`，并继续执行现有清除置顶和取消开放提醒逻辑。
- 本期只允许活动待办修改项目；历史待办展示但不编辑项目。
- 自动识别待办初始截止时间为当天最后一刻；用户后续在详情修改截止时间后，日历和时间桶都以持久化新值为准。
- 日历同时展示活动和已办理待办，但只通过详情流程改变状态，不在日历内部执行状态写入。
- v5 后 `ManagedProjectId` 是用户可见归属，旧 `ProjectId` 只作内部分类兼容，不再直接驱动下拉框。

## 7. 控制流与数据流

### 7.1 打开创建表单

1. 用户在 `TodoPendingView` 点击“添加待办”。
2. `TodoWorkbenchViewModel.AddTodoCommand` 请求对话框服务打开创建窗口。
3. 创建 ViewModel 加载现有活动项目列表；成功时加入一个 UI 专用“未指定项目”选项，失败时显示读取错误而不是空列表。
4. 标题为空、优先级 P2，其余字段为空；此时没有数据库写入。

### 7.2 在现有项目设置中新增项目

1. 用户在项目设置中输入名称、从固定色板选择颜色并点击新增。
2. 设置 ViewModel/编排沿用现有项目创建服务，只向请求增加颜色。
3. 服务按现有规则保存项目，并按现有行为创建项目名初始关键字；项目与初始关键字保持原有一致性边界。
4. 成功：刷新项目/关键字快照；重复名称返回已有结果且不改原颜色，非法颜色保留输入并显示错误。
5. 待办窗口重新加载后可选择该项目；取消待办只取消待办创建，已提交项目继续存在。

### 7.2.1 在项目树节点中修改颜色

1. 项目节点的颜色下拉为固定七色选项，当前选择来自该项目已持久化颜色。
2. 用户选择另一颜色时，行模型先保留原颜色并进入保存中状态，再按项目稳定 ID 调用颜色更新服务；同值选择不写数据库。
3. 成功后刷新项目快照以及待办、已办理和日历的项目颜色投影；各视图的项目名称文字直接使用新颜色；项目名称、关键字和待办 `ManagedProjectId` 不变。
4. 失败时恢复原选择并显示错误；不得把未持久化颜色留在界面，也不得通过再次创建同名项目实现改色。

### 7.3 创建手动待办

1. 保存命令进入 busy，阻止重复执行。
2. ViewModel 把控件值转换为 `CreateManualTodoRequest`。
3. `TodoApplicationService` 验证标题和提醒时间、应用 P2 默认值。
4. `SqliteTodoUnitOfWork` 验证 `ManagedProjectId`，并在单事务内插入待办及可选首次提醒。
5. 成功：刷新工作台和项目解析，通知 MainWindow 刷新计数，关闭窗口。
6. 失败：回滚事务，恢复命令，保留表单输入并显示分类后的错误。

### 7.4 修改待办项目

1. 打开活动待办详情时加载活动项目并选中当前 `ManagedProjectId` 或“未指定项目”。
2. 保存时把 `ManagedProjectId` 与其他字段一起放入 `UpdateTodoRequest`。
3. 工作单元使用 `ExpectedUpdatedAt` 做单行乐观并发更新。
4. 成功刷新工作台；冲突提示用户关闭详情并重新打开；项目已不存在时刷新选项并保留其他输入。

### 7.5 创建自动到期待办

1. 消息被确认为 `@我` 并完成分类与紧急度计算。
2. 项目关键字匹配器使用当前项目/关键字快照返回一个项目或未命中。
3. `TodoService.CreateFromMention` 使用消息发送时间 `SentAt` 和用户时区计算发送日最后一刻；`TodoFactory.CreateFromMessage` 同时接收该 `DueAt` 和匹配到的 `ManagedProjectId`，既有 `SourceMessageId` 幂等持久化不变。
4. 工作台刷新后，消息当天发送的待办进入“今日到期”，日历显示在发送日对应的用户本地日期；补采的历史消息待办落在发送日，若发送日已过则进入“已逾期”。用户后续在详情修改截止时间后，日历和时间桶都以持久化新值为准。

### 7.6 刷新并恢复批量选择

1. 刷新前收集当前活动行中 `IsSelected=true` 的 Todo ID。
2. 重新读取活动/已办理集合并创建新行模型。
3. 对最新活动 ID 与旧选择 ID 的交集恢复选择。
4. 替换各时间桶后统一刷新选择计数、按钮文案和命令状态。

### 7.7 打开和刷新桌面日历

1. 用户点击主窗口左侧“桌面日历”。
2. 主窗口创建或复用唯一日历窗口，并从状态文件恢复偏好。
3. ViewModel 并行读取一次活动和一次已办理集合，再按待办 ID 批量读取一次提醒和一次来源消息发送时间，按本地日期建立投影、按四级键排序并生成 42 个日期单元。
4. 点击日期更新右侧列表；点击 Todo 通过现有详情回调打开详情，返回后刷新。
5. 每分钟自动刷新；隐藏时停止刷新，重新显示时立即刷新。

### 7.8 停靠、缩进和退出

1. 拖动结束时若启用自动吸附且最近边缘在 32 DIP 内，则记录并应用该边缘；“停靠边缘”按钮强制选择最近边缘。
2. 停靠状态下鼠标离开启动 2 秒计时；期间重新进入则取消。
3. 超时后隐藏日历表面、显示边缘把手并移动到缩进位置；点击把手恢复。
4. 隐藏/普通关闭保存状态但保留窗口实例；主程序退出调用 `Shutdown` 完整释放。

### 7.9 选择截止时间并打开日程概览

1. 创建或详情窗口把日期、小时、分钟分别绑定到 ViewModel；清空日期表示无截止时间。
2. 保存时 ViewModel 组合本地时间并构造 `DateTimeOffset`，应用服务和仓储契约不变。
3. 用户点击左侧“日程概览”，`MainWindow` 切换到 Tag=8 页并刷新共享 `DesktopCalendarViewModel`。
4. 内嵌页和桌面窗口都承载 `DesktopCalendarView`；点击待办继续调用同一详情回调。

### 7.10 用现有项目设置驱动消息、待办和看板

1. 设置页按现有方式读取项目和关键字；项目设置区域改为可折叠项目树，节点内可改色、删除项目、逐个删除或添加关键字，整页只保留最外层滚动条。
2. 新增项目时保存颜色并自动创建项目名关键字；继续允许多个关键字且不允许删除最后一个。
3. 待办创建和详情加载活动项目；“未指定项目”只存在于 UI，不写特殊项目记录。
4. 捕获/刷新消息时，matcher 对同一快照执行确定性匹配；自动 Todo 和项目看板使用返回的项目 ID。
5. 项目设置变化后使快照失效并刷新相关视图；既有待办不会被后台批量重分类。

### 7.11 编辑待办说明

1. 详情窗口在“原始消息”上方渲染“待办说明”多行编辑框，初值取 `todo_items.description`，即使没有说明也保持可见可编辑。
2. 用户在编辑框内修改后，点击上方唯一的“保存待办”统一提交（2026-10-29 起移除卡片内的“保存说明”按钮，避免同一动作出现两个入口）；说明仍通过 `SaveDescriptionAsync` 所在的同一条保存路径提交，只是界面不再单独暴露该入口。
3. 两条路径共用同一次保存：`TodoDetailViewModel` 把说明规范化（`Trim`，全空白视为无说明）后放入 `UpdateTodoRequest`，仍使用 `ExpectedUpdatedAt` 做乐观并发更新；冲突时给出与“保存待办”一致的提示。
4. 保存成功后把规范化结果回写界面，保证清空说明后编辑框为空且数据库存储 `NULL` 而不是空串；同时刷新一次共享工作台。
5. 2026-10-29 起原始消息卡片不再提供“复制原文”“复制全部信息”按钮，正文可直接选中复制；`TodoDetailViewModel.BuildCopyText()` 仍保留（含说明行，无说明时显示“无”），仅由回归测试引用。

### 7.12 滚动当日日程列表

1. 当日待办超出列表高度时，右侧出现 8 DIP 宽的主题化滚动条；内容未溢出时不出现。
2. 滑块使用当前主题的 `CalendarScrollThumb` 颜色，悬停变亮、拖动加深；轨道透明，直接透出面板背景。
3. 点击轨道空白区域按页滚动，拖动滑块连续滚动；滚动条不改变卡片宽度以外的任何布局，也不引入增减箭头按钮。
4. 点击“暗黑/浅色”切换主题时，滑块颜色在同一帧内切换到目标主题色值，不出现浅色残留。

## 8. 错误分类、恢复与用户可见行为

| 分类 | 示例 | 数据结果 | 用户可见行为 |
|---|---|---|---|
| Validation | 空标题、无效日期、过去提醒、空项目名 | 无写入 | 定位字段并保留输入 |
| DuplicateProject | 现有项目重复规则命中或并发唯一冲突 | 只保留已有项目，不改原颜色 | 提示“项目已存在” |
| ProjectColorUpdate | 项目已删除、颜色非法、数据库写入失败 | 不改变项目名、关键字或待办关联；失败时原颜色保持 | 恢复颜色下拉原值并提示“项目颜色保存失败” |
| ProjectMissing | 表单/详情引用已不存在的 `ManagedProjectId` | 待办不写入/不更新 | 刷新项目列表，保留其他输入 |
| ConcurrencyConflict | 详情 `ExpectedUpdatedAt` 不匹配 | 不覆盖新版本 | 提示重新打开详情 |
| Persistence | SQLite 忙、磁盘或 schema 错误 | 当前事务回滚 | 显示保存失败，可重试 |
| Cancelled | 窗口关闭或取消令牌触发 | 未开始或事务回滚 | 安静关闭或恢复可操作状态 |
| Migration | v3 SQL 或校验和失败 | 迁移事务回滚 | 初始化失败，不进入可写状态 |
| InvalidColor | 任意色值或不支持的色板输入 | 不创建/不改写项目 | 提示从预设色板选择 |
| CalendarState | JSON 损坏、非法数值或 IO 失败 | Todo 数据不受影响 | 回退默认偏好并继续打开 |
| CalendarRefresh | 仓储读取或详情打开失败 | 不写入重复数据 | 保留窗口并显示可恢复错误/由主窗口报告 |
| ProjectDelete | 删除项目过程中 SQLite 写入失败 | 当前删除事务回滚，不留下部分关键字/项目状态 | 显示删除失败并刷新项目列表 |
| ProjectMigrationConflict | 名称映射、外键或迁移 SQL 失败 | v5 不提交 | 阻止可写启动并提供可诊断信息 |

UI 不直接显示 SQL、数据库路径或堆栈。诊断可记录错误类型和操作阶段，不记录标题/描述全文。（REQ-007、NFR-006）

## 9. 安全、隐私、性能、兼容性与可观测性

### 9.1 安全与隐私

- 所有 SQL 值参数化，项目名称和关键字不得拼接进 SQL。
- 唯一冲突只映射为业务结果，不把原始 SQLite 错误直接显示给用户。
- 日志仅记录操作类型、结果和实体 ID；默认不记录待办标题、描述、项目名或关键字原文。
- 不新增网络、账号、权限或消息读取边界。
- 迁移备份路径和哈希可记录，但不得记录真实项目关键字或待办内容；备份继承本地数据库访问权限。

### 9.2 性能

- 项目数量预期远小于待办数量，按现有排序一次读取并构建字典。
- 每次工作台刷新最多增加一次项目查询，不产生按待办 N+1 查询。
- `todo_items.managed_project_id` 索引支持列表解析和删除引用检查；当前时间桶查询继续使用原有索引和排序。
- 日历刷新固定为一次活动查询和一次已办理查询；在内存中按日期分组并生成 42 个单元，不按日期查询仓储。
- 工作台、消息匹配和看板各使用一次项目/关键字快照；单次刷新内不得为每条消息或待办重新查询项目。

### 9.3 兼容性

- v3 迁移只新增表、可空列和索引；v4 只新增带默认值的颜色列，不修改待办关联。
- 自动待办按项目关键字写 `ManagedProjectId`，未匹配为空；消息转待办保持其现有触发与按 `SourceMessageId` 的唯一行为。
- 完成、批量完成、置顶、提醒弹窗完成和清空已办 SQL 必须继续使用显式列/条件，新增列不会改变其状态转换。
- WPF 样式、命令和对话框沿用现有组件，不引入新 UI 框架或依赖。
- 日历状态文件损坏不阻塞主数据库初始化；旧版本可忽略该文件和 v4 颜色列。
- v5 保留旧项目分类和分组列，只新增指向现有项目设置记录的关联；任何旧 `project_id` 转换都通过名称映射，不依赖数值 ID。

### 9.4 可观测性

- 创建窗口显示加载、保存、成功和失败状态。
- 应用服务返回可枚举结果，便于测试和 UI 映射。
- 迁移失败包含版本、名称和校验和上下文，但不把用户内容写入日志。
- 项目操作记录业务结果、稳定 ID 和阶段；不记录关键字全文。matcher 可记录隐私安全的命中数量和规则 ID。

## 10. 迁移、发布与回滚

### 10.1 迁移

1. `SchemaSql` 继续创建旧基础表，保证既有初始化顺序不变。
2. `schema_migrations` 表建立后依次验证/执行 v1、v2、v3、v4。
3. v3 在同一事务中创建 `todo_groups`、添加 `group_id` 和索引、写入迁移记录。
4. 旧待办不回填，读取时 `GroupId=null` 显示“未分组”。
5. v4 为所有既有分组补充默认灰色，新分组保存经过策略验证的色值。
6. 新数据库同样经过 v1 至 v4，避免新库与升级库 schema 漂移。
7. v5 按 D-024 为既有项目回填默认灰色、按名称保守回填 `managed_project_id`、建立查询索引并写迁移记录。
8. v5 后继续读取现有项目设置，旧分组表只作为审计/回滚来源，不接受新写入。

### 10.2 发布验证

- 使用旧版 schema fixture 验证升级前后待办逐字段一致。
- 使用新库验证迁移幂等、校验和和外键开启状态。
- 验证所有创建/更新/列表 SQL 都显式处理 `group_id`。
- 验证分组颜色迁移、自动到期边界、选择恢复、日历本地日期投影和四边缩进几何。
- 验证同名旧分组映射、不同名旧分组保持未指定项目、旧分类名称映射、项目颜色创建/按 ID 修改/失败恢复、初始/多关键字/至少一个关键字和跨页面统一匹配。
- 构建 WPF 与测试项目并运行仓库控制台回归测试器；实际窗口交互需单独执行手工 GUI 验收。

### 10.3 回滚

v3/v4 都是追加式兼容迁移。v5 同样保留旧表/列，但写入事实源发生变化，因此不承诺旧版与新版可以交替写同一已迁移数据库。正式切换前必须保留迁移前备份；旧版回滚写入必须恢复该备份，或仅以只读方式诊断当前库。

若 v3 未提交则事务整体回滚；若新版功能故障但迁移已成功，可回滚应用二进制并保留数据库。任何未来真正删除分组 schema 的操作都需要独立审批和备份方案。

## 11. 备选方案与权衡

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| 继续保留独立待办分组 | 已有实现可复用 | 与用户最新决定冲突，项目与分组重复配置且展示含义不一致 | 拒绝 |
| 在 `todo_items` 保存分组名称字符串 | 查询简单 | 重复名称、改名困难、并发唯一性弱、无法稳定引用 | 拒绝 |
| 特殊“未分组”实体 | 查询统一 | 需要保留 ID/名称，可能被重复或误操作 | 拒绝，使用 NULL |
| 只在 UI 内保存分组 | 实现快 | 重启丢失、无法满足详情与历史展示 | 拒绝 |
| 分组与待办一次事务创建 | 可一起取消 | 不符合新增分组独立保留要求，事务和交互耦合 | 拒绝 |
| 独立提交分组，待办+提醒原子提交 | 语义清晰，满足取消后保留与提醒一致性 | 分成两个可观察步骤 | 采用 |
| 按分组重排活动待办 | 分组更醒目 | 破坏已确认的截止时间分类和现有操作习惯 | 拒绝 |
| 任意颜色选择器 | 自由度高 | 低对比度、非法值、迁移和主题兼容成本高 | 拒绝，采用固定可读色板 |
| 刷新时清空勾选 | 实现最简单 | 后台刷新会破坏正在进行的批量选择 | 拒绝，按活动 Todo ID 恢复 |
| 日历复制一份待办数据库 | 查询隔离 | 双写、一致性和隐私风险 | 拒绝，复用仓储只读投影 |
| 日历使用普通子页面 | 生命周期简单 | 无法桌面常驻、停靠和自动收起 | 拒绝，采用主窗口拥有的唯一独立窗口 |
| 只把两个设置区放在同一页面，底层继续双表 | UI 改动小 | 名称、颜色和生命周期仍会漂移，详情无法保证与项目设置一致 | 拒绝 |
| 直接把 `todo_items.project_id` 当作 `followed_projects.id` | 无需新列 | 两套 ID 来源不同，可能静默错绑 | 拒绝 |
| 以 `followed_projects` 为统一事实源并新增 `ManagedProjectId` | 保留现有关键字与稳定项目 ID，迁移边界明确 | 需要 v5 迁移并保留兼容字段 | 采用 |
| 为不同名旧分组自动创建项目和关键字 | 表面上保留全部旧分类 | 偷偷改变项目设置和后续自动匹配，违背“项目关键字功能不动” | 拒绝，保留旧数据并把相关待办设为未指定项目 |
| 删除被待办引用的项目时拒绝删除 | 保留历史归属 | 改变现有项目删除功能，与“项目设置不动”冲突 | 拒绝；采用 `ON DELETE SET NULL` 保持现有删除语义 |

## 12. 需求到设计追踪

| 需求 | 主要设计决策与章节 |
|---|---|
| REQ-001 | D-006、4、7.1、7.3 |
| REQ-002 | D-006、5、6.2、7.3、8 |
| REQ-003 | D-005、D-007、5、7.3 |
| REQ-004 | D-022、D-026、5、6.1、7.2 |
| REQ-005 | D-023、D-027、5、6.3、7.4 |
| REQ-006 | D-027、D-010、6.3、9.2 |
| REQ-007 | D-003、D-007、D-011、8、9.4 |
| REQ-008 | D-005、D-006、D-012、9.3、10 |
| REQ-009 | D-022、D-024、D-026、4、5、6.1、6.2、9.3、10 |
| REQ-010 | D-014、4、6.2、6.3、7.5 |
| REQ-011 | D-015、4、7.6、9.2 |
| REQ-012 | D-016 至 D-019、4、5、7.7、7.8、8、9、10 |
| REQ-013 | D-020、4、6.2、7.3、7.9、8 |
| REQ-014 | D-016、D-017、D-021、4、7.7、7.9、9.2 |
| REQ-015 | D-022、D-026、D-028、4、5、7.10 |
| REQ-016 | D-022、D-024、D-026、D-028、5、6、7.2、7.10 |
| REQ-017 | D-023、D-026、D-027、5、6、7.1、7.3、7.4 |
| REQ-018 | D-025、4、7.10、9.2、9.4 |
| REQ-019 | D-024、D-028、8、10 |

## 13. 设计完成条件

本设计只有在以下条件全部满足时才可作为实现依据：

- `requirements.md` 中 REQ-001 至 REQ-019 和 AC-001 至 AC-086 均能映射到本设计，没有引入范围外能力。
- 数据库迁移、项目颜色创建/修改、手动待办、首次提醒和详情乐观并发均有明确事务边界。
- 手动来源、来源消息丢失和真实消息来源三种状态不会混淆。
- 时间分类不读取 `ManagedProjectId`；不同名旧分组相关待办升级后显示“未指定项目”，其他待办字段不变。
- 项目颜色只能来自固定策略；自动到期只作用于自动识别路径；刷新选择只恢复最新活动 ID。
- 日历只投影现有仓储，状态文件不含业务内容，四边停靠/缩进不改变保存尺寸。
- 现有项目设置仍是唯一项目事实源；项目设置/关键字行为保持；旧三套 ID 明确分离；v5 同名映射、异名不建项目、删除后外键置空和回滚均有确定语义。
- 后续 `tasks.md` 和 `check_list.md` 覆盖本设计及所有验收标准，并由用户统一确认四份当前版本后才修改产品代码。
