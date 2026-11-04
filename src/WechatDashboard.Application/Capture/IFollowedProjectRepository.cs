using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

public enum UpdateFollowedProjectColorOutcome
{
    Updated,
    Unchanged,
    Missing
}

/// <summary>更新项目生命周期状态的结果。</summary>
public enum UpdateFollowedProjectStageOutcome
{
    Updated,
    Unchanged,
    Missing
}

/// <summary>
/// 关注项目仓储接口：持久化用户关注的项目名称列表。
/// 实现见 SqliteFollowedProjectRepository。
/// 项目名用于与群名做包含匹配，匹配成功的群消息将被标记为重点关注。
/// </summary>
public interface IFollowedProjectRepository
{
    /// <summary>读取所有启用状态的关注项目。</summary>
    Task<IReadOnlyList<FollowedProject>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>保存关注项目（重复项目名不会新增）。</summary>
    Task<FollowedProject> SaveAsync(string projectName, CancellationToken cancellationToken);

    /// <summary>保存带固定色板颜色的关注项目；重复名称保留原颜色。</summary>
    Task<FollowedProject> SaveAsync(string projectName, string color, CancellationToken cancellationToken);

    /// <summary>按稳定项目 ID 更新固定色板颜色，不触碰项目名称或关键字。</summary>
    Task<UpdateFollowedProjectColorOutcome> UpdateColorAsync(
        long id,
        string color,
        CancellationToken cancellationToken);

    /// <summary>按稳定项目 ID 更新生命周期状态（在建/运维/移交）。</summary>
    Task<UpdateFollowedProjectStageOutcome> UpdateStageAsync(
        long id,
        ProjectStage stage,
        CancellationToken cancellationToken);

    /// <summary>
    /// 在同一事务内更新生命周期状态与颜色。
    /// 界面已把状态与颜色合并成一个下拉框（状态决定颜色），
    /// 因此这是一次写入而不是两次，避免出现“状态已改、颜色未改”的中间态。
    /// </summary>
    Task<UpdateFollowedProjectStageOutcome> UpdateStageAndColorAsync(
        long id,
        ProjectStage stage,
        string color,
        CancellationToken cancellationToken);

    /// <summary>按 ID 删除关注项目。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
