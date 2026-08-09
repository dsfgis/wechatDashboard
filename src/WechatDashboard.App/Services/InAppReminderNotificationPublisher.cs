using System.Windows;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;

namespace WechatDashboard.App.Services;

/// <summary>第一版应用内提醒适配器；Windows Toast 可在不改用例层的情况下替换。</summary>
public sealed class InAppReminderNotificationPublisher : IUserNotificationPublisher
{
    private readonly ReminderApplicationService _reminders;
    private readonly IClock _clock;
    private readonly Func<Task> _refresh;

    public InAppReminderNotificationPublisher(ReminderApplicationService reminders, IClock clock, Func<Task> refresh)
    {
        _reminders = reminders;
        _clock = clock;
        _refresh = refresh;
    }

    public async Task PublishAsync(ReminderDispatchItem item, CancellationToken cancellationToken)
    {
        var result = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => MessageBox.Show(
            $"{item.TodoTitle}\n\n是否延后 10 分钟？选择“否”表示本次提醒已知晓。",
            "待办提醒",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information));
        if (result == MessageBoxResult.Yes)
        {
            await _reminders.SnoozeAsync(item.Reminder.Id, _clock.Now.AddMinutes(10), cancellationToken);
        }

        await await System.Windows.Application.Current.Dispatcher.InvokeAsync(_refresh);
    }
}
