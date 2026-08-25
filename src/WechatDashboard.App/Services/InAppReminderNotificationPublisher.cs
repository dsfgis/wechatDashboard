using WechatDashboard.App.Views.Reminders;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;

namespace WechatDashboard.App.Services;

/// <summary>第一版应用内提醒适配器；Windows Toast 可在不改用例层的情况下替换。</summary>
public sealed class InAppReminderNotificationPublisher : IUserNotificationPublisher
{
    private readonly ReminderApplicationService _reminders;
    private readonly ITodoRepository _todos;
    private readonly IClock _clock;
    private readonly Func<Task> _refresh;

    public InAppReminderNotificationPublisher(
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

    public async Task PublishAsync(ReminderDispatchItem item, CancellationToken cancellationToken)
    {
        var action = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => ReminderNotificationWindow.Show(item.TodoTitle, item.TodoDescription));
        if (action == ReminderNotificationAction.Complete)
        {
            await _todos.MarkSelectedCompletedAsync(
                new[] { item.Reminder.TodoId },
                _clock.Now,
                cancellationToken);
        }
        else if (action == ReminderNotificationAction.SnoozeTenMinutes)
        {
            await _reminders.SnoozeAsync(item.Reminder.Id, _clock.Now.AddMinutes(10), cancellationToken);
        }

        await await System.Windows.Application.Current.Dispatcher.InvokeAsync(_refresh);
    }
}
