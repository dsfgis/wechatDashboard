# 消息采集扩展说明

## 当前实现

当前版本已经实现通用采集流水线和一个本地 JSONL 目录采集器。

采集目录：

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox
```

WPF 界面点击“采集一次”后，会读取该目录下按来源划分的子目录，把新消息写入 SQLite，并对命中 `@我` 的消息自动创建待办理 Todo。

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

来源注册由 `CaptureSourceDefinition` 描述，当前 `CaptureAdapterFactory.CreateDefaultJsonlSources(...)` 会为微信、飞书、石化通、钉钉创建 JSONL 目录采集来源。后续真实监听器应继续使用同一来源定义和 `IMessageCaptureAdapter`。

## 可见窗口文本采集

当前版本还提供了 `WindowTextCaptureAdapter` 的可测试核心。它通过 `IWindowTextSnapshotProvider` 获取用户可见窗口文本快照，再解析形如：

```text
09:20 王经理: @张三 今天下班前处理线上故障
09:21 李工：同步一下接口变更
```

的文本行，输出标准 `CapturedMessage`。

已实现能力：

1. 按窗口标题关键字过滤快照。
2. 从 `HH:mm 发送人: 内容` 或 `HH:mm 发送人：内容` 解析发送人、内容和发送时间。
3. 从窗口标题推断会话名，例如 `CRM项目群 - 微信` 推断为 `CRM项目群`。
4. 使用窗口标题和规范化文本行生成稳定 `SourceMessageKey`。
5. 使用快照 fingerprint 作为 offset，避免重复处理完全相同的窗口文本。

`CaptureAdapterFactory.CreateWeChatWindowTextSource()` 已提供微信可见窗口来源定义，但默认 `IsEnabled = false`。真实 Windows UI Automation provider 需要在实际微信桌面窗口上验证后再启用。

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
