using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>关注项目关键字仓储：一个项目可关联多个关键字。</summary>
public interface IFollowedProjectKeywordRepository
{
    Task<IReadOnlyList<FollowedProjectKeyword>> GetAllAsync(CancellationToken cancellationToken);
    Task<FollowedProjectKeyword> SaveAsync(long projectId, string keyword, CancellationToken cancellationToken);
    Task DeleteAsync(long id, CancellationToken cancellationToken);
    Task DeleteByProjectIdAsync(long projectId, CancellationToken cancellationToken);
}
