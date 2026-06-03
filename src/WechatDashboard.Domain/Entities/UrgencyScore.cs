using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

public sealed record UrgencyScore(
    long MessageId,
    int Score,
    PriorityLevel Priority,
    string Reason);
