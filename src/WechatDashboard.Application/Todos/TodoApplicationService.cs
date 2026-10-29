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
    DateTimeOffset? FirstReminderAt,
    long? ManagedProjectId = null,
    // 独立区分手动 Agent 评分，避免普通待办优先级修改意外覆盖消息流评分。
    PriorityLevel? AgentPriority = null,
    // Agent 识别出的明确完成时间；未显式提供截止时间时作为回退。
    DateTimeOffset? AnalysisDueAt = null);

public enum CreateTodoOutcome
{
    Created,
    ExistingTodo,
    SourceMessageMissing,
    /// <summary>该消息描述的事项已存在，已作为来源消息合并到既有待办。</summary>
    MergedIntoExisting
}

public sealed record CreateTodoResult(CreateTodoOutcome Outcome, TodoItem? Todo);

/// <summary>把一条新消息合并到既有待办的请求。</summary>
/// <param name="TodoId">被合并的目标待办。</param>
/// <param name="MessageId">需要追加为来源的消息。</param>
/// <param name="ReminderAt">新消息推导出的提醒时间；仅在更晚时改期。</param>
/// <param name="DueAt">新消息识别出的截止时间；仅在更晚时更新。</param>
/// <param name="TimeConfirmed">消息是否把时间“确定下来”（那就5点半/改到16点）；为 true 时允许把截止与提醒改早。</param>
public sealed record MergeTodoRequest(
    long TodoId,
    long MessageId,
    DateTimeOffset? ReminderAt,
    DateTimeOffset? DueAt = null,
    bool TimeConfirmed = false);

/// <summary>合并结果；<paramref name="Merged"/> 为 false 表示目标待办不存在或已完成。</summary>
public sealed record MergeTodoResult(bool Merged, bool ReminderRescheduled, TodoItem? Todo);

public sealed record CreateManualTodoRequest(
    string Title,
    string? Description,
    PriorityLevel? Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? FirstReminderAt,
    long? GroupId,
    long? ManagedProjectId = null);

public enum CreateManualTodoOutcome
{
    Created,
    GroupMissing,
    ProjectMissing
}

public sealed record CreateManualTodoResult(CreateManualTodoOutcome Outcome, TodoItem? Todo);

public sealed record UpdateTodoRequest(
    long TodoId,
    string Title,
    string? Description,
    long? ProjectId,
    PriorityLevel Priority,
    DateTimeOffset? DueAt,
    TodoStatus Status,
    DateTimeOffset ExpectedUpdatedAt,
    long? GroupId = null,
    long? ManagedProjectId = null);

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

    public Task<CreateManualTodoResult> CreateManualAsync(
        CreateManualTodoRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Todo title is required.", nameof(request));
        }

        if (request.FirstReminderAt is { } reminderAt && reminderAt <= _clock.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Reminder time must be in the future.");
        }

        return _unitOfWork.CreateManualAsync(
            request with
            {
                Title = request.Title.Trim(),
                Priority = request.Priority ?? PriorityLevel.P2
            },
            _clock.Now,
            cancellationToken);
    }

    /// <summary>把一条消息合并进既有待办：追加来源消息，并在新提醒更晚时改期。</summary>
    public Task<MergeTodoResult> MergeIntoExistingAsync(MergeTodoRequest request, CancellationToken cancellationToken)
    {
        if (request.TodoId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "TodoId must be a persisted positive id.");
        }

        if (request.MessageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MessageId must be a persisted positive id.");
        }

        if (request.ReminderAt is { } reminderAt && reminderAt <= _clock.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Reminder time must be in the future.");
        }

        return _unitOfWork.MergeIntoExistingAsync(request, _clock.Now, cancellationToken);
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
