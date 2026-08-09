namespace WechatDashboard.Domain.Enums;

/// <summary>根据当前时间派生的待办到期分组，不持久化到数据库。</summary>
public enum TodoDueBucket
{
    Overdue,
    DueToday,
    Upcoming,
    NoDueDate
}
