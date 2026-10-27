# 自动与手动待办 Agent 分析及引用处理

## 1. 当前需求与行为

在“模型配置”页提供“分析消息内容”复选框，开关立即保存并对之后处理的新消息生效：

- **未勾选**：不调用大模型；`@我` / `@所有人` 消息仍自动创建待办，使用本地规则优先级和默认说明。
- **已勾选**：仅对原本就会自动创建待办的新消息调用 Agent，单次分析同时返回 `shouldCreate`、P0–P3 优先级、简洁中文摘要，以及可选的截止时间 `dueAt` 和提醒时间 `reminderAt`。
- 只有模型成功且明确返回 `shouldCreate=false` 时才跳过自动待办；消息仍正常入库、分类并计算本地紧急度。判定需要创建时，Agent 结果分别覆盖 `TodoItem.Priority`、`TodoItem.Description`，摘要显示在待办详情的“待办说明”文本框中；识别出的 `dueAt` 优先作为截止时间，`reminderAt` 优先作为首次提醒时间。
- 消息流刷新和自动采集不调用大模型重新判断列表优先级，也不采用自动待办的 Agent 优先级；自动流程的消息流评分始终使用本地规则。
- 手动点击“转为待办”时，复用同一个 Agent 协议总结当前消息并分析优先级和时间，不受上述自动开关或是否提及当前用户限制。手动点击是明确创建指令，即使 `shouldCreate=false` 也创建；已有待办直接打开且不重复调用模型。
- 只有手动转换且 Agent 分析成功时，才把 Agent 优先级同时用于待办和该消息的消息流评分。模型失败仍按默认说明、本地优先级和本地时间规则创建，不追加 Agent 评分；空白说明使用默认文本，但保留有效 Agent 优先级。
- Agent 摘要只写入待办说明，不改写消息流原始正文。自动和手动分析发生模型未配置、超时、网络失败或响应格式异常时均安全回退，不阻断原有创建流程。
- 石化通当前消息带有 `reference_seq` 时，先联查并合并非重复的引用正文，再执行展示、自动待办分析、本地紧急度和截止时间计算。
- 自动待办截止时间优先使用 Agent 识别的明确时间；未识别时由本地规则生成：明确的会议开始时间优先，其次是“本周三”等本周星期，最后回退到消息发送当天日末；日历直接使用同一个 `TodoItem.DueAt` 分组。
- 首次提醒时间优先使用 Agent 识别的 `reminderAt`；未识别时按截止时间倒推：会议待办提前五分钟、普通任务提前一小时（`MeetingReminderLead` / `TaskEffortLead`）。提醒早于当前时间或无法落到未来时不创建提醒。手动消息待办同样按上述规则自动补全提醒；无来源消息的手动待办只在用户显式设置时才创建提醒。提醒与待办在同一 SQLite 事务原子写入。
- 跨群重复提醒同一件事时不重复建待办：在创建待办之前，把当前消息与最多 30 条活动待办交给 Agent 语义判定，命中后只把消息追加为来源消息（`todo_source_messages`），原始消息框同时展示多个群的消息。
- 合并时提醒时间只向后取更晚：新提醒晚于当前未触发提醒才取消旧提醒并改期，更早的忽略；后到消息识别的截止时间更晚时同步平移。已完成或已忽略的待办不参与判定，同一件事再次提醒会创建新待办。
- 不批量回填已有待办；重复消息不重复调用模型；外部取消继续传播。
- API Key 继续保持“保存或清除后重启生效”。旧“AI 建议”、审核、澄清以及 `todo_suggestions` 读写流程已删除；旧库遗留表和数据保留但不使用，新库不再创建该表。

## 2. 识别范围与边界

1. `MentionDetector` 支持配置别名、`@我`、`@你` 和 `@所有人`；`@所有人` 后紧跟微信 U+2005 空白时也能识别。
2. 只有检测为提及当前用户的消息才进入自动待办 Agent 分析，普通消息不调用模型。
3. Agent 决定自动待办是否创建，并可覆盖其优先级、说明以及识别出的截止时间、提醒时间；不修改标题、项目、管理项目或自动创建标记。消息未识别出明确时间时仍使用本地规则。
4. 本地 `UrgencyRanker` 和 `urgency_scores` 仍按原规则生成，保证消息流优先级不受自动待办 Agent 结果影响。
5. 石化通引用联查只在同一会话内按 `cider + server_seq` 进行；找不到引用、引用正文为空或内容已包含时不额外拼接。
6. 会议开始时间和本周星期仍由 `TodoService` 在本地解析作为可靠回退；Agent 提供明确 `dueAt` 时优先使用 Agent 时间。只有星期而没有具体时刻时使用目标日日末。
7. 功能只影响实施后新入库的消息；修复识别器、引用合并或更新模型不会自动批量重算历史消息。

