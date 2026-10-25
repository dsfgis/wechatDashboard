# `@我` 自动待办 Agent 优先级与内容分析

## 1. 当前需求与行为

在“模型配置”页提供“分析消息内容”复选框，开关立即保存并对之后处理的新消息生效：

- **未勾选**：不调用大模型；`@我` / `@所有人` 消息仍自动创建待办，使用本地规则优先级和默认说明。
- **已勾选**：仅对原本就会自动创建待办的新消息调用 Agent，单次分析同时返回 P0–P3 优先级和简洁中文摘要。
- Agent 结果分别覆盖 `TodoItem.Priority` 和 `TodoItem.Description`，摘要显示在待办详情的“待办说明”文本框中。
- 消息流不自动调用大模型重新判断列表优先级，也不采用自动待办的 Agent 优先级；原有手动“AI 建议”及审核流程保持不变。
- 模型未配置、超时、网络失败或响应格式异常时，仍创建待办，并回退为本地优先级和默认说明。
- 空白说明回退为默认文本，但不丢弃 Agent 已返回的有效优先级。
- 不批量回填已有待办；重复消息不重复调用模型；外部取消继续传播。
- API Key 继续保持“保存或清除后重启生效”，不复用手动建议的 `todo_suggestions`、审核状态或 `Decision` 语义。

## 2. 识别范围与边界

1. `MentionDetector` 支持配置别名、`@我`、`@你` 和 `@所有人`；`@所有人` 后紧跟微信 U+2005 空白时也能识别。
2. 只有检测为提及当前用户的消息才进入自动待办 Agent 分析，普通消息不调用模型。
3. Agent 不决定是否创建待办，不修改标题、项目、管理项目、截止时间或自动创建标记。
4. 本地 `UrgencyRanker` 和 `urgency_scores` 仍按原规则生成，保证消息流优先级不受自动待办 Agent 结果影响。
5. 功能只影响实施后新入库的消息；修复识别器或更新模型不会自动重算历史消息。

## 3. 当前实现

### 3.1 配置与装配

- `SqliteModelSettingsRepository` 使用现有 `app_settings` 表保存 `model_analyze_message_content`，值为 `"1"` 或 `"0"`；缺失或非法值按关闭处理。
- `MainWindow` 使用 `volatile bool _analyzeMessageContent` 让已运行的实时及手动采集管线立即读取新状态。
- 开关保存失败时恢复点击前状态；模型 Key 的加载和重启语义不受开关影响。
- `CreateCapturePipeline()` 注入 `_messageRepository`、`() => _analyzeMessageContent` 和 `_messageActionModel.AnalyzeMentionAsync`。

### 3.2 单次 Agent 分析协议

`OpenAiCompatibleMessageActionModel.AnalyzeMentionAsync` 使用独立系统提示词和 JSON 输出协议：

- 输入只包含当前消息的 `ToInput(message)`，不发送相邻消息、澄清回答、数据库路径或密钥。
- 请求使用 `temperature = 0`、`max_tokens = 400` 和 `response_format: json_object`。
- 内置 DeepSeek 继续携带 `thinking.type = disabled`；环境变量驱动的其他兼容服务不额外添加该参数。
- 模型必须返回 `{"priority":"P1","description":"..."}`，优先级仅允许 P0、P1、P2、P3。
- 返回类型为 `MentionMessageAnalysis(PriorityLevel Priority, string? Description)`。
- 无效枚举、缺失字段或非法 JSON 作为模型格式错误抛出，由采集管线统一降级；说明去除首尾空白，空白转换为 `null`。

### 3.3 自动待办管线与事务

`MessageCapturePipeline.ProcessAsync` 的处理顺序如下：

1. 检测提及并构造 `Message`，本地分类和紧急度规则保持不变。
2. 只有“提及当前用户、开关开启、分析依赖完整”时才准备 Agent 分析。
3. 调用模型前先通过 `ExistsAsync(source, sourceMessageKey)` 预检；已存在则跳过模型，后续仍由工作单元唯一键完成最终去重。
4. 新消息在 `TryProcessAsync` 之前完成网络分析，确保 SQLite 事务内没有网络等待。
5. 非外部取消异常记录脱敏调试信息并返回 `null`，继续原消息和待办处理；外部取消重新抛出。
6. `TodoService.CreateFromMention` 接收可空 `description` 和 `priority`，仅把非空结果覆盖到自动待办。
7. 消息、分类、本地紧急度和自动待办仍在同一 SQLite 事务中提交。

### 3.4 消息流隔离

- Agent 优先级只写入自动待办，不写入 `urgency_scores`，也不创建手动建议记录。
- 消息流仍通过原有有效优先级查询读取本地评分；没有手动建议时，显示本地 `UrgencyRanker` 结果。
- 因此同一条消息可以表现为“消息流本地 P2、自动待办 Agent P1”，这是明确设计而非数据不同步。

## 4. 自动化回归

`tests/WechatDashboard.Tests/Program.cs` 当前覆盖：

1. DeepSeek 假 HTTP 协议：Bearer Key、400 token 上限、JSON Output、关闭思考、只发送当前消息，以及优先级和摘要解析。
2. 空白说明保留有效模型优先级，并请求待办工厂使用默认说明。
3. 真实 SQLite 管线：开关关闭零调用、运行中开启立即生效、普通消息不调用、异常降级、空白说明、重复消息免调用和外部取消。
4. 优先级断言：关闭和异常使用本地 P2，成功分析使用 Agent P1，空白说明仍使用 Agent P0。
5. 消息流有效优先级继续为本地 P2，证明自动待办 Agent 结果没有改写消息评分。
6. 三人群 `@所有人` 与 U+2005 空白可通过真实 SQLite 管线生成有待办截止日期的记录。

## 5. 已执行验证与限制

- Release 和 Debug 解决方案构建成功。
- DeepSeek 分析及自动待办 SQLite 针对性测试通过。
- 完整 .NET runner 最近一次结果为 81/82；剩余失败是既有 `MessageFeedViewModel` 并发刷新/上下文导航竞态，不属于本功能。
- 自动化模型测试使用假 HTTP handler 或可控委托，不消耗真实 API 配额；真实 DeepSeek 在线结果仍需有效 Key 单独验收。
- 新增和修改代码保持必要的中文注释，新增代码注释率遵守项目 20% 以上的约束。

## 6. 相关文件

- `src/WechatDashboard.Application/Capture/MessageCapturePipeline.cs`
- `src/WechatDashboard.Application/Mentions/MentionDetector.cs`
- `src/WechatDashboard.Application/Todos/TodoService.cs`
- `src/WechatDashboard.Infrastructure/Agent/OpenAiCompatibleMessageActionModel.cs`
- `src/WechatDashboard.Infrastructure/Persistence/SqliteModelSettingsRepository.cs`
- `src/WechatDashboard.App/MainWindow.xaml`
- `src/WechatDashboard.App/MainWindow.xaml.cs`
- `tests/WechatDashboard.Tests/Program.cs`
