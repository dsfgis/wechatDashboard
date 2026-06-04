# 消息采集扩展说明

## 当前实现

当前版本已经实现通用采集流水线、本地 JSONL 目录采集器，以及微信桌面端可见窗口的 UI Automation + OCR 轮询采集。

采集目录：

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox
```

WPF 界面点击“采集一次”后，会同时运行默认 JSONL 目录采集源和 `WeChat.WindowText` 可见窗口采集源，把新消息写入 SQLite，并对命中 `@我` 的消息自动创建待办理 Todo。点击“开始微信监听”后，应用会按固定间隔重复运行同一条采集流水线。当前默认 `@我` 别名为 `白驹过隙` 和 `戴少峰`。

默认来源目录：

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox\WeChat
%LOCALAPPDATA%\WechatDashboard\capture-inbox\Feishu
%LOCALAPPDATA%\WechatDashboard\capture-inbox\Shihuatong
%LOCALAPPDATA%\WechatDashboard\capture-inbox\DingTalk
```

## JSONL 消息格式

每一行是一条消息：

```json
{"id":"wx-1","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| id | 否 | 来源软件内的消息 ID；为空时使用文件名和行号 |
| platform | 否 | 来源平台，例如 WeChat、Feishu、Shihuatong、DingTalk |
| chatId | 否 | 会话 ID；为空时使用 chatName |
| chatName | 否 | 群聊或会话名称 |
| senderName | 否 | 发送人 |
| content | 否 | 消息正文 |
| sentAt | 否 | 发送时间，建议 ISO 8601 |
| messageType | 否 | Text、Image、File、Link、System |

## 扩展接口

所有消息来源统一实现：

```csharp
public interface IMessageCaptureAdapter
{
    string Name { get; }

    Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken);
}
```

Adapter 输出 `CapturedMessage`，由 `MessageCapturePipeline` 统一完成：

1. offset 读取。
2. 消息采集。
3. 去重入库。
4. `@我` 识别。
5. 项目分类。
6. 紧急度评分。
7. 自动 Todo 创建。
8. offset 保存。

来源注册由 `CaptureSourceDefinition` 描述：

1. `CaptureAdapterFactory.CreateDefaultJsonlSources(...)` 会为微信、飞书、石化通、钉钉创建 JSONL 目录采集来源。
2. `CaptureAdapterFactory.CreateDefaultLiveSources(...)` 会在 JSONL 来源之外启用 `WeChat.WindowText`，用于桌面端微信可见窗口实时采集。
3. 后续飞书、石化通、钉钉真实监听器应继续使用同一来源定义和 `IMessageCaptureAdapter`。

## 可见窗口文本采集

当前版本还提供了 `WindowTextCaptureAdapter` 的可测试核心。它通过 `IWindowTextSnapshotProvider` 获取用户可见窗口文本快照，再解析形如：

```text
09:20 王经理: @张三 今天下班前处理线上故障
09:21 李工：同步一下接口变更
```

的文本行，输出标准 `CapturedMessage`。

同时支持 UIA 常见的分行结构：

```text
CRM项目群
09:20
王经理
@张三 今天下班前处理线上故障
09:21
李工
同步一下接口变更
```

已实现能力：

1. 按窗口标题关键字过滤快照。
2. 从 `HH:mm 发送人: 内容` 或 `HH:mm 发送人：内容` 解析发送人、内容和发送时间。
3. 从 `时间 / 发送人 / 内容` 或 `发送人 / 时间 / 内容` 分行块解析 UIA 可见消息。
4. 从窗口标题或首个可见标题行推断会话名，例如 `CRM项目群 - 微信` 或窗口正文首行 `CRM项目群` 推断为 `CRM项目群`。
5. 使用窗口标题、会话名和规范化消息内容生成稳定 `SourceMessageKey`。
6. 使用快照 fingerprint 作为 offset，避免重复处理完全相同的窗口文本。

`CaptureAdapterFactory.CreateWeChatWindowTextSource()` 仍返回默认禁用的单独来源定义，供设置页或测试按需启用。WPF 默认实时采集入口使用 `CreateDefaultLiveSources(...)`，会启用微信可见窗口来源。

`WindowsUiAutomationSnapshotProvider` 和 `SystemWindowsAutomationReader` 已实现，可以通过 Windows UI Automation 枚举顶层窗口并聚合子元素文本。`SystemWindowsAutomationReader` 默认使用 Raw View 遍历，以便尽量读取微信自定义控件树中的文本。

实际验证发现，当前微信桌面端可能只向 UIA 暴露窗口标题和按钮文本，例如 `微信 Weixin 微信 最小化 最大化 ...`，不暴露聊天正文。因此 WPF 默认采集入口已经改为 `WindowsOcrWindowTextSnapshotProvider`，它会把 Windows OCR 识别出的当前可见窗口文字放在快照前部，再附加 UIA 文本，并交给 `WindowTextCaptureAdapter` 解析。

当前真实采集边界是“微信桌面端当前可见窗口文本”，不是微信全量历史数据库读取。后续验证重点是：

1. 微信窗口标题是否稳定包含“微信”。
2. UIA 子元素是否暴露群名、发送人、时间和消息正文。
3. 实际文本格式是否符合 `WindowTextCaptureAdapter` 的解析规则。
4. 轮询是否会带来明显桌面卡顿。
5. OCR 识别出的消息行顺序是否适配当前解析规则。
6. 未打开或不在屏幕可见区域的会话不会被采集。

WPF 的“采集诊断”页提供“扫描微信窗口”按钮。该按钮只读取并展示 UIA + OCR 可见文本快照预览，不会写入 SQLite，也不会创建 Todo。它用于验证真实微信窗口的文本格式。当“采集一次”或“开始微信监听”没有采集到消息时，应先查看这里的快照预览，再按真实文本格式扩展解析规则。

## 后续接入建议

| 来源 | 建议 Adapter | 说明 |
| --- | --- | --- |
| 微信 | WeChatUiaCaptureAdapter | 使用 Windows UI Automation 读取用户可见窗口文本 |
| 飞书 | FeishuNotificationAdapter 或 FeishuApiAdapter | 优先使用开放接口或通知采集 |
| 石化通 | ShihuatongUiaCaptureAdapter | 若无开放接口，按窗口可见内容采集 |
| 钉钉 | DingTalkApiAdapter 或 DingTalkUiaCaptureAdapter | 优先开放接口，其次 UI Automation |

约束：

1. 不使用进程注入、Hook、破解数据库或绕过加密。
2. 每个 Adapter 负责来源软件特有的读取方式，不负责分类、待办和看板逻辑。
3. 每个 Adapter 必须提供稳定的 `SourceMessageKey`，用于跨重启去重。
4. 每个 Adapter 必须维护 offset，避免重复处理历史消息。
