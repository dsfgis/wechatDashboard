# `@我` / `@所有人` 自动待办分析与引用处理

## 1. 当前需求与行为

在“模型配置”页提供“分析消息内容”复选框，开关立即保存并对之后处理的新消息生效：

- **未勾选**：不调用大模型；`@我` / `@所有人` 消息仍自动创建待办，使用本地规则优先级和默认说明。
- **已勾选**：仅对原本就会自动创建待办的新消息调用 Agent，单次分析同时返回 `shouldCreate`、P0–P3 优先级和简洁中文摘要。
- 只有模型成功且明确返回 `shouldCreate=false` 时才跳过自动待办；消息仍正常入库、分类并计算本地紧急度。建议创建时，Agent 结果分别覆盖 `TodoItem.Priority` 和 `TodoItem.Description`，摘要显示在待办详情的“待办说明”文本框中。
- 消息流不自动调用大模型重新判断列表优先级，也不采用自动待办的 Agent 优先级；原有手动“AI 建议”及审核流程保持不变。
- 模型未配置、超时、网络失败或响应格式异常时，仍创建待办，并回退为本地优先级和默认说明；模型故障不会被解释为“无需创建”。
- 空白说明回退为默认文本，但不丢弃 Agent 已返回的有效优先级。
- 石化通当前消息带有 `reference_seq` 时，先联查并合并非重复的引用正文，再执行展示、自动待办分析、本地紧急度和截止时间计算。
- 自动待办截止时间始终由本地规则生成：明确的会议开始时间优先，其次是“本周三”等本周星期，最后回退到消息发送当天日末；日历直接使用同一个 `TodoItem.DueAt` 分组。
- 不批量回填已有待办；重复消息不重复调用模型；外部取消继续传播。
- API Key 继续保持“保存或清除后重启生效”，不复用手动建议的 `todo_suggestions`、审核状态或 `Decision` 语义。

## 2. 识别范围与边界

1. `MentionDetector` 支持配置别名、`@我`、`@你` 和 `@所有人`；`@所有人` 后紧跟微信 U+2005 空白时也能识别。
2. 只有检测为提及当前用户的消息才进入自动待办 Agent 分析，普通消息不调用模型。
3. Agent 只决定自动待办是否创建，并可覆盖其优先级和说明；不修改标题、项目、管理项目、截止时间或自动创建标记。
4. 本地 `UrgencyRanker` 和 `urgency_scores` 仍按原规则生成，保证消息流优先级不受自动待办 Agent 结果影响。
5. 石化通引用联查只在同一会话内按 `cider + server_seq` 进行；找不到引用、引用正文为空或内容已包含时不额外拼接。
6. 会议开始时间和本周星期均由 `TodoService` 在本地解析，不交给大模型修改；只有星期而没有具体时刻时使用目标日日末。
7. 功能只影响实施后新入库的消息；修复识别器、引用合并或更新模型不会自动批量重算历史消息。

## 3. 当前实现

### 3.1 配置与装配

- `SqliteModelSettingsRepository` 使用现有 `app_settings` 表保存 `model_analyze_message_content`，值为 `"1"` 或 `"0"`；缺失或非法值按关闭处理。
- `MainWindow` 使用 `volatile bool _analyzeMessageContent` 让已运行的实时及手动采集管线立即读取新状态。
- 开关保存失败时恢复点击前状态；模型 Key 的加载和重启语义不受开关影响。
- `CreateCapturePipeline()` 注入 `_messageRepository`、`() => _analyzeMessageContent` 和 `_messageActionModel.AnalyzeMentionAsync`。

### 3.2 单次 Agent 分析协议

`OpenAiCompatibleMessageActionModel.AnalyzeMentionAsync` 使用独立系统提示词和 JSON 输出协议：

- 输入只包含当前消息的 `ToInput(message)`；石化通消息在此已包含合并后的引用正文。不发送相邻消息、澄清回答、数据库路径或密钥。
- 请求使用 `temperature = 0`、`max_tokens = 400` 和 `response_format: json_object`。
- 内置 DeepSeek 继续携带 `thinking.type = disabled`；环境变量驱动的其他兼容服务不额外添加该参数。
- 模型必须返回 `{"shouldCreate":true,"priority":"P1","description":"..."}`，优先级仅允许 P0、P1、P2、P3。
- 返回类型为 `MentionMessageAnalysis(PriorityLevel Priority, string? Description, bool ShouldCreate = true)`；默认值只用于未筛选或故障回退路径，模型响应仍必须显式提供布尔字段。
- `shouldCreate` 缺失或不是 JSON 布尔值、优先级枚举无效、缺失字段或非法 JSON 都作为模型格式错误抛出，由采集管线统一降级；说明去除首尾空白，空白转换为 `null`。

### 3.3 自动待办管线与事务

`MessageCapturePipeline.ProcessAsync` 的处理顺序如下：

