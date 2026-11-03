# 简报事项项目记忆与状态跟踪

## Context

实施前项目简报只输出一次性文本，事项没有稳定 ID、来源证据、当前状态或历史记录，因此下一次生成简报无法可靠回答同一事项是否完成、阻塞、搁置或废弃。本次在不新增顶级菜单、不改变待办规则的前提下，将简报事项沉淀为项目记忆，并在后续简报中持续关联和跟踪。

已确认采用**批量确认后入库**：生成简报只产生新增及状态变化建议，用户勾选并点击“应用选中”后才写入，避免模型误判直接污染项目记忆。

## 一、领域模型与状态规则

新增独立于 Todo 的项目记忆模型，不复用待办状态，也不改自动待办优先级逻辑。

- `ProjectMemoryItem`：稳定 ID、项目 ID、类别（进展/风险/行动）、标题、规范化标题、当前状态、创建/更新/最近提及时间。
- `ProjectMemorySource`：事项与来源消息关联，并保存来源、来源键、会话、发送人、发送时间和正文快照；消息日后删除时仍保留证据。
- `ProjectMemoryStatusChange`：前状态、后状态、原因、主要证据消息、建议来源（简报/人工）、发生时间。
- `ProjectMemoryProposal`：本次简报建议，包含既有事项 ID（新事项为空）、基准状态、建议状态、标题、类别、原因、来源消息 ID 和勾选状态。

状态采用：`Open`、`InProgress`、`Blocked`、`Shelved`、`Completed`、`Discarded`。

- 新事项默认 `Open`；有明确推进证据可进入 `InProgress`，有阻断证据进入 `Blocked`。
- `Shelved` 表示暂缓且仍可能恢复；`Discarded` 表示取消或不再需要，必须与 `Completed` 区分。
- 完成必须有交付/验收证据，废弃必须有取消/不再需要证据。
- 未被本次简报提及的事项保持原状态，不能因“没有消息”自动完成或搁置。
- 模型不得重开已完成或已废弃事项；终态重开仅允许人工修正。

## 二、SQLite v9 与事务仓储

在 `SqliteDatabaseInitializer` 追加 v9，保留既有迁移和校验和：

1. `project_memory_items`：项目外键、当前状态和规范化标题；对同一项目的非终态规范化标题建立唯一约束，防止活跃重复事项。
2. `project_memory_sources`：`UNIQUE(item_id, source, source_message_key)`，消息外键删除时置空但保留快照。
3. `project_memory_status_history`：记录完整状态变化及原因、证据和来源。

新增 `IProjectMemoryRepository` / `SqliteProjectMemoryRepository` 与轻量 `ProjectMemoryService`：

- 按项目读取事项、来源及状态时间线。
- 应用建议时重新读取当前状态，校验项目归属和基准状态，防止过期窗口覆盖新状态。
- 新事项按项目内非终态规范化标题再次归并；同一事项、同一来源重复应用不产生重复行。
- 事项、来源快照、状态历史在同一事务提交；状态未变化只更新最近提及时间和补充来源，不写假历史。
- 并发或过期确认明确提示刷新，不静默覆盖。

## 三、简报结构化关联

修改 `ProjectBriefingContracts`、`ProjectBriefingService` 和 `OpenAiCompatibleMessageActionModel`：

- `BriefingMessage` 增加数据库消息 ID，保留现有来源、会话、发送人、正文和时间。
- 简报请求附带本项目既有事项的 ID、标题、类别和状态；响应在保留 Summary/Highlights/Risks/Actions 的同时增加结构化 `Items`。
- 模型只能返回输入中的真实既有事项 ID 和来源消息 ID；`itemId=null` 才表示建议新事项。提示词要求同一事项优先关联旧 ID，不得按相似名称臆造新事项，消息正文只作为数据而非指令。
- 服务校验项目归属、来源白名单、至少一条证据、状态及迁移合法性；伪造 ID、跨项目引用或无证据状态变化均拒绝并回退本地简报。
- 本地回退仍生成可读简报，并仅从明确风险及 @我 行动消息形成 `Blocked/Open` 建议，不猜测完成、搁置或废弃。
- 未进入本次模型上下文或未被提及的旧事项不变更状态；保留现有简报条数限制和摘要输出，避免扩大本次改造范围。

## 四、项目看板交互

在现有“项目简报”附近增加“项目记忆”区域，不新增顶级菜单：

- 简报生成后保留当前文本展示，同时显示“新增事项/状态变化建议”表格；新增和变更新增默认勾选。
- 表格展示类别、标题、当前状态、建议状态、原因和来源条数；建议状态应用前可人工修正。
- “应用选中”一次提交所选建议，成功后刷新项目记忆并显示实际新增、更新、跳过数量。
- 项目记忆列表支持按项目及状态筛选，展示当前状态、最近更新和最近状态原因；选中事项可查看来源快照与状态时间线。
- 已完成和已废弃默认折叠，可切换查看；模型失败时明确标记建议来自本地规则。

## 五、关键文件与实施顺序

1. 领域与契约：`Domain/Entities/ProjectMemoryItem.cs`、`Domain/Enums`、`Application/Agent/ProjectBriefingContracts.cs`、新增 `Application/Briefing/ProjectMemoryContracts.cs`。
2. 持久化：`SqliteDatabaseInitializer.cs`、新增 `SqliteProjectMemoryRepository.cs`、`ProjectMemoryService.cs`。
3. 模型与服务：`ProjectBriefingService.cs`、`OpenAiCompatibleMessageActionModel.cs`，复用周报服务的来源 ID 白名单校验思路。
4. UI：`MainWindow.xaml`、`MainWindow.xaml.cs`，沿用现有后台执行、按钮防重入和状态提示模式。
5. 测试：新增独立项目记忆测试文件，并在 `tests/WechatDashboard.Tests/Program.cs` 注册。

所有新增和修改代码保持中文注释占比超过 20%，不引入图数据库或额外框架。

## 六、验证

自动化覆盖：

- v9 新库/旧库升级、重复初始化及旧迁移校验和不变。
- 首次事项入库、跨期按稳定 ID 关联、非终态标题去重、重复应用幂等。
- 完成、阻塞、搁置、废弃的区别及合法迁移；终态不能被模型重开。
- 伪造事项 ID、未知/跨项目来源、无证据完成或废弃、非法状态值均拒绝。
- 来源快照与状态时间线保留；状态不变不写历史。
- 过期确认和并发更新不覆盖新状态；事务失败无部分写入。
- 模型异常、本地回退和取消传播；现有简报文本格式保持兼容。

执行：

```powershell
dotnet build WechatDashboard.sln -c Release
dotnet run --project tests/WechatDashboard.Tests -c Release
```

端到端手工验证：生成首期简报并应用事项 → 新增完成、阻塞、搁置、废弃证据消息 → 再生成简报并核对建议 → 应用后检查状态时间线和来源快照 → 重复应用确认不产生重复事项或历史。

## 七、实施结果

- 已完成 SQLite v9、事务仓储、结构化简报模型校验、本地保守建议，以及项目看板中的批量确认、状态筛选、终态折叠、来源快照和状态时间线。
- 新增独立项目记忆集成测试，覆盖旧库升级、跨期稳定 ID、标题归并、重复应用、事务回滚、来源删除、模型非法值和取消传播。
- `dotnet build WechatDashboard.sln -c Release` 构建 0 错误；`dotnet run --project tests/WechatDashboard.Tests -c Release` 全量 112 项测试通过，仅有既有 NU1900 网络警告。
