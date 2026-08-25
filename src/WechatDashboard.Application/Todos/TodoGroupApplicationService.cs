using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Todos;

public sealed class TodoGroupApplicationService
{
    private readonly ITodoGroupRepository _groups;
    private readonly IClock _clock;

    public TodoGroupApplicationService(ITodoGroupRepository groups, IClock clock)
    {
        _groups = groups;
        _clock = clock;
    }

    public Task<IReadOnlyList<TodoGroup>> GetAllAsync(CancellationToken cancellationToken) =>
        _groups.GetAllAsync(cancellationToken);

    public Task<CreateTodoGroupResult> CreateAsync(string name, CancellationToken cancellationToken) =>
        CreateAsync(name, TodoGroupColorPolicy.DefaultColor, cancellationToken);

    public Task<CreateTodoGroupResult> CreateAsync(
        string name,
        string color,
        CancellationToken cancellationToken)
    {
        var displayName = TodoGroupNamePolicy.NormalizeDisplayName(name);
        var normalizedName = TodoGroupNamePolicy.NormalizeKey(displayName);
        var normalizedColor = TodoGroupColorPolicy.Normalize(color);
        return _groups.CreateAsync(displayName, normalizedName, normalizedColor, _clock.Now, cancellationToken);
    }
}
