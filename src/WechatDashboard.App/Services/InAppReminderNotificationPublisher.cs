using WechatDashboard.Application.Reminders;

namespace WechatDashboard.App.Services;

/// <summary>
/// 应用内提醒通知适配器。只负责把到期提醒投入右下角队列并立即返回，
/// 使提醒轮询不再被用户处理动作阻塞；用户选择的落地由 <see cref="ReminderResolutionHandler"/> 承担。
/// </summary>
public sealed class InAppReminderNotificationPublisher : IUserNotificationPublisher
{
    private readonly ReminderNotificationQueue _queue;

    public InAppReminderNotificationPublisher(ReminderNotificationQueue queue)
    {
        _queue = queue;
    }

    public Task PublishAsync(ReminderDispatchItem item, CancellationToken cancellationToken)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return Task.CompletedTask;
        }

        if (dispatcher.CheckAccess())
        {
            _queue.Enqueue(item);
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(() => _queue.Enqueue(item)).Task;
    }
}
