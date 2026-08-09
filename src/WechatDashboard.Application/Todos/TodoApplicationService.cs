using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

public sealed record CreateTodoFromMessageRequest(
    long MessageId,
    string? Title,
    string? Description,
    long? ProjectId,
    PriorityLevel? Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? FirstReminderAt);

public enum CreateTodoOutcome
{
    Created,
    ExistingTodo,
    SourceMessageMissing
}

public sealed record CreateTodoResult(CreateTodoOutcome Outcome, TodoItem? Todo);

public sealed record UpdateTodoRequest(
    long TodoId,
    string Title,
    string? Description,
    long? ProjectId,
    PriorityLevel Priority,
    DateTimeOffset? DueAt,
    TodoStatus Status,
    DateTimeOffset ExpectedUpdatedAt);

/// <summary>任意消息转 Todo 的用例服务；事务细节由工作单元实现。</summary>
public sealed class TodoApplicationService
{
    private readonly ITodoUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public TodoApplicationService(ITodoUnitOfWork unitOfWork, IClock clock)
    {
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<CreateTodoResult> CreateFromMessageAsync(
        CreateTodoFromMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MessageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MessageId must be a persisted positive id.");
        }

        if (request.FirstReminderAt is { } reminderAt && reminderAt <= _clock.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Reminder time must be in the future.");
        }

        return _unitOfWork.CreateFromMessageAsync(request, _clock.Now, cancellationToken);
    }

    public Task<TodoItem?> UpdateAsync(UpdateTodoRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Todo title is required.", nameof(request));
        }

        return _unitOfWork.UpdateTodoAsync(request with { Title = request.Title.Trim() }, _clock.Now, cancellationToken);
    }
}
