namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 关注项目的生命周期状态。用于设置界面标注项目阶段，并决定项目下拉框的排序。
/// 排序固定为：在建 → 运维 → 移交。
/// </summary>
public enum ProjectStage
{
    /// <summary>在建：正在实施的项目，排在最前。</summary>
    Construction = 0,

    /// <summary>运维：已交付进入运维期。</summary>
    Operation = 1,

    /// <summary>移交：已交接给其他团队或进入收尾。</summary>
    Handover = 2
}

/// <summary>
/// 项目状态的显示名、存储值解析与排序规则。
/// 存储使用枚举名，避免中文或数字字面量散落在 SQL 与界面中。
/// </summary>
public static class ProjectStagePolicy
{
    /// <summary>下拉框与排序使用的固定顺序。</summary>
    public static IReadOnlyList<ProjectStage> OrderedStages { get; } =
        [ProjectStage.Construction, ProjectStage.Operation, ProjectStage.Handover];

    /// <summary>中文显示名：在建 / 运维 / 移交。</summary>
    public static string GetDisplayName(ProjectStage stage) => stage switch
    {
        ProjectStage.Operation => "运维",
        ProjectStage.Handover => "移交",
        _ => "在建"
    };

    /// <summary>持久化存储值（枚举名）。</summary>
    public static string ToStorageValue(ProjectStage stage) => stage.ToString();

    /// <summary>解析存储值；未知或空值一律回落到“在建”，保证旧数据可用。</summary>
    public static ProjectStage Parse(string? value) => value switch
    {
        nameof(ProjectStage.Operation) => ProjectStage.Operation,
        nameof(ProjectStage.Handover) => ProjectStage.Handover,
        _ => ProjectStage.Construction
    };

    /// <summary>排序权重，数值越小越靠前。</summary>
    public static int GetOrder(ProjectStage stage) => stage switch
    {
        ProjectStage.Operation => 1,
        ProjectStage.Handover => 2,
        _ => 0
    };
}
