using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 关注群仓储接口：持久化用户关注的群聊名称列表。
/// 实现见 SqliteFollowedChatRepository。
/// </summary>
public interface IFollowedChatRepository
{
    /// <summary>读取所有启用状态的关注群。</summary>
    Task<IReadOnlyList<FollowedChat>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>保存关注群（重复群名不会新增）。</summary>
    Task<FollowedChat> SaveAsync(string chatName, CancellationToken cancellationToken);

    /// <summary>按 ID 软删除关注群。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