## 3. 当前实现

### 3.1 配置与装配

- `SqliteModelSettingsRepository` 使用现有 `app_settings` 表保存 `model_analyze_message_content`，值为 `"1"` 或 `"0"`；缺失或非法值按关闭处理。
- `MainWindow` 使用 `volatile bool _analyzeMessageContent` 让已运行的实时及手动采集管线立即读取新状态。
- 开关保存失败时恢复点击前状态；模型 Key 的加载和重启语义不受开关影响。
- `CreateCapturePipeline()` 注入 `_messageRepository`、`() => _analyzeMessageContent` 和 `_messageActionModel.AnalyzeMentionAsync`。

### 3.2 单次 Agent 分析协议

`IMessageActionModel.AnalyzeMentionAsync` 是自动待办和手动转待办共用的分析入口，`OpenAiCompatibleMessageActionModel` 使用统一系统提示词和 JSON 输出协议：

- 输入只包含当前消息的 `ToInput(message)`；石化通消息在此已包含合并后的引用正文。不发送相邻消息、澄清回答、数据库路径或密钥。
- 请求使用 `temperature = 0`、`max_tokens = 500`（摘要与时间字段的有界输出）和 `response_format: json_object`。
- 内置 DeepSeek 继续携带 `thinking.type = disabled`；环境变量驱动的其他兼容服务不额外添加该参数。
- 模型必须返回 `{"shouldCreate":true,"priority":"P1","description":"...","dueAt":"2026-10-28T09:00:00+08:00","reminderAt":"2026-10-28T08:55:00+08:00"}`，优先级仅允许 P0、P1、P2、P3；`dueAt` 和 `reminderAt` 是 ISO 8601 带时区偏移的完整时刻字符串或 `null`，消息没有明确时间时必须为 `null`，不得猜测。
- 返回类型为 `MentionMessageAnalysis(PriorityLevel Priority, string? Description, bool ShouldCreate = true, DateTimeOffset? DueAt = null, DateTimeOffset? FirstReminderAt = null)`；默认值只用于未筛选或故障回退路径，模型响应仍必须显式提供布尔字段。
- `shouldCreate` 缺失或不是 JSON 布尔值、优先级枚举无效、缺失字段或非法 JSON 都作为模型格式错误抛出，由自动管线或手动转换流程分别降级；时间字段缺少时区偏移按格式错误处理，不猜测时区。说明去除首尾空白，空白转换为 `null`；时间字段空白或 `null` 转换为“未识别”。

### 3.3 自动待办管线与事务

`MessageCapturePipeline.ProcessAsync` 的处理顺序如下：

1. 检测提及并构造 `Message`，本地分类和紧急度规则保持不变；石化通消息正文已在采集适配器中完成引用合并。
2. 只有“提及当前用户、开关开启、分析依赖完整”时才准备 Agent 分析。
3. 调用模型前先通过 `ExistsAsync(source, sourceMessageKey)` 预检；已存在则跳过模型，后续仍由工作单元唯一键完成最终去重。
4. 新消息在 `TryProcessAsync` 之前完成网络分析，确保 SQLite 事务内没有网络等待。
5. 非外部取消异常记录脱敏调试信息并返回 `null`，继续原消息和待办处理；外部取消重新抛出。
6. 仅当成功分析明确返回 `ShouldCreate=false` 时跳过自动待办；否则 `TodoService.CreateFromMention` 接收可空 `description`、`priority`、`analysisDueAt` 和 `analysisFirstReminderAt`，把非空结果覆盖到自动待办，并计算随同创建的首次提醒时间。
7. 被跳过的消息仍正常保存；未跳过时，消息、分类、本地紧急度、自动待办和首次提醒仍在同一 SQLite 事务中提交（`MessageProcessingArtifacts.FirstReminderAt` 随消息事务原子写入）。

