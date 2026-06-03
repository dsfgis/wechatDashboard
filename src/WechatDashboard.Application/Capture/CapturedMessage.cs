using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Capture;

public sealed record CapturedMessage(
    string Source,
    string SourceMessageKey,
    string ChatId,
    string ChatName,
    string SenderName,
    string Content,
    DateTimeOffset SentAt,
    MessageType MessageType);
