# 微信消息监控桌面软件设计文档

## 1. 背景与目标

用户微信群数量多，项目消息分散，容易漏掉群内 `@我`、紧急事项和跨项目待办。本系统面向 Windows 桌面环境，使用 WPF 技术栈构建一个本地消息监控与分析工具，将微信消息采集、待办提取、项目分类、紧急度排序和多维统计看板整合在一个桌面应用中。

核心目标：

1. 监控用户授权范围内的微信消息，优先发现群聊中 `@我` 的消息。
2. 将所有 `@我` 消息自动加入 Todo 清单的“待办理”状态。
3. 将非 `@我` 消息按项目、类别和规则进行归类。
4. 对消息和待办按紧急程度排序，帮助用户先处理高优先级事项。
5. 提供按项目、时间、类别、状态、紧急程度等维度的统计看板。
6. 数据默认保存在本机 SQLite 数据库中，避免默认上传敏感聊天内容。

范围说明：

1. 本文中的“所有微信消息”指当前用户授权、且能通过合规采集适配器获取到的消息集合，包括本机微信已同步或已缓存到本地的数据。
2. 本文中的“所有 `@我` 消息”指已采集消息中命中 `@我` 识别规则的全部消息。
3. 系统不承诺突破微信个人版未公开接口或安全机制来读取不可访问的全量历史消息。

## 2. 合规边界与设计原则

微信个人版没有公开的全量消息读取接口，因此系统必须将消息采集设计为可插拔适配器。当前主线从“屏幕 OCR 读取”调整为“本地文件或本地消息库读取优先，OCR 兜底”，但仍必须坚持用户授权、只读、本地执行和可审计的边界。

设计边界：

1. 系统只处理当前用户在本机、本人账号、明确授权范围内的消息。
2. 默认优先采用本地文件、本地消息库只读快照、外部导出 JSONL、通知监听、用户主动导入、企业合规接口等方式采集消息。
3. 不设计微信账号密码采集，不保存微信登录凭据。
4. 微信登录密码不能用于解密本地数据库，系统不接收、不保存、不尝试使用登录密码。
5. WPF 主程序不内置 Hook、注入或不可审计的逆向逻辑；如需读取微信本地加密数据，只允许在用户显式授权后由隔离的本地读取器只读扫描 `Weixin.exe`，并通过数据库首页 HMAC 校验确认候选密钥。
6. 数据库密钥、salt、内存地址和原始聊天正文不得写入应用日志、诊断摘要、项目目录或 Git 仓库。
7. 涉及群聊和个人消息的内容默认只保存在本地；如后续接入 AI 云服务，必须提供显式开关、脱敏策略和风险提示。

产品能力分级：

| 等级 | 能力 | 说明 |
| --- | --- | --- |
| MVP | 手动导入、JSONL 导入、本地导出文件采集、重点消息 Todo 化 | 先验证消息流水线、去重、`@我` 和看板闭环 |
| 标准版 | 微信本地文件/本地库采集、规则配置、消息补偿扫描 | 微信最小化或被遮挡时仍可采集本机已缓存消息 |
| 企业版 | 对接企业微信、机器人、组织批准的数据接口 | 适合公司内部项目消息治理 |

## 3. 总体架构

系统采用 WPF + MVVM + 本地 SQLite 的桌面应用架构，分为表现层、应用服务层、领域层和基础设施层。

```mermaid
flowchart LR
    A["微信本地文件 / 本地消息库 / 桌面通知 / 导入文件 / 企业接口"] --> B["Message Capture Adapters"]
    B --> C["Message Normalizer"]
    C --> D["SQLite Repository"]
    C --> E["Mention Detector"]
    C --> F["Project Classifier"]
    C --> G["Urgency Ranker"]
    E --> H["Todo Service"]
    F --> I["Analytics Service"]
    G --> H
    D --> I
    H --> D
    I --> J["WPF Dashboard"]
    H --> K["WPF Todo View"]
```

分层说明：

