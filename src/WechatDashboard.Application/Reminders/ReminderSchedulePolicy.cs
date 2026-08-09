using WechatDashboard.Application.Common;

namespace WechatDashboard.Application.Reminders;

/// <summary>把 UI 的延期预设转换成绝对时间。</summary>
public sealed class ReminderSchedulePolicy
{
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;

    public ReminderSchedulePolicy(IClock clock, TimeZoneInfo timeZone)
    {
        _clock = clock;
        _timeZone = timeZone;
    }

    public DateTimeOffset After(TimeSpan delay) => _clock.Now.Add(delay);

    public DateTimeOffset TomorrowAtNine()
    {
        var localNow = TimeZoneInfo.ConvertTime(_clock.Now, _timeZone);
        var localTarget = localNow.Date.AddDays(1).AddHours(9);
        return new DateTimeOffset(localTarget, _timeZone.GetUtcOffset(localTarget));
    }
}
