using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

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