| 层级 | 职责 | 典型模块 |
| --- | --- | --- |
| Presentation | WPF 页面、控件、ViewModel、用户交互 | TodoView、MessageFeedView、DashboardView、SettingsView |
| Application | 编排业务流程和后台任务 | MessagePipelineService、TodoService、AnalyticsService |
| Domain | 业务模型与规则 | Message、TodoItem、Project、UrgencyScore、ClassificationResult |
| Infrastructure | SQLite、采集适配器、配置、日志、通知 | SqliteRepository、WeChatLocalDatabaseAdapter、WeChatUiaOcrAdapter、NotificationAdapter |

推荐技术栈：

| 类别 | 方案 |
| --- | --- |
| UI | WPF、MVVM、CommunityToolkit.Mvvm |
| 后台任务 | .NET Generic Host、BackgroundService、Channel 队列 |
| 数据库 | SQLite、Microsoft.Data.Sqlite、EF Core SQLite 或 Dapper |
| 图表 | LiveCharts2、OxyPlot 或同类 WPF 图表库 |
| 日志 | Serilog，本地滚动日志文件 |
| 配置 | appsettings.json + 用户配置表 |
| 隐私保护 | Windows DPAPI 加密敏感配置；可选 SQLCipher 保护本地数据库 |

## 4. 核心功能设计

### 4.1 消息采集

消息采集层使用 Adapter 模式，避免把系统绑定到单一采集方式。

接口设计：

```csharp
public interface IMessageCaptureAdapter
{
    string Name { get; }
    Task<IReadOnlyList<CapturedMessage>> CaptureAsync(CaptureContext context, CancellationToken cancellationToken);
}
```

适配器类型：

| 适配器 | 用途 | 备注 |
| --- | --- | --- |
| WeChatLocalExportAdapter | 读取本地导出工具生成的 JSONL 或 JSON 消息文件 | 第一优先级，风险低，便于快速验证微信最小化采集 |
| WeChatLocalDatabaseAdapter | 只读读取本机微信已缓存的本地消息文件或本地消息库快照 | 标准版目标，窗口最小化、被遮挡不影响采集 |
| WeChatUiaOcrAdapter | 通过 Windows UI Automation + OCR 读取当前可见微信窗口文本 | 作为诊断和兜底，不作为微信主采集路径 |
| WindowsNotificationAdapter | 捕获系统通知中的新消息摘要 | 覆盖新消息提醒，但内容可能不完整 |
| ManualImportAdapter | 支持用户导入 CSV、文本、截图 OCR 结果或企业导出的消息文件 | 适合补录历史消息 |
| EnterpriseApiAdapter | 对接组织批准的企业微信或机器人消息接口 | 适合企业环境，权限清晰 |

采集策略：

1. 微信来源默认优先使用 `WeChatLocalExportAdapter` 或 `WeChatLocalDatabaseAdapter`，`WeChatUiaOcrAdapter` 只作为诊断和兜底。
2. 通过 `source_message_key` 做去重，防止同一条消息重复入库。
3. 保存采集偏移量 `processing_offsets`，重启后从上次位置继续。
4. 采集异常只影响对应适配器，不阻塞其他适配器和 UI。
5. 本地文件采集默认只读，先复制或读取快照，不修改微信原始文件。
6. UI Automation + OCR 只读取当前用户可见窗口内容，不做隐藏式监听。

#### 4.1.1 微信本地文件/本地库采集

该方案是后续微信采集主线。它不依赖屏幕像素，因此微信窗口最小化、被其他窗口遮挡或切到后台时，仍有机会读取本机已经接收并缓存到本地的消息。

阶段划分：

