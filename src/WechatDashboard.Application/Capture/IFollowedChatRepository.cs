using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 关注群过滤模式。
/// - Include：只显示列表中的群（白名单）
/// - Exclude：不显示列表中的群（黑名单）
/// </summary>
public enum FollowedChatFilterMode
{
    Include = 0,
    Exclude = 1
}

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

    /// <summary>读取关注群过滤模式（默认 Include）。</summary>
    Task<FollowedChatFilterMode> GetFilterModeAsync(CancellationToken cancellationToken);

    /// <summary>保存关注群过滤模式。</summary>
    Task SetFilterModeAsync(FollowedChatFilterMode mode, CancellationToken cancellationToken);
}
