using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 消息实体，对应数据库 messages 表中的一条记录。
/// 表示从各采集源（微信、飞书等）捕获到的一条原始消息。
/// </summary>
/// <param name="Id">数据库自增主键。</param>
/// <param name="Source">消息来源标识，如 WeChat、Feishu、Sample。</param>
/// <param name="SourceMessageKey">来源系统中的消息唯一键，用于去重。</param>
/// <param name="ChatSessionId">会话/群聊的内部会话 ID。</param>
/// <param name="ChatName">群聊或会话名称（显示用）。</param>
/// <param name="SenderName">发送人昵称。</param>
/// <param name="Content">消息正文。</param>
/// <param name="SentAt">消息发送时间。</param>
/// <param name="CapturedAt">消息被采集入库的时间。</param>
/// <param name="MessageType">消息类型（文本、图片、文件等）。</param>
/// <param name="IsMentionMe">是否 @ 到当前用户。</param>
public sealed record Message(
    long Id,
    string Source,
    string SourceMessageKey,
    long ChatSessionId,
    string ChatName,
    string SenderName,
    string Content,
    DateTimeOffset SentAt,
    DateTimeOffset CapturedAt,
    MessageType MessageType,
    bool IsMentionMe);