1. **本地导出桥接阶段**：WPF 配置本地导出工具路径或导出目录，工具输出 JSONL/JSON，系统通过 `WeChatLocalExportAdapter` 转换为 `CapturedMessage`。该阶段不在主程序中实现微信库解析，优先验证增量采集、去重、`@我` Todo 和看板刷新。
2. **本地库只读阶段**：通过隔离的 `wechat-local-reader` 进程读取加密数据库并输出结构化 JSON。WPF 只负责启动工具、传入 offset 和解析结果，不在主进程保存或打印数据库密钥。读取器使用临时解密副本，不修改微信原始文件。
3. **多源本地采集阶段**：沉淀 `ExternalCommandCaptureAdapter`、`LocalFileWatchCaptureAdapter`、`LocalDatabaseCaptureAdapter`，让飞书、石化通、钉钉也能按本地文件、外部命令、官方 API 或可见窗口等方式接入。

本地消息映射：

| 微信本地数据 | 系统字段 | 说明 |
| --- | --- | --- |
| 消息唯一 ID 或组合键 | SourceMessageKey | 跨重启去重的核心字段 |
| 会话 ID | ChatId | 群聊或单聊稳定标识 |
| 会话名称 | ChatName | 用于项目分类和看板展示 |
| 发送人 ID 或昵称 | SenderName | 群聊中尽量保留真实发送人 |
| 消息正文或摘要 | Content | 文本、引用摘要、文件标题、链接标题 |
| 发送时间 | SentAt | 用于排序、统计和增量 offset |
| 消息类型 | MessageType | Text、Image、File、Link、System |

非文本消息第一阶段处理为摘要：图片记为 `[图片]`，文件记录文件名和大小，链接记录标题和 URL，语音和视频先记录占位摘要，撤回和系统通知默认不生成 Todo。

增量采集要求：

1. 优先使用微信本地消息唯一 ID。
2. 没有唯一 ID 时，使用 `账号 + 会话 ID + 发送时间 + 发送人 + 内容 hash` 生成稳定键。
3. offset 至少保存 `source`、账号标识、最后消息时间、最后消息键和按会话推进的位置。
4. 同一导出文件重复读取时不能重复入库。
5. 外部工具失败、输出格式错误、版本不兼容、目录不存在等问题必须显示在采集诊断页，不导致 WPF 崩溃。

