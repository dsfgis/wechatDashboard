using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

public interface ITodoRepository
{
    Task<TodoItem> SaveAsync(TodoItem todo, CancellationToken cancellationToken);

    Task<IReadOnlyList<TodoItem>> GetPendingAsync(CancellationToken cancellationToken);
}
