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
}