### 3.4 石化通引用正文与本地截止时间

- `ShihuatongLocalDatabaseCaptureAdapter` 通过 `quoted.cider = message.cider AND quoted.server_seq = message.reference_seq` 左联同一会话的被引用记录，并读取其正文回退字段。
- 当前正文和引用正文均使用既有 protobuf 解码及 `search_text` / `hint_content` 回退。`CombineWithReference` 仅在引用非空且尚未包含时，以“当前正文 + 空行 + `引用消息：` + 引用正文”合并，避免重复追加。
- 合并正文进入原始消息展示和后续整条处理管线，因此会议主题、时间、链接等引用信息可以参与 Agent 创建判断和本地日期解析。
- `TodoService.ResolveAutomaticDueAt` 优先使用 Agent 识别的明确完成/截止时间（`analysisDueAt`）；未识别时从 `会议时间` 中解析开始时间，例如 `2026/10/28 09:00-12:00 (GMT+08:00)` 使用当天 `09:00`，而不是会议结束时间。
- 无明确会议时间时，`本周` / `这周` / `本星期` / `这星期` 加中文星期会基于消息 `SentAt` 定位所在自然周；自然周从周一开始，无具体时刻时使用目标日日末。都没有识别时继续调用 `TodoDueBucketPolicy.EndOfToday(message.SentAt)`。
- `TodoService.ResolveAutomaticFirstReminderAt` 计算首次提醒：Agent 显式 `reminderAt` 优先；否则按截止时间倒推——正文命中会议时间的待办提前 `MeetingReminderLead`（五分钟），普通任务提前 `TaskEffortLead`（一小时）。结果早于当前时间或无截止时间时返回 `null`，不创建提醒。
- `SqliteTodoUnitOfWork` 手动消息待办创建时，显式提醒优先；未显式设置但存在截止时间时按同一会议/任务规则自动补全提醒。无来源消息的手动待办只使用用户显式设置的提醒，不从标题猜测时间。

### 3.5 消息流评分与手动转待办

- `MessageFeedViewModel.ConvertToTodoAsync` 先按原消息查询已有待办；存在时直接打开，不调用模型，也不追加评分。新待办则只发送当前消息并调用 `AnalyzeMentionAsync`，不检查自动分析开关，也不要求消息提及当前用户。
- 手动转换忽略 `ShouldCreate`，模型异常只触发回退，不阻断创建。空白摘要由 `TodoFactory` 使用默认说明；模型失败时使用默认说明和本地优先级，不追加 Agent 评分。
- 手动分析成功时，通过独立的 `CreateTodoFromMessageRequest.AgentPriority` 传递 Agent 优先级，避免普通 `Priority` 或后续待办优先级编辑意外改写消息流评分。`UrgencyRanker.ForManualAgentAnalysis` 将 P0/P1/P2/P3 映射为 90/75/50/20 分。
- `SqliteTodoUnitOfWork` 在事务内再次检查已有待办，然后把 Agent 评分、待办和首个提醒作为同一事务提交；任一步失败都会一起回滚。
- 消息流统一读取每条消息 ID 最新的 `urgency_scores` 优先级，没有评分时回退 P3。自动管线仍只追加本地 `UrgencyRanker` 评分；只有手动转换成功分析时才追加 Agent 评分。因此自动待办可以是 Agent P1，而消息流仍保持本地 P2。
- Agent 摘要只进入待办说明，消息流继续显示原始消息及采集时合并的引用正文。旧 `todo_suggestions` 不再参与有效优先级或其他读写流程；旧库保留遗留数据，新库不创建该表。

### 3.6 跨群去重协议与合并事务

`IMessageActionModel.MatchDuplicateTodoAsync` 是去重判定的唯一入口，`TodoDuplicateDetector` 负责候选与降级：

