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

    /// <summary>将指定待办标记为已办理，并记录完成时间。</summary>
    Task<bool> MarkCompletedAsync(long id, DateTimeOffset completedAt, CancellationToken cancellationToken);

    /// <summary>将所有待办理记录标记为已办理，并返回更新数量。</summary>
    Task<int> MarkAllCompletedAsync(DateTimeOffset completedAt, CancellationToken cancellationToken);

    /// <summary>删除所有已办理记录，并返回删除数量。</summary>
    Task<int> DeleteCompletedAsync(CancellationToken cancellationToken);
}
