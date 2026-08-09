namespace WechatDashboard.Application.Reminders;

public interface IUserNotificationPublisher
{
    Task PublishAsync(ReminderDispatchItem item, CancellationToken cancellationToken);
}
