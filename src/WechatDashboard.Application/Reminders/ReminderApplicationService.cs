using WechatDashboard.Application.Common;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Reminders;

public sealed record SnoozeReminderResult(bool Succeeded, TodoReminder? Reminder, string? Error);

/// <summary>提醒延期用例；延期只产生新提醒，不修改 Todo 截止时间。</summary>
public sealed class ReminderApplicationService
{
    private readonly ITodoUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ReminderApplicationService(ITodoUnitOfWork unitOfWork, IClock clock)
    {
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<SnoozeReminderResult> SnoozeAsync(
        long reminderId,
        DateTimeOffset snoozeUntil,
        CancellationToken cancellationToken)
    {
        if (snoozeUntil <= _clock.Now)
        {
            return Task.FromResult(new SnoozeReminderResult(false, null, "延后时间必须晚于当前时间。"));
        }

        return _unitOfWork.SnoozeReminderAsync(reminderId, snoozeUntil, _clock.Now, cancellationToken);
    }

    public Task<TodoReminder?> ScheduleAsync(
        long todoId,
        DateTimeOffset scheduledAt,
        CancellationToken cancellationToken)
    {
        if (scheduledAt <= _clock.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduledAt), "提醒时间必须晚于当前时间。");
        }

        return _unitOfWork.ScheduleReminderAsync(todoId, scheduledAt, _clock.Now, cancellationToken);
    }
}
