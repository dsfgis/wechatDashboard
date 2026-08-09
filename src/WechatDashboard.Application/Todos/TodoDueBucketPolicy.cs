using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

/// <summary>按当前时刻和用户时区计算互斥的到期分组。</summary>
public sealed class TodoDueBucketPolicy
{
    private readonly TimeZoneInfo _timeZone;

    public TodoDueBucketPolicy(TimeZoneInfo timeZone)
    {
        _timeZone = timeZone;
    }

    public TodoDueBucket Classify(DateTimeOffset? dueAt, DateTimeOffset now)
    {
        if (dueAt is null)
        {
            return TodoDueBucket.NoDueDate;
        }

        if (dueAt.Value <= now)
        {
            return TodoDueBucket.Overdue;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, _timeZone);
        var nextLocalDay = localNow.Date.AddDays(1);
        var nextDayOffset = _timeZone.GetUtcOffset(nextLocalDay);
        var nextDayStart = new DateTimeOffset(nextLocalDay, nextDayOffset);
        return dueAt.Value < nextDayStart
            ? TodoDueBucket.DueToday
            : TodoDueBucket.Upcoming;
    }
}
