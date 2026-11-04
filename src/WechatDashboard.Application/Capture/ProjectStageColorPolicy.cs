using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 项目状态与颜色的唯一映射：状态即颜色，界面不再单独提供颜色选择。
/// 在建 = 红色、运维 = 蓝色、移交（枚举值 Handover）= 灰色。
/// 历史数据里独立选择的颜色会在加载项目列表时被静默归一化到本映射，
/// 保证项目树圆点、待办卡片与日程卡片读到同一个颜色。
/// </summary>
public static class ProjectStageColorPolicy
{
    /// <summary>在建项目使用红色，表示正在推进。</summary>
    public const string ConstructionColor = ProjectColorPolicy.Red;

    /// <summary>运维项目使用蓝色，表示已交付运行。</summary>
    public const string OperationColor = ProjectColorPolicy.Blue;

    /// <summary>移交项目使用灰色（色板默认色），表示已退出主线跟踪。</summary>
    public const string HandoverColor = ProjectColorPolicy.DefaultColor;

    /// <summary>取状态对应的固定颜色；未知状态一律按“在建”处理，与 ProjectStagePolicy.Parse 的回落一致。</summary>
    public static string GetColor(ProjectStage stage) => stage switch
    {
        ProjectStage.Operation => OperationColor,
        ProjectStage.Handover => HandoverColor,
        _ => ConstructionColor
    };
}