- 候选来源：`ITodoRepository.GetActiveAsync` 中状态为 `Pending` / `InProgress` / `Waiting` 的待办，按 `UpdatedAt` 倒序取最多 30 条；`Done` / `Ignored` 不进入候选。
- 请求使用 `temperature = 0`、`max_tokens = 120`、`response_format: json_object`，只发送当前消息 `ToInput(message)` 与候选的 `id`、`title`、`description`、`status`、`dueAt`，不发送相邻消息或密钥。
- 模型必须返回 `{"duplicateTodoId":12}` 或 `{"duplicateTodoId":null}`；返回值必须是候选集合中存在的整数 id，否则按未命中处理。非法 JSON 抛出格式错误，由 `TodoDuplicateDetector` 捕获后按未命中处理，不阻断采集。
- 判定发生在 SQLite 事务之前：`MessageCapturePipeline` 新增 `detectDuplicateAsync` 委托，在 `PrepareMentionAnalysisAsync` 之后调用；手动链路在 `AnalyzeMentionAsync` 之后、`CreateFromMessageAsync` 之前调用。
- 命中后的写入：自动链路通过 `MessageProcessingArtifacts.MergeIntoTodoId`、`MergeDueAt` 交给 `SqliteMessageProcessingUnitOfWork`；手动链路调用 `ITodoUnitOfWork.MergeIntoExistingAsync(MergeTodoRequest)`。两者共用 `SqliteTodoMerge.ApplyAsync`：写入 `todo_source_messages`（`is_primary = 0`）、刷新 `todo_items.updated_at`、在新提醒更晚时把旧提醒置为 `Cancelled` 并插入新提醒、在新截止时间更晚时更新 `due_at`。
- 命中时仍会构造待办草稿：若目标待办在判定与写入之间被完成或删除，工作单元会回退到正常新建，消息不会丢失归属。
- `MessageProcessingWriteResult.MergedIntoTodoId` 标记合并结果，`CreateTodoOutcome.MergedIntoExisting` 供手动链路给出“已把本条消息加入原始消息”的反馈。
- 查询与展示：`GetBySourceMessageIdsAsync` 先查关联表、再按主来源消息回退，使被合并的第二条消息也显示“已转待办”；`GetSourceMessagesForTodosAsync` 按主来源在前返回全部来源消息；`GetByAnySourceMessageIdAsync` 支持按任意来源消息幂等打开待办。

## 4. 自动化回归

`tests/WechatDashboard.Tests/Program.cs` 当前覆盖：

1. DeepSeek 假 HTTP 协议：Bearer Key、500 token 上限、JSON Output、关闭思考、只发送当前消息，以及 `shouldCreate`、优先级、摘要和 `dueAt`/`reminderAt` 时间解析（含 `null` 与缺少时区偏移的格式错误路径）。
2. 空白说明保留有效模型优先级，并请求待办工厂使用默认说明；`null` 时间字段解析为未识别。
3. 真实 SQLite 自动管线：开关关闭零调用、运行中开启立即生效、普通消息不调用、异常降级、空白说明、重复消息免调用和外部取消。
4. 自动分析明确 `shouldCreate=false` 时消息成功入库但待办数为 0；异常路径仍创建待办，证明显式拒绝与模型故障语义不同。
5. 自动优先级断言：关闭和异常使用本地 P2，成功分析使用 Agent P1，空白说明仍使用 Agent P0；显式跳过消息仍保留本地 P3 评分。
6. 自动分析后的消息流有效优先级继续为本地结果，证明自动待办 Agent 结果没有改写消息评分。
7. 手动转换覆盖非 `@我` 消息、`shouldCreate=false` 仍创建、Agent 摘要和优先级同时生效、原消息正文不变、空白说明回退、模型失败回退，以及已有待办零新增模型调用和评分。
8. 手动评分事务覆盖普通待办优先级不改消息评分、工作单元已有待办复查不追加评分，以及提醒写入失败时 Agent 评分和待办一起回滚。
9. 首次提醒规则：会议待办开始前五分钟提醒、普通任务截止前一小时提醒、Agent 显式 `dueAt`/`reminderAt` 覆盖本地规则、过去时间不创建提醒；手动消息待办未显式设置提醒时按任务规则自动补全。
10. 待办工作台优先级分组：P0–P3 独立统计全部活动待办（含置顶、不含已办），与截止时间五组计数并存。
11. 三人群 `@所有人` 与 U+2005 空白可通过真实 SQLite 管线生成有待办截止日期的记录。
12. 石化通 `reference_seq` 和微信 `appmsg type=57` / `refermsg` 引用正文合并，以及多行正文、XML 转义、空引用和重复引用处理。
13. 2026-10-26 周一发送“本周三”时截止为 2026-10-28 日末并进入日历 `Upcoming` 分组；引用会议包含 `2026/10/28 09:00-12:00` 时截止为 2026-10-28 09:00。
14. 跨群合并：第二条群的提醒只追加来源消息、活动待办仍为一条，两条来源消息按主来源在前返回，两条消息都能查到同一条待办。
15. 提醒合并规则：新提醒更晚时旧提醒置为 `Cancelled` 并改期到更晚时间；新提醒更早时不改期但仍追加来源消息；待办完成后既不进入去重候选也拒绝继续合并。
16. 真实管线去重：第一条 `@我` 消息建待办，第二条不同群的等价消息经 `TodoDuplicateDetector` 命中后 `CreatedTodoCount = 0`、来源消息两条、提醒改期为更晚时间；没有候选待办的首条消息不调用模型。
17. 迁移 v6：在模拟的升级前库（无关联表、无 v6 记录）上初始化后历史 `source_message_id` 回填为 `is_primary = 1`，重复初始化不产生重复关联行；详情页多来源时按条展示并隐藏单来源行。

