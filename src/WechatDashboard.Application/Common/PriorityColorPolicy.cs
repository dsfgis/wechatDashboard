using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Common;

/// <summary>
/// 优先级 P0–P3 的唯一显示色板。消息流中的正文着色与日历“当日日程”卡片共用同一份定义，
/// 保证同一优先级在全局只有一种颜色；改动这里即同时改变两处。
/// </summary>
public static class PriorityColorPolicy
{
    public const string P0Color = "#D92D20";
    public const string P1Color = "#B54708";
    public const string P2Color = "#3B5998";
    public const string P3Color = "#475467";

    public static string For(PriorityLevel priority) => priority switch
    {
        PriorityLevel.P0 => P0Color,
        PriorityLevel.P1 => P1Color,
        PriorityLevel.P2 => P2Color,
        _ => P3Color
    };
}
