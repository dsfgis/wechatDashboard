using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Todos;

public enum CreateTodoGroupOutcome
{
    Created,
    Existing
}

public sealed record CreateTodoGroupResult(CreateTodoGroupOutcome Outcome, TodoGroup Group);

public interface ITodoGroupRepository
{
    Task<IReadOnlyList<TodoGroup>> GetAllAsync(CancellationToken cancellationToken);
    Task<TodoGroup?> GetByIdAsync(long id, CancellationToken cancellationToken);
    Task<CreateTodoGroupResult> CreateAsync(
        string name,
        string normalizedName,
        string color,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
