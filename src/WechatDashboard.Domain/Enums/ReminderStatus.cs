namespace WechatDashboard.Domain.Enums;

/// <summary>持久化提醒的生命周期状态。</summary>
public enum ReminderStatus
{
    Scheduled,
    Dispatching,
    Delivered,
    Snoozed,
    Dismissed,
    Cancelled
}