## 5. 已执行验证与限制

- 本次改动后的 Release 解决方案构建成功，0 编译错误。
- 完整 .NET runner 最新结果为 86/87：除 `Native WeChat reader matches the frozen Python oracle field by field`（跨群去重功能实施前已失败，内容摘要压缩消息回退差异，与本功能无关，已在干净基线上复现）外全部通过。
- 已通过正式 `ShihuatongLocalDatabaseCaptureAdapter.ReadMessages` 只读验证一条真实授权引用会议记录，确认合并正文包含完整会议信息；备份后定向修复了对应现有消息和待办，没有批量回填历史数据。
- 自动化模型测试使用假 HTTP handler 或可控 Fake，不消耗真实 API 配额；真实 DeepSeek 在线结果仍需有效 Key 单独验收。
- 新增和修改代码保持必要的中文注释，新增代码注释率遵守项目 20% 以上的约束。

## 6. 相关文件

- `src/WechatDashboard.Application/Agent/IMessageActionModel.cs`
- `src/WechatDashboard.Application/Capture/IMessageProcessingUnitOfWork.cs`
- `src/WechatDashboard.Application/Capture/MessageCapturePipeline.cs`
- `src/WechatDashboard.Application/Mentions/MentionDetector.cs`
- `src/WechatDashboard.Application/Todos/TodoApplicationService.cs`
- `src/WechatDashboard.Application/Todos/TodoFactory.cs`
- `src/WechatDashboard.Application/Todos/TodoService.cs`
- `src/WechatDashboard.Application/Todos/TodoDuplicateDetector.cs`
- `src/WechatDashboard.Application/Todos/ITodoUnitOfWork.cs`
- `src/WechatDashboard.Application/Capture/ITodoRepository.cs`
- `src/WechatDashboard.Application/Urgency/UrgencyRanker.cs`
- `src/WechatDashboard.Infrastructure/Agent/OpenAiCompatibleMessageActionModel.cs`
- `src/WechatDashboard.Infrastructure/Capture/ShihuatongLocalDatabaseCaptureAdapter.cs`
- `src/WechatDashboard.Infrastructure/Capture/WeChatMessageContentDecoder.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteMessageProcessingUnitOfWork.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteMessageRepository.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteModelSettingsRepository.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteTodoUnitOfWork.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteTodoRepository.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteTodoMerge.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteDatabaseInitializer.cs`
- `src/WechatDashboard.App/ViewModels/Messages/MessageFeedViewModel.cs`
- `src/WechatDashboard.App/ViewModels/Todos/TodoWorkbenchViewModel.cs`
- `src/WechatDashboard.App/ViewModels/Todos/TodoDetailViewModel.cs`
- `src/WechatDashboard.App/Views/Todos/TodoPendingView.xaml`
- `src/WechatDashboard.App/Views/Todos/TodoDetailWindow.xaml`
- `src/WechatDashboard.App/MainWindow.xaml`
- `src/WechatDashboard.App/MainWindow.xaml.cs`
- `tests/WechatDashboard.Tests/Program.cs`
