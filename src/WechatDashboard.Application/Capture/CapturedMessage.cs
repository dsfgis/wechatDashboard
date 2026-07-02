using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 采集到的消息（适配器输出形态），尚未持久化。
/// 与 <see cref="Message"/> 区别：不含 Id，按来源去重键存储。
/// </summary>
/// <param name="Source">来源标识。</param>
/// <param name="SourceMessageKey">来源系统消息唯一键。</param>
/// <param name="ChatId">会话 ID。</param>
/// <param name="ChatName">会话/群名。</param>
/// <param name="SenderName">发送人。</param>
/// <param name="Content">正文。</param>
/// <param name="SentAt">发送时间。</param>
/// <param name="MessageType">消息类型。</param>
public sealed record CapturedMessage(
    string Source,
    string SourceMessageKey,
    string ChatId,
    string ChatName,
    string SenderName,
    string Content,
    DateTimeOffset SentAt,
    MessageType MessageType);
