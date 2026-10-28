using WechatDashboard.App.Views.Reminders;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;

namespace WechatDashboard.App.Services;

/// <summary>
/// 把用户在提醒卡片上的选择落地为用例调用：完成待办、延后 10 分钟，或仅确认已知悉。
/// 与提醒队列解耦，便于单独替换（例如后续改为 Windows Toast 时复用同一处理逻辑）。
/// </summary>
public sealed class ReminderResolutionHandler
{
    private static readonly TimeSpan SnoozeDuration = TimeSpan.FromMinutes(10);

    private readonly ReminderApplicationService _reminders;
    private readonly ITodoRepository _todos;
    private readonly IClock _clock;
    private readonly Func<Task> _refresh;

    public ReminderResolutionHandler(
        ReminderApplicationService reminders,
        ITodoRepository todos,
        IClock clock,
        Func<Task> refresh)
    {
        _reminders = reminders;
        _todos = todos;
        _clock = clock;
        _refresh = refresh;
    }

    public async Task HandleAsync(ReminderDispatchItem item, ReminderNotificationAction action)
    {
        if (action == ReminderNotificationAction.Complete)
        {
            await _todos.MarkSelectedCompletedAsync(
                new[] { item.Reminder.TodoId },
                _clock.Now,
                CancellationToken.None);
        }
        else if (action == ReminderNotificationAction.SnoozeTenMinutes)
        {
            await _reminders.SnoozeAsync(item.Reminder.Id, _clock.Now.Add(SnoozeDuration), CancellationToken.None);
        }

        await _refresh();
    }
}
