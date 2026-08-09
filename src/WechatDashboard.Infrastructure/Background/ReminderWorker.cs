using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;

namespace WechatDashboard.Infrastructure.Background;

/// <summary>周期领取到期提醒并通过可替换通知适配器发送。</summary>
public sealed class ReminderWorker
{
    private readonly IReminderRepository _repository;
    private readonly IUserNotificationPublisher _publisher;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _claimTimeout;

    public ReminderWorker(
        IReminderRepository repository,
        IUserNotificationPublisher publisher,
        IClock clock,
        TimeSpan? interval = null,
        TimeSpan? claimTimeout = null)
    {
        _repository = repository;
        _publisher = publisher;
        _clock = clock;
        _interval = interval ?? TimeSpan.FromMinutes(1);
        _claimTimeout = claimTimeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RunOnceAsync(cancellationToken);
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunOnceAsync(cancellationToken);
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _clock.Now;
        await _repository.RecoverStaleClaimsAsync(now.Subtract(_claimTimeout), now, cancellationToken);
        var due = await _repository.GetDueAsync(now, 50, cancellationToken);
        var delivered = 0;
        foreach (var item in due)
        {
            if (!await _repository.TryClaimAsync(item.Reminder.Id, _clock.Now, cancellationToken))
            {
                continue;
            }

            try
            {
                await _publisher.PublishAsync(item, cancellationToken);
                await _repository.MarkDeliveredAsync(item.Reminder.Id, _clock.Now, cancellationToken);
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delayMinutes = Math.Min(60, Math.Pow(2, Math.Min(item.Reminder.AttemptCount, 5)));
                var summary = $"{ex.GetType().Name}: {ex.Message}";
                await _repository.RescheduleAfterFailureAsync(
                    item.Reminder.Id,
                    _clock.Now.AddMinutes(delayMinutes),
                    summary,
                    cancellationToken);
            }
        }

        return delivered;
    }
}
