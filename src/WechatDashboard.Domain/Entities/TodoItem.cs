using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

public sealed record TodoItem(
    long Id,
    long? SourceMessageId,
    long? ProjectId,
    string Title,
    string? Description,
    TodoStatus Status,
    PriorityLevel Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool IsAutoCreated);
