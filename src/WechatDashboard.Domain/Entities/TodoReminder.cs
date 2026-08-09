using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>一条可审计的待办提醒；延期会保留旧记录并创建新记录。</summary>
public sealed record TodoReminder(
    long Id,
    long TodoId,
    DateTimeOffset ScheduledAt,
    ReminderStatus Status,
    long? ParentReminderId,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? SnoozedAt,
    DateTimeOffset? DismissedAt,
    int AttemptCount,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
