using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

/// <summary>
/// 优先级下拉选项：层级、代码与当前颜色。
/// 选项集合来自“优先级设置”的层数配置，因此用户改成 2 级后下拉里只剩 P0/P1。
/// </summary>
/// <param name="Level">层级枚举值，作为 SelectedValue。</param>
/// <param name="Code">显示代码，如 P0。</param>
/// <param name="Color">当前配色，用于下拉项的色点与文字。</param>
public sealed record PriorityOption(PriorityLevel Level, string Code, string Color)
{
    /// <summary>按当前生效的色板生成全部可选层级。</summary>
    public static IReadOnlyList<PriorityOption> FromCurrentPalette()
    {
        var palette = PrioritySettings.Current;
        return palette.Levels
            .Select(definition => new PriorityOption(definition.Level, definition.Code, definition.Color))
            .ToArray();
    }
}
