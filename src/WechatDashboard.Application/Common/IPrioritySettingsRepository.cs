namespace WechatDashboard.Application.Common;

/// <summary>
/// 优先级层数与颜色的配置仓储。
/// 只保存颜色数组：数组长度即层数，索引即层级（0 对应 P0），
/// 因此不需要新增数据库表，也不会影响既有待办的 priority 字段。
/// </summary>
public interface IPrioritySettingsRepository
{
    /// <summary>读取已保存的颜色数组；未配置或配置损坏时返回默认色板。</summary>
    Task<IReadOnlyList<string>> GetColorsAsync(CancellationToken cancellationToken);

    /// <summary>保存颜色数组，长度必须在 2–6 之间且每项为 #RRGGBB。</summary>
    Task SaveColorsAsync(IReadOnlyList<string> colors, CancellationToken cancellationToken);
}
