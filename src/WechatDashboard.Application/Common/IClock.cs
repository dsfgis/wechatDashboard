namespace WechatDashboard.Application.Common;

/// <summary>为到期、提醒和跨午夜逻辑提供可测试的当前时间。</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public DateTimeOffset Now => DateTimeOffset.Now;
}
