using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Common;

/// <summary>
/// 优先级的唯一取色入口。消息流正文着色、待办列表、已办理列表与日程卡片都调用这里，
/// 保证同一层级在全局只有一种颜色。
/// 实际取色来自 <see cref="PrioritySettings.Current"/>（“优先级设置”页可配置层数与颜色）；
/// 下列常量只是默认色板，供未配置时兜底与测试断言使用。
/// </summary>
public static class PriorityColorPolicy
{
    public const string P0Color = "#D92D20";
    public const string P1Color = "#B54708";
    public const string P2Color = "#3B5998";
    public const string P3Color = "#475467";
    public const string P4Color = "#7C3AED";
    public const string P5Color = "#0E7490";

    /// <summary>取该层级当前生效的颜色；超出已配置层数时按最低一级取色。</summary>
    public static string For(PriorityLevel priority) => PrioritySettings.Current.ColorFor(priority);
}
