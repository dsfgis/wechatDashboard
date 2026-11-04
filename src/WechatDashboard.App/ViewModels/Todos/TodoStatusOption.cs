using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

/// <summary>
/// 待办状态下拉选项。只有“完成”会让待办离开待办理列表，
/// 进行中与等待仍按截止时间留在待办理，因此不提供“忽略”入口。
/// </summary>
/// <param name="Value">状态枚举值，作为 SelectedValue。</param>
/// <param name="Name">中文显示名。</param>
public sealed record TodoStatusOption(TodoStatus Value, string Name)
{
    /// <summary>详情窗口可主动选择的状态，顺序即下拉框顺序。</summary>
    public static IReadOnlyList<TodoStatusOption> Selectable { get; } =
    [
        new(TodoStatus.Pending, "待办理"),
        new(TodoStatus.InProgress, "进行中"),
        new(TodoStatus.Waiting, "等待"),
        new(TodoStatus.Done, "完成")
    ];

    /// <summary>
    /// 按当前状态取可选项。历史数据若为“忽略”，补一项仅用于正确显示，
    /// 避免下拉框因找不到匹配值而显示空白。
    /// </summary>
    public static IReadOnlyList<TodoStatusOption> ForCurrent(TodoStatus status) =>
        status == TodoStatus.Ignored
            ? [.. Selectable, new(TodoStatus.Ignored, "已忽略")]
            : Selectable;

    /// <summary>状态中文名，供详情窗口与待办列表角标共用。</summary>
    public static string NameOf(TodoStatus status) => status switch
    {
        TodoStatus.InProgress => "进行中",
        TodoStatus.Waiting => "等待",
        TodoStatus.Done => "完成",
        TodoStatus.Ignored => "已忽略",
        _ => "待办理"
    };
}
