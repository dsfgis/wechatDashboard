using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

public enum UpdateFollowedProjectColorOutcome
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

    /// <summary>按 ID 删除关注项目。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
