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

    public Task<CreateTodoGroupResult> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var displayName = TodoGroupNamePolicy.NormalizeDisplayName(name);
        var normalizedName = TodoGroupNamePolicy.NormalizeKey(displayName);
        return _groups.CreateAsync(displayName, normalizedName, _clock.Now, cancellationToken);
    }
}
