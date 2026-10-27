using WechatDashboard.Application.Reminders;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Todos;

/// <summary>Todo 创建、提醒延期和完成取消提醒的原子边界。</summary>
public interface ITodoUnitOfWork
{
    Task<CreateTodoResult> CreateFromMessageAsync(
        CreateTodoFromMessageRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<CreateManualTodoResult> CreateManualAsync(
        CreateManualTodoRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>把一条消息合并到既有待办：追加来源消息并按更晚时间调整提醒。</summary>
    Task<MergeTodoResult> MergeIntoExistingAsync(
        MergeTodoRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<SnoozeReminderResult> SnoozeReminderAsync(
        long reminderId,
        DateTimeOffset snoozeUntil,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<TodoItem?> UpdateTodoAsync(
        UpdateTodoRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<TodoReminder?> ScheduleReminderAsync(
        long todoId,
        DateTimeOffset scheduledAt,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
