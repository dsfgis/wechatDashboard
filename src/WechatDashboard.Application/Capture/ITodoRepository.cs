using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 待办仓储接口。
/// 实现见 SqliteTodoRepository。
/// </summary>
public interface ITodoRepository
{
    /// <summary>保存待办（新增或更新）。</summary>
    Task<TodoItem> SaveAsync(TodoItem todo, CancellationToken cancellationToken);

    /// <summary>获取所有未完成的待办（按优先级和时间排序）。</summary>
    Task<IReadOnlyList<TodoItem>> GetPendingAsync(CancellationToken cancellationToken);

    /// <summary>获取所有已办理的待办（按完成时间倒序排列）。</summary>
    Task<IReadOnlyList<TodoItem>> GetCompletedAsync(CancellationToken cancellationToken);

    /// <summary>获取所有活动状态待办。</summary>
    Task<IReadOnlyList<TodoItem>> GetActiveAsync(CancellationToken cancellationToken);

    /// <summary>按主键获取待办。</summary>
    Task<TodoItem?> GetByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>按原消息获取第一个关联待办，用于消息转待办幂等。</summary>
    Task<TodoItem?> GetBySourceMessageIdAsync(long sourceMessageId, CancellationToken cancellationToken);

    /// <summary>将指定待办标记为已办理，并记录完成时间。</summary>
    Task<bool> MarkCompletedAsync(long id, DateTimeOffset completedAt, CancellationToken cancellationToken);

    /// <summary>设置活动待办的置顶状态。</summary>
    Task<bool> SetPinnedAsync(long id, bool isPinned, DateTimeOffset updatedAt, CancellationToken cancellationToken);

    /// <summary>仅将指定的活动待办标记为已办理，并返回实际更新数量。</summary>
    Task<int> MarkSelectedCompletedAsync(
        IReadOnlyCollection<long> ids,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    /// <summary>将所有活动状态（待办理、进行中、等待）的记录标记为已办理，并返回更新数量。</summary>
    Task<int> MarkAllCompletedAsync(DateTimeOffset completedAt, CancellationToken cancellationToken);

    /// <summary>删除所有已办理记录，并返回删除数量。</summary>
    Task<int> DeleteCompletedAsync(CancellationToken cancellationToken);
}