1. 检测提及并构造 `Message`，本地分类和紧急度规则保持不变；石化通消息正文已在采集适配器中完成引用合并。
2. 只有“提及当前用户、开关开启、分析依赖完整”时才准备 Agent 分析。
3. 调用模型前先通过 `ExistsAsync(source, sourceMessageKey)` 预检；已存在则跳过模型，后续仍由工作单元唯一键完成最终去重。
4. 新消息在 `TryProcessAsync` 之前完成网络分析，确保 SQLite 事务内没有网络等待。
5. 非外部取消异常记录脱敏调试信息并返回 `null`，继续原消息和待办处理；外部取消重新抛出。
6. 仅当成功分析明确返回 `ShouldCreate=false` 时跳过自动待办；否则 `TodoService.CreateFromMention` 接收可空 `description` 和 `priority`，把非空结果覆盖到自动待办。
7. 被跳过的消息仍正常保存；未跳过时，消息、分类、本地紧急度和自动待办仍在同一 SQLite 事务中提交。

### 3.4 石化通引用正文与本地截止时间

- `ShihuatongLocalDatabaseCaptureAdapter` 通过 `quoted.cider = message.cider AND quoted.server_seq = message.reference_seq` 左联同一会话的被引用记录，并读取其正文回退字段。
- 当前正文和引用正文均使用既有 protobuf 解码及 `search_text` / `hint_content` 回退。`CombineWithReference` 仅在引用非空且尚未包含时，以“当前正文 + 空行 + `引用消息：` + 引用正文”合并，避免重复追加。
- 合并正文进入原始消息展示和后续整条处理管线，因此会议主题、时间、链接等引用信息可以参与 Agent 创建判断和本地日期解析。
- `TodoService.ResolveAutomaticDueAt` 优先从 `会议时间` 中解析开始时间，例如 `2026/10/28 09:00-12:00 (GMT+08:00)` 使用当天 `09:00`，而不是会议结束时间。
- 无明确会议时间时，`本周` / `这周` / `本星期` / `这星期` 加中文星期会基于消息 `SentAt` 定位所在自然周；自然周从周一开始，无具体时刻时使用目标日日末。都没有识别时继续调用 `TodoDueBucketPolicy.EndOfToday(message.SentAt)`。

### 3.5 消息流隔离

- Agent 优先级只写入自动待办，不写入 `urgency_scores`，也不创建手动建议记录。
- 消息流仍通过原有有效优先级查询读取本地评分；没有手动建议时，显示本地 `UrgencyRanker` 结果。
- 因此同一条消息可以表现为“消息流本地 P2、自动待办 Agent P1”，这是明确设计而非数据不同步。

## 4. 自动化回归

`tests/WechatDashboard.Tests/Program.cs` 当前覆盖：

1. DeepSeek 假 HTTP 协议：Bearer Key、400 token 上限、JSON Output、关闭思考、只发送当前消息，以及 `shouldCreate`、优先级和摘要解析。
2. 空白说明保留有效模型优先级，并请求待办工厂使用默认说明。
3. 真实 SQLite 管线：开关关闭零调用、运行中开启立即生效、普通消息不调用、异常降级、空白说明、重复消息免调用和外部取消。
4. 明确 `shouldCreate=false` 时消息成功入库但待办数为 0；异常路径仍创建待办，证明显式拒绝与模型故障语义不同。
5. 优先级断言：关闭和异常使用本地 P2，成功分析使用 Agent P1，空白说明仍使用 Agent P0；显式跳过消息仍保留本地 P3 评分。
6. 消息流有效优先级继续为本地结果，证明自动待办 Agent 结果没有改写消息评分。
7. 三人群 `@所有人` 与 U+2005 空白可通过真实 SQLite 管线生成有待办截止日期的记录。
8. 石化通引用正文合并、已包含引用时的去重，以及空引用不改变正文。
9. 2026-10-26 周一发送“本周三”时截止为 2026-10-28 日末并进入日历 `Upcoming` 分组；引用会议包含 `2026/10/28 09:00-12:00` 时截止为 2026-10-28 09:00。

## 5. 已执行验证与限制

- 本次改动后的 Release 解决方案构建成功，0 编译错误。
- DeepSeek 创建筛选、故障回退、石化通引用合并、本地星期与会议开始时间等针对性测试通过。
- 已通过正式 `ShihuatongLocalDatabaseCaptureAdapter.ReadMessages` 只读验证一条真实授权引用会议记录，确认合并正文包含完整会议信息；备份后定向修复了对应现有消息和待办，没有批量回填历史数据。
- 完整 .NET runner 最近一次结果为 81/82；剩余失败是既有 `MessageFeedViewModel` 并发刷新/上下文导航竞态，不属于本功能，修复前不能宣称完整测试全部通过。
- 自动化模型测试使用假 HTTP handler 或可控委托，不消耗真实 API 配额；真实 DeepSeek 在线结果仍需有效 Key 单独验收。
- 新增和修改代码保持必要的中文注释，新增代码注释率遵守项目 20% 以上的约束。

## 6. 相关文件

- `src/WechatDashboard.Application/Capture/MessageCapturePipeline.cs`
- `src/WechatDashboard.Application/Mentions/MentionDetector.cs`
- `src/WechatDashboard.Application/Todos/TodoService.cs`
- `src/WechatDashboard.Infrastructure/Agent/OpenAiCompatibleMessageActionModel.cs`
- `src/WechatDashboard.Infrastructure/Capture/ShihuatongLocalDatabaseCaptureAdapter.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteModelSettingsRepository.cs`
- `src/WechatDashboard.App/MainWindow.xaml`
- `src/WechatDashboard.App/MainWindow.xaml.cs`
- `tests/WechatDashboard.Tests/Program.cs`