当前默认本地导出目录：

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox\WeChatLocalExport
```

第一阶段已支持 JSONL/JSON 中常见字段名映射，例如 `msgId`、`messageId`、`chatId`、`talker`、`roomName`、`senderName`、`sender`、`content`、`message`、`createTime`、`timestamp`、`msgType`。

当前真实环境已定位：

```text
微信版本：4.1.10.31
数据目录：D:\cache\xwechat_files\dsfgis_84f8\db_storage
主消息库：D:\cache\xwechat_files\dsfgis_84f8\db_storage\message\message_0.db
读取工具：%LOCALAPPDATA%\WechatDashboard\tools\wechat-local-reader\wechat-local-reader.exe
```

本地库文件是加密格式。首次初始化必须由用户明确授权读取正在运行的 `Weixin.exe` 进程内存以获取本机数据库密钥。密钥只保存在应用本机工具目录的配置文件中，不进入应用日志、SQLite 消息库、项目目录或 Git 仓库。初始化完成前，`WeChat.LocalDatabase` 保持禁用。

#### 4.1.2 微信 4.x 密钥初始化

本地读取器提供独立 `init` 子命令，初始化过程与 WPF 消息采集进程隔离：

1. 枚举本机正在运行的 `Weixin.exe`，只申请 `PROCESS_VM_READ | PROCESS_QUERY_INFORMATION`。
2. 分块读取已提交、可读、私有内存，不写入或修改微信进程。
3. 识别微信 4.x 的 32 字节密钥结构、无包装 ASCII/UTF-16 十六进制候选，并允许容量字段随微信版本变化。
4. 先把候选作为已派生页密钥直接验证，再按微信 4.x 参数执行 `PBKDF2-HMAC-SHA512` 256000 次派生并验证。
5. 使用数据库第一页的 HMAC-SHA512 校验确认密钥，未经校验的候选不得保存。
6. 为 `session`、`contact` 和 `message` 数据库生成独立派生密钥映射，原子写入本机 `all_keys.json`。
7. 生成的 `config.json` 和 `all_keys.json` 位于 `%LOCALAPPDATA%\WechatDashboard\tools\wechat-local-reader`，并限制为当前 Windows 用户访问。
8. 初始化命令只输出状态、数据库数量、候选数量和耗时，不输出密钥、salt、内存地址或聊天内容。

当前验证状态（2026-06-06）：

1. 已确认微信版本为 `4.1.10.31`，数据库目录和消息库持续更新。
2. 旧版 `x'<64hex_key><32hex_salt>'` 文本模式未在五个微信进程中出现。
3. 固定容量 `0x2F` 的微信 4.x 指针结构产生候选，但没有候选通过数据库校验。
4. 无包装 ASCII/UTF-16 十六进制候选也没有通过直接页密钥或原始口令校验。
5. 读取器已改为支持可变容量结构、直接页密钥和原始口令双路径；真实进程验证仍待下一次具备管理员读取权限时完成。
6. 在密钥校验成功、配置生成且真实消息读取测试通过前，系统不能宣称已经支持后台读取微信消息。

### 4.2 消息标准化

不同来源的消息统一转成内部模型：

```csharp
public sealed class Message
{
    public long Id { get; init; }
    public string Source { get; init; } = "";
    public string SourceMessageKey { get; init; } = "";
    public string ChatId { get; init; } = "";
    public string ChatName { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string Content { get; init; } = "";
    public DateTimeOffset SentAt { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public MessageType MessageType { get; init; }
}
```

标准化规则：

1. 统一时间格式，全部存储为 UTC 时间戳，同时保留本地展示时区。
2. 统一群名、发送人、消息内容、来源和消息类型。
3. 对图片、文件、链接等非文本消息保存摘要和元数据，不默认保存二进制内容。
4. 对空白、撤回、系统提示等消息打类型标签，便于统计时过滤。

### 4.3 `@我` 识别与 Todo 自动生成

`@我` 识别基于别名集合、群内昵称和微信界面可见提示。

识别信号：

1. 消息内容包含 `@我`、`@你`、`@当前昵称`、`@姓名`、`@英文名` 等别名。
2. 微信窗口或通知中出现“有人@我”的提示。
3. 重点群配置中指定“所有提到某关键字均视为待办”。
4. 发送人是重点联系人且内容包含动作词，例如“请处理”“帮看下”“今天给反馈”。

Todo 生成规则：

1. 命中 `@我` 的消息自动创建 Todo，状态为 `Pending`。
2. 如果同一 `source_message_key` 已创建 Todo，则不重复创建。
3. Todo 标题从消息内容自动截取，默认长度不超过 80 字。
4. Todo 保留原始消息链接、群名、发送人、上下文消息 ID、项目分类和紧急度。
5. 用户可以手动修改 Todo 标题、项目、截止时间、备注和状态。

Todo 状态：

| 状态 | 含义 |
| --- | --- |
| Pending | 待办理 |
| InProgress | 处理中 |
| Waiting | 等待他人 |
| Done | 已完成 |
| Ignored | 已忽略 |

### 4.4 项目分类

项目分类采用“规则优先、模型辅助、人工修正闭环”的策略。

分类信号：

| 信号 | 示例 | 权重 |
| --- | --- | --- |
| 群聊映射 | 某微信群固定属于 A 项目 | 高 |
| 关键词 | 项目代号、客户名、系统名、需求编号 | 高 |
| 发送人 | 项目经理、客户接口人、核心开发 | 中 |
| 消息上下文 | 同一会话最近消息所属项目 | 中 |
| 用户修正 | 用户手动改过分类 | 最高 |

分类流程：

1. 先检查群聊与项目的固定映射。
2. 再匹配项目关键词、别名、客户名、系统名称。
3. 对无法确定的消息进入 `Unclassified`。
4. 用户在界面修正分类后，系统记录为规则候选。
5. 可选接入本地或云端 AI 分类器，但默认不开启云端发送。

分类输出：

```csharp
public sealed class ClassificationResult
{
    public long MessageId { get; init; }
    public long ProjectId { get; init; }
    public string Category { get; init; } = "";
    public decimal Confidence { get; init; }
    public string Reason { get; init; } = "";
    public string Classifier { get; init; } = "Rules";
}
```

类别建议：

| 类别 | 说明 |
| --- | --- |
| Requirement | 需求、变更、业务诉求 |
| Incident | 故障、线上问题、阻塞 |
| Meeting | 会议、评审、同步 |
| Delivery | 交付、上线、验收 |
| Question | 咨询、答疑 |
| FYI | 通知、同步信息 |

### 4.5 紧急程度排序

紧急度计算输出 0 到 100 分，并映射为 P0 到 P3。

评分信号：

| 信号 | 加分示例 |
| --- | --- |
| `@我` | +30 |
| 包含紧急词 | “紧急”“马上”“今天”“阻塞”“线上故障” |
| 明确截止时间 | “下班前”“今天 18 点”“明早前” |
| 重点项目 | 核心项目、上线项目 |
| 重点联系人 | 领导、客户、项目经理 |
| 消息类型 | 故障类、交付类优先 |
| 时间衰减 | 超过 SLA 未处理继续升权 |

优先级映射：

| 分数 | 优先级 | 处理建议 |
| --- | --- | --- |
| 85-100 | P0 | 立即处理 |
| 65-84 | P1 | 当天优先处理 |
| 40-64 | P2 | 纳入今日或近期计划 |
| 0-39 | P3 | 低优先级关注 |

排序规则：

1. Todo 默认按 `priority DESC, due_at ASC, captured_at DESC` 排序。
2. P0 消息触发桌面提醒和托盘高亮。
3. 同一项目内优先显示未完成、超时、`@我` 的事项。
4. 用户手动调整优先级后，手动值优先于自动评分。

### 4.6 多维统计看板

看板用于快速回答“哪些项目最忙、哪些事项最急、哪些消息还没处理、最近趋势如何”。

主要视图：

| 看板 | 指标 |
| --- | --- |
| 今日概览 | 今日消息数、`@我` 数、待办理数、P0/P1 数、已完成数 |
| 项目看板 | 各项目消息量、待办量、未完成量、紧急事项数 |
| 时间趋势 | 按小时、日、周统计消息和待办趋势 |
| 类别分布 | 需求、故障、会议、交付、咨询、通知占比 |
| SLA 看板 | 超时待办、平均响应时间、平均完成时间 |
| 未分类消息 | 无法归类消息量、建议新增规则 |

筛选维度：

1. 项目。
2. 时间范围。
3. 群聊。
4. 发送人。
5. 类别。
6. 优先级。
7. Todo 状态。
8. 是否 `@我`。

## 5. WPF 界面设计

主窗口采用左侧导航 + 顶部状态栏 + 右侧内容区布局。

导航项：

| 页面 | 说明 |
| --- | --- |
| 待办理 | 展示自动生成和手动创建的 Todo |
| 消息流 | 查看按时间排序的消息，支持筛选和搜索 |
| 项目 | 按项目聚合消息、待办和风险 |
| 看板 | 多维统计和图表 |
| 规则 | 配置项目规则、关键词、别名、重点联系人 |
| 采集诊断 | 查看适配器状态、最近采集时间、错误日志 |
| 设置 | 隐私、数据库、提醒、备份、AI 开关 |

关键交互：

1. 双击 Todo 打开详情面板，展示原消息、上下文、项目、紧急度原因和操作历史。
2. 消息流支持右键“转为待办”“加入项目规则”“忽略类似消息”。
3. 项目页支持拖拽调整 Todo 状态。
4. 规则页支持测试规则，输入一条消息后展示分类和紧急度原因。
5. 采集诊断页展示每个 Adapter 的启用状态、最近成功时间、失败次数和错误摘要。

ViewModel 建议：

| ViewModel | 职责 |
| --- | --- |
| ShellViewModel | 导航、全局状态、托盘提醒 |
| TodoListViewModel | Todo 查询、筛选、状态变更 |
| MessageFeedViewModel | 消息列表、搜索、转待办 |
| DashboardViewModel | 聚合指标和图表数据 |
| RuleEditorViewModel | 项目规则、关键词、别名配置 |
| CaptureDiagnosticsViewModel | 采集适配器健康状态 |

## 6. SQLite 数据模型

数据库文件默认存放在用户应用数据目录，例如 `%LOCALAPPDATA%\WechatDashboard\data\wechat-dashboard.db`。开发环境可通过配置覆盖。

基础设置：

1. 启用 WAL 模式提升读写并发。
2. 使用 schema version 表管理迁移。
3. 对消息时间、项目、状态、优先级建立索引。
4. 对消息内容启用 FTS5 全文检索，用于快速搜索。

核心表：

```sql
CREATE TABLE chat_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    source_chat_key TEXT NOT NULL,
    name TEXT NOT NULL,
    chat_type TEXT NOT NULL,
    project_id INTEGER NULL,
    is_priority INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(source, source_chat_key)
);

CREATE TABLE messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source TEXT NOT NULL,
    source_message_key TEXT NOT NULL,
    chat_session_id INTEGER NOT NULL,
    sender_name TEXT NOT NULL,
    content TEXT NOT NULL,
    message_type TEXT NOT NULL,
    sent_at TEXT NOT NULL,
    captured_at TEXT NOT NULL,
    is_mention_me INTEGER NOT NULL DEFAULT 0,
    raw_excerpt TEXT NULL,
    FOREIGN KEY(chat_session_id) REFERENCES chat_sessions(id),
    UNIQUE(source, source_message_key)
);

