using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>项目成员仓储：成员在项目设置中维护，并随项目删除一并清理。</summary>
public interface IProjectMemberRepository
{
    /// <summary>读取全部项目的成员（含已停用项目时由调用方自行过滤）。</summary>
    Task<IReadOnlyList<ProjectMember>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>兼容既有调用的新增/更新签名；微信 ID 按空值保存。</summary>
    Task<ProjectMember> SaveAsync(
        long projectId,
        string displayName,
        string? alias,
        string? email,
        CancellationToken cancellationToken);

    /// <summary>
    /// 新增成员；同一项目同名时覆盖别名、邮箱与微信 ID，保持幂等。
    /// 周报开关不属于本签名，冲突更新绝不会覆盖 IsReporting。
    /// </summary>
    Task<ProjectMember> SaveAsync(
        long projectId,
        string displayName,
        string? alias,
        string? email,
        string? weChatUserId,
        CancellationToken cancellationToken);

    /// <summary>只更新指定成员的周报开关，不修改微信 ID、邮箱或其他成员资料。</summary>
    Task SetReportingAsync(long id, bool isReporting, CancellationToken cancellationToken);

    /// <summary>删除指定成员。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>删除项目的全部成员。</summary>
    Task DeleteByProjectIdAsync(long projectId, CancellationToken cancellationToken);
}
