using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 待办事项实体，对应数据库 todo_items 表。
/// 通常由 @我 的消息自动生成，也可手动创建。
/// </summary>
/// <param name="Id">数据库自增主键。</param>
/// <param name="SourceMessageId">触发该待办的消息 ID，可为空（手动创建）。</param>
/// <param name="ProjectId">所属项目 ID，可为空表示未分类。</param>
/// <param name="Title">待办标题。</param>
/// <param name="Description">详细描述，可为空。</param>
/// <param name="Status">当前状态（待办理/进行中/等待/完成/忽略）。</param>
/// <param name="Priority">优先级（P0 最高 ~ P3 最低）。</param>
/// <param name="DueAt">截止时间，可为空。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">最近更新时间。</param>
/// <param name="CompletedAt">完成时间，未完成为空。</param>
/// <param name="IsAutoCreated">是否由系统自动创建（如 @我 触发）。</param>
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
    bool IsAutoCreated)
{
    /// <summary>是否固定显示在活动待办列表顶部。</summary>
    public bool IsPinned { get; init; }
}