CREATE TABLE projects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    code TEXT NULL,
    color TEXT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE message_classifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id INTEGER NOT NULL,
    project_id INTEGER NULL,
    category TEXT NOT NULL,
    confidence REAL NOT NULL,
    reason TEXT NOT NULL,
    classifier TEXT NOT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id),
    FOREIGN KEY(project_id) REFERENCES projects(id)
);

CREATE TABLE urgency_scores (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id INTEGER NOT NULL,
    score INTEGER NOT NULL,
    priority TEXT NOT NULL,
    reason TEXT NOT NULL,
    calculated_at TEXT NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id)
);

CREATE TABLE todo_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    source_message_id INTEGER NULL,
    project_id INTEGER NULL,
    title TEXT NOT NULL,
    description TEXT NULL,
    status TEXT NOT NULL,
    priority TEXT NOT NULL,
    due_at TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    completed_at TEXT NULL,
    is_auto_created INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY(source_message_id) REFERENCES messages(id),
    FOREIGN KEY(project_id) REFERENCES projects(id)
);

CREATE TABLE project_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id INTEGER NOT NULL,
    rule_type TEXT NOT NULL,
    pattern TEXT NOT NULL,
    weight INTEGER NOT NULL DEFAULT 10,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    FOREIGN KEY(project_id) REFERENCES projects(id)
);

