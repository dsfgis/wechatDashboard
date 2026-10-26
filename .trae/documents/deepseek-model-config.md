# DeepSeek 模型配置接入计划

## Context

项目已有“消息流 → 转为待办 → OpenAI 兼容 Chat Completions 接口”和自动待办分析调用链。模型最初只能在启动时从三个环境变量读取 Endpoint、Model 和 API Key，没有模型配置界面。用户希望固定使用 DeepSeek，在界面填写 API Key 并保存，重启应用后即可调用大模型。

采用最小接入方式：内置 DeepSeek 接口和 `deepseek-flash` 模型，界面只管理 API Key；复用现有 Agent、HTTP 适配器和待办创建逻辑，不增加模型选择、连接测试或运行时热更新。

## 实施方案

### 1. 本地加密保存 API Key

- 新增 `src/WechatDashboard.Infrastructure/Agent/ProtectedDeepSeekApiKeyStore.cs`，提供同步的读取、保存和清除方法，便于主窗口在创建模型前完成加载。
- 使用 Windows DPAPI `CurrentUser` 和独立 entropy 加密 UTF-8 密钥；密文单独保存，不写入 SQLite，不在界面、日志或异常中回显明文。
- 保存时校验非空和合理长度，通过同目录临时文件原子替换正式文件，并清零临时明文字节；损坏或无法解密时返回不包含密钥的中文错误。
- 在 `src/WechatDashboard.Infrastructure/Capture/ProjectToolPaths.cs` 增加密钥路径：`%LocalAppData%\WechatDashboard\Agent\protected-deepseek-api-key.bin`。

### 2. 启动时创建 DeepSeek 模型

- 修改 `src/WechatDashboard.Infrastructure/Agent/OpenAiCompatibleMessageActionModel.cs`：
  - 内置 `https://api.deepseek.com/chat/completions` 和 `deepseek-flash`。
  - 增加“本机已保存 Key 优先、否则沿用原三个 `WECHATDASHBOARD_AGENT_*` 环境变量”的装配入口。
  - 仅内置 DeepSeek 配置在请求中加入 `thinking.type=disabled`，避免默认思考过程耗尽现有 400 token 的短 JSON 响应限制；环境变量驱动的其他 OpenAI 兼容请求保持不变。
  - 未配置时的提示增加“模型配置”页引导，现有鉴权、请求、响应解析和错误处理继续复用。
- 修改 `src/WechatDashboard.App/Services/TodoFeatureCoordinator.cs`，通过构造函数接收 `IMessageActionModel`，不再自行从环境变量创建模型。
- 修改 `src/WechatDashboard.App/MainWindow.xaml.cs`：在创建 `TodoFeatureCoordinator` 前读取密钥并创建模型。密钥读取失败不阻断其他页面，在模型配置页显示重新保存提示，并保留环境变量回退。

### 3. 增加模型配置页

- 修改 `src/WechatDashboard.App/MainWindow.xaml`：
  - 侧栏新增“模型配置”，使用新的 Tab 索引 9，不改变现有页面索引。
  - 页面显示 DeepSeek / `deepseek-flash` 的只读说明、API Key `PasswordBox`、“保存配置”和“清除本机密钥”按钮，以及状态文本。
  - 模型页隐藏顶部采集操作按钮。
- 在 `MainWindow.xaml.cs` 增加保存、清除事件和状态属性：
  - 启动时只显示“已保存/未保存/读取失败”，绝不把解密后的 Key 填回输入框。
  - 保存成功后清空 `PasswordBox`，提示“已加密保存，重启后生效”；保存只代表已持久化，不宣称密钥有效或连接成功。
  - 空输入不覆盖旧密钥；清除是独立操作，提示重启后将回退到环境变量配置（如有）。
  - 本次运行始终沿用启动时的模型实例，不增加热更新或“测试连接”按钮。

### 4. 回归测试与现有说明

- 修改 `tests/WechatDashboard.Tests/Program.cs`：
  - 覆盖 API Key 的 DPAPI 保存/读取、密文不包含明文、缺失文件、损坏文件和清除行为。
  - 使用假 `HttpMessageHandler` 验证 DeepSeek 地址、`deepseek-flash`、Bearer Key、JSON Output 和关闭思考的请求参数；验证无本机 Key 时仍可使用原环境变量配置，测试不访问真实 API。
- 修改现有 `README.md` 的 Agent 配置段落，说明界面保存、重启生效、DPAPI 文件位置和环境变量回退，删除“密钥仅从运行环境读取”的旧描述。
- 所有新增和修改的代码使用必要的中文注释，新增代码注释率保持 20% 以上，不改动无关代码。

## 验证

1. 执行 Release 还原、构建和现有自定义测试 runner，确保新增及原回归测试全部通过。
2. 启动应用，在“模型配置”填写有效 DeepSeek API Key 并保存；确认输入框被清空、密文文件存在且不含明文、SQLite 中没有 API Key。
3. 保存后不重启，确认本次运行仍使用启动时配置；重启后确认页面只显示已保存状态且不回显密钥。
4. 在“消息流”点击“转为待办”，使用真实 Key 做一次人工验收，确认 DeepSeek 返回的说明和优先级写入新待办，且消息流评分同步更新；无有效 Key 时不执行真实外部请求。
5. 替换 Key 后确认重启才使用新值；清除本机 Key 后确认重启时回退到原环境变量配置（如有）。

## 后续补充（2026-10-26）

- 模型配置页已增加“分析消息内容”开关。开关持久化到 SQLite 并立即生效，不改变本文规定的 API Key“保存或清除后重启生效”语义。
- 开启后，新的 `@我` / `@所有人` 消息由 Agent 单次返回 `shouldCreate`、优先级和内容摘要；明确判定无关、低优先级或无需行动时只入库不建待办。
- 自动分析开关关闭或模型失败时仍按原规则创建待办，并保留本地优先级及默认说明；模型故障不会被当成“无需创建”。
- 自动分析不改写消息流本地评分。旧“AI 建议”、审核和澄清流程已删除；手动“转为待办”复用同一 Agent 协议，只发送当前消息，已有待办直接打开且不重复分析。
- 手动点击不受自动开关或提及识别限制，即使 `shouldCreate=false` 也创建；模型失败仍按默认说明和本地优先级创建。
- 仅手动转换成功分析时，Agent 优先级才同时写入待办和该消息的消息流评分，二者与首个提醒在同一事务提交。Agent 摘要只进入待办说明，不改写消息原文。
- 旧库中的 `todo_suggestions` 遗留表和数据保留但不再读写，新库不再创建该表。石化通引用正文会在采集时合并到当前消息，截止时间仍由本地规则识别会议开始时间或本周日期。
- 最新协议、事务边界、引用消息、截止时间和降级规则见 `.trae/documents/model-message-content-analysis.md`。
