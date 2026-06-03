# 消息采集扩展说明

## 当前实现

当前版本已经实现通用采集流水线和一个本地 JSONL 目录采集器。

采集目录：

```text
%LOCALAPPDATA%\WechatDashboard\capture-inbox
```

WPF 界面点击“采集一次”后，会读取该目录下的 `*.jsonl` 文件，把新消息写入 SQLite，并对命中 `@我` 的消息自动创建待办理 Todo。

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