CREATE TABLE user_aliases (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    alias TEXT NOT NULL UNIQUE,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL
);

CREATE TABLE processing_offsets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    adapter_name TEXT NOT NULL UNIQUE,
    offset_value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE audit_logs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_type TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    action TEXT NOT NULL,
    detail TEXT NULL,
    created_at TEXT NOT NULL
);
```

建议索引：

```sql
CREATE INDEX idx_messages_sent_at ON messages(sent_at);
CREATE INDEX idx_messages_chat_session ON messages(chat_session_id);
CREATE INDEX idx_messages_mention ON messages(is_mention_me, sent_at);
CREATE INDEX idx_todo_status_priority ON todo_items(status, priority, due_at);
CREATE INDEX idx_classification_project ON message_classifications(project_id, category);
```

## 7. 消息处理流水线

后台流水线按以下顺序处理：

1. `CaptureWorker` 从启用的采集适配器拉取消息。
2. `MessageNormalizer` 转换为统一消息模型。
3. `DeduplicationService` 按来源和消息 Key 去重。
4. `MessageRepository` 保存消息。
5. `MentionDetector` 判断是否 `@我`。
6. `ProjectClassifier` 计算项目和类别。
7. `UrgencyRanker` 计算紧急度。
8. `TodoService` 为 `@我` 消息创建待办。
9. `AnalyticsCacheService` 更新看板聚合缓存。
10. `NotificationService` 对 P0/P1 待办发出桌面提醒。

失败处理：

1. 单条消息处理失败写入日志，不中断整个批次。
2. 采集适配器失败时记录健康状态，并使用指数退避重试。
3. 数据库写入失败时暂停后台采集，提示用户检查磁盘和权限。
4. 分类或紧急度计算失败时使用默认分类 `Unclassified` 和默认优先级 `P3`。

## 8. 隐私、安全与数据治理

隐私策略：

1. 默认本地存储，不上传微信消息内容。
2. 首次启动展示数据采集说明，用户确认后才启用监控。
3. 支持为不同群聊设置“忽略采集”“仅采集 @我”“完整采集”。
4. 支持按时间范围删除消息、清空项目数据、导出本地数据。
5. 支持敏感关键词脱敏，例如手机号、身份证号、邮箱。

本地安全：

1. 数据库默认放在当前 Windows 用户目录，不放在公共目录。
2. 使用 DPAPI 加密敏感配置。
3. 可选启用 SQLCipher 或字段级加密保护聊天内容。
4. 日志不记录完整消息正文，只记录消息 ID、来源和错误摘要。

## 9. 性能与可靠性

性能目标：

| 指标 | 目标 |
| --- | --- |
| 普通消息入库延迟 | 10 秒内 |
| `@我` Todo 生成延迟 | 10 秒内 |
| 消息列表分页加载 | 500ms 内返回首屏 |
| 看板刷新 | 2 秒内完成 |
| 单库消息容量 | 支持 100 万条级别本地消息 |

可靠性设计：

1. WPF UI 不直接执行采集和分类逻辑，避免界面卡顿。
2. 后台服务使用 Channel 队列削峰，数据库批量写入。
3. 消息列表采用分页和虚拟化控件。
4. 看板优先读聚合缓存，避免每次全表扫描。
5. 定期执行 SQLite `PRAGMA integrity_check`，发现异常提示备份和修复。

## 10. 测试方案

单元测试：

1. `MentionDetector`：覆盖中文昵称、英文名、`@你`、误匹配。
2. `ProjectClassifier`：覆盖群聊映射、关键词、用户修正规则。
3. `UrgencyRanker`：覆盖紧急词、截止时间、重点联系人和时间衰减。
4. `TodoService`：覆盖自动创建、去重、状态更新。

集成测试：

1. SQLite 初始化、迁移、索引和 CRUD。
2. 消息处理流水线端到端。
3. 采集适配器异常时不影响其他适配器。
4. 看板聚合数据准确性。

UI 测试：

1. 待办理页面筛选、排序、状态修改。
2. 消息流搜索、转待办、归类操作。
3. 规则测试输入和结果展示。
4. 采集诊断页面状态刷新。

## 11. 迭代计划

第一阶段：本地基础能力

1. 创建 WPF Shell、MVVM 基础结构和 SQLite 初始化。
2. 实现消息手动导入、消息列表、Todo 自动生成。
3. 实现 `@我` 识别、基础项目规则和紧急度评分。
4. 实现待办理页面和今日概览看板。

第二阶段：微信本地采集和实时监控能力

1. 实现 `WeChatLocalExportAdapter`，先读取本地 JSONL/JSON 导出消息。
2. 验证微信窗口最小化时，本机新增消息是否能通过本地导出链路进入 SQLite 和 Todo。
3. 抽象 `IWeChatLocalStoreReader`，准备本地库只读快照读取能力。
4. 保留 `WeChatUiaOcrAdapter` 作为诊断和兜底。
5. 增加采集诊断页面、失败重试、桌面通知和托盘状态。

第三阶段：分类和看板增强

1. 增加项目规则编辑器。
2. 增加未分类消息修正闭环。
3. 增加项目、时间、类别、SLA 多维看板。
4. 增加全文检索和高级筛选。

第四阶段：隐私、安全和企业扩展

1. 增加数据库加密选项。
2. 增加敏感字段脱敏和数据清理策略。
3. 增加企业微信或组织批准接口适配器。
4. 增加导出报表和备份恢复。

## 12. 主要风险与应对

| 风险 | 影响 | 应对 |
| --- | --- | --- |
| 微信个人版缺少官方全量读取接口 | 无法保证读取所有历史消息 | 采用适配器分级，优先读取本机已缓存消息，保留导入、通知和 OCR 兜底 |
| 微信本地库加密、分片或版本变化 | 本地采集可能失效 | 先采用外部导出桥接和只读快照，诊断页暴露版本、路径和错误 |
| UI Automation 和 OCR 稳定性受窗口状态影响 | 最小化或遮挡时不可用 | OCR 只作为兜底，不作为主采集路径 |
| 群消息涉及隐私 | 合规风险 | 本地存储、显式授权、群级采集开关、脱敏和清理 |
| 分类误判 | 项目统计不准确 | 规则优先、展示分类原因、支持用户修正并沉淀规则 |
| 消息量大导致卡顿 | 用户体验下降 | 后台队列、批量写入、分页、虚拟化、聚合缓存 |
| 紧急度评分不符合个人习惯 | 排序效果差 | 提供权重配置、重点联系人、重点项目和手动优先级覆盖 |

## 13. 验收标准

MVP 验收：

1. 用户可以导入或采集一批微信消息并在消息流中查看。
2. 系统能识别 `@我` 消息，并自动生成 `Pending` Todo。
3. Todo 能按项目、优先级、时间和状态筛选排序。
4. 普通消息能根据规则归类到项目和类别。
5. 看板能展示今日消息数、`@我` 数、待办理数、项目分布和优先级分布。
6. SQLite 数据库能持久保存消息、项目、分类、紧急度和 Todo。
7. 采集失败、分类失败、数据库异常都有可见提示或诊断日志。

正式版验收：

1. 支持至少一种实时采集适配器。
2. 微信窗口最小化或被遮挡时，已缓存到本机的新消息仍能通过本地采集链路进入消息库。
3. 支持项目规则编辑、规则测试和用户修正闭环。
4. 支持 P0/P1 待办桌面提醒。
5. 支持按项目、时间、类别、状态、紧急程度多维统计。
6. 支持群级采集策略、数据删除、导出和备份。

## 14. 推荐项目结构

```text
WechatDashboard/
  src/
    WechatDashboard.App/
      Views/
      ViewModels/
      App.xaml
      MainWindow.xaml
    WechatDashboard.Application/
      Services/
      Pipelines/
      UseCases/
    WechatDashboard.Domain/
      Entities/
      Rules/
      ValueObjects/
    WechatDashboard.Infrastructure/
      Capture/
      Persistence/
      Notifications/
      Configuration/
  tests/
    WechatDashboard.Domain.Tests/
    WechatDashboard.Application.Tests/
    WechatDashboard.Infrastructure.Tests/
  design/
    wechat-message-monitor-wpf-design.md
```

## 15. 结论

该系统应以“本地优先、规则优先、合规采集、可解释排序”为核心。第一版不追求一次性解决所有微信历史消息读取问题，而是先建立稳定的数据模型、待办闭环和看板能力，再通过采集适配器逐步提升实时覆盖率。这样可以在技术风险和合规风险可控的前提下，尽快交付对用户真正有价值的 `@我` 待办理和项目消息看板。
