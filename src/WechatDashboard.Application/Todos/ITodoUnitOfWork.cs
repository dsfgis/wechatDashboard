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
