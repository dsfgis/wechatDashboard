# 移除 AI 建议并接入手动转待办分析计划

## Context

消息流当前同时存在“AI 建议”和“转为待办”：前者生成需要审核的建议，后者直接使用默认内容创建待办。用户要求移除“AI 建议”，把 Agent 分析合并到手动“转为待办”操作中。

最新确认的行为边界：只有用户点击“转为待办”时，Agent 才为该消息生成待办摘要和优先级，并将该优先级用于消息流评分；自动采集、自动 `@我` / `@所有人` 待办的消息流评分仍保持本地规则。Agent 摘要只写入待办说明，消息流继续显示原始消息及引用正文。

## 目标行为

- 删除消息流“AI 建议”按钮、建议面板、采纳/忽略/澄清流程及其生产代码。
- 点击“转为待办”时，即使消息没有 `@我`，也调用同一 `AnalyzeMentionAsync` 模型入口；不受“分析消息内容”自动开关限制。
- 手动点击是明确创建指令：忽略 `ShouldCreate`，即使模型返回 `shouldCreate=false` 也照常创建待办。
- 手动 Agent 分析成功时：
  - Agent 优先级同时用于新待办和该消息的消息流评分。
  - 非空 `Description` 写入待办说明；空白说明使用默认文本，但 Agent 优先级仍生效。
  - 消息流正文不变，不展示 Agent 摘要。
- 模型未配置、失败、超时或非法 JSON 时，仍按原标题、默认说明和既有本地优先级创建，消息流保持本地评分。
- 已有待办继续幂等打开，不重复调用模型；并发情况下仍由工作单元保证只创建一个待办。
- 自动待办管线保持现状：Agent 只影响自动待办的创建、优先级和说明，自动消息流评分仍由本地规则生成。

## 实施方案

### 1. 收缩模型接口并删除旧建议协议

- 新增 `src/WechatDashboard.Application/Agent/IMessageActionModel.cs`，接口只声明自动和手动共用的 `AnalyzeMentionAsync`。
- 删除 `TodoSuggestionAgent.cs` 中的建议 Agent、审核 DTO、旧上下文协议及建议仓储接口，并删除 `SqliteTodoSuggestionRepository.cs`。
- 修改 `OpenAiCompatibleMessageActionModel.cs`：删除旧 `EvaluateAsync`、建议提示词及其解析方法，保留配置、HTTP 请求和 `AnalyzeMentionAsync`。
- 调整共用提示词：`description` 始终输出可独立使用的客观中文摘要，即使 `shouldCreate=false` 也不输出“无需处理”等审核式文字；JSON 协议和自动创建判断不变。

### 2. 改造手动转待办与装配

- 修改 `MessageFeedViewModel.cs`：删除建议依赖、命令、状态、分析方法及列表项 `Suggestion`；注入窄 `IMessageActionModel`。
- `ConvertToTodoAsync` 先读取原消息并检查已有待办：已有待办直接打开且不分析；源消息缺失仍走原服务结果。新待办在 SQLite 事务外调用模型，再把可空 `Description` 和独立 `AgentPriority` 传给原 `CreateFromMessageAsync`。
- 仅捕获模型分析异常并回退，不改变待办创建异常和外部取消语义；状态文本区分正常创建与模型失败后的默认创建。
- 修改 `TodoFeatureCoordinator.cs`，删除建议 Agent/仓储装配，改为把现有模型实例传给消息流；`MainWindow` 的启动配置和自动管线委托继续复用。
- 修改 `MessageFeedView.xaml`，删除建议面板和“AI 建议”按钮，保留“转为待办”入口。

### 3. 仅在手动点击时持久化 Agent 评分

- `CreateTodoFromMessageRequest` 增加独立可空 `AgentPriority`，不复用普通 `Priority`，避免其他调用方修改待办优先级时意外改写消息评分。
- 在 `UrgencyRanker` 增加手动 Agent 评分工厂，固定映射 P0/P1/P2/P3 为 90/75/50/20，符合现有优先级阈值；原因明确标记为 Agent 手动转待办分析。
- 将紧急度写入逻辑复用到 `SqliteMessageRepository`，供原自动工作单元和手动 `SqliteTodoUnitOfWork` 共用。
- `SqliteTodoUnitOfWork` 在事务内复查已有待办后：先追加 Agent `urgency_scores`，再读取最新优先级并创建待办和提醒。待办或提醒失败时评分一并回滚；已有待办在写评分前返回。
- 不修改 `MessageCapturePipeline` 的自动评分构造，自动消息仍保存本地 `UrgencyScore`。

### 4. 移除旧建议评分来源

- 将 `GetEffectivePrioritiesAsync` 从旧建议仓储迁到 `IMessageRepository` / `SqliteMessageRepository`，统一读取最新 `urgency_scores`（按 ID 倒序），缺失时为 P3。这样普通消息显示本地评分，只有手动点击成功分析的消息显示最新 Agent 评分。
- `MessageFeedViewModel.ReplaceItemsAsync` 改从消息仓储读取优先级，不再加载建议记录。
- `SqliteDatabaseInitializer` 停止为新库创建 `todo_suggestions`；不 DROP 旧表、不删除历史数据，旧库升级后只是不再读写该表。

### 5. 更新回归测试

修改 `tests/WechatDashboard.Tests/Program.cs`，保留当前未提交的微信 refermsg 测试：

- 删除旧建议 Agent、审核流程、上下文澄清测试和旧 Fake；DeepSeek 配置测试改走保留的摘要入口。
- 更新消息流 ViewModel 构造和有效优先级断言；保留自动管线“Agent 不改写自动消息流本地评分”的现有断言。
- 新增手动转待办测试：非 `@我` 消息成功生成 Agent 优先级/摘要并覆盖消息流评分；`shouldCreate=false` 仍创建；模型异常回退本地评分；空白说明使用默认文本但 Agent 优先级仍生效；已有待办不重复分析；普通 `Priority` 不写消息评分；事务失败时 Agent 评分回滚；消息流原文保持不变。
- 保留并运行自动待办、石化通引用和微信 refermsg 回归。

### 6. 同步现有说明

更新 `README.md`、`Agent.md`、`.trae/documents/deepseek-model-config.md` 和 `.trae/documents/model-message-content-analysis.md`，删除手动建议、审核、澄清及旧建议表描述，明确“自动消息流评分保持本地；只有手动点击成功分析时才写入 Agent 评分”。

## 验证

1. 搜索 `TodoSuggestion`、`AI 建议`、`EvaluateAsync` 和 `todo_suggestions`，确认除旧表兼容说明外无生产代码残留。
2. 执行 `dotnet build WechatDashboard.sln --configuration Release --no-restore -m:1 -v minimal`，确认 0 编译错误。
3. 执行完整 .NET 测试 runner，重点核对手动转待办、自动评分隔离、DeepSeek 协议、评分回滚和本地回退；既有 `MessageFeedViewModel` 并发竞态若仍失败需单独注明。
4. 执行 `git diff --check` 并审查最终改动，确保不丢失未提交的微信 refermsg 修改，不包含数据库或敏感数据。
