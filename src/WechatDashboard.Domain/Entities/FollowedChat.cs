namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 关注群实体，对应 followed_chats 表。
/// 维护用户关注的群聊名称列表，用于过滤消息流和微信消息，只展示关注群的消息。
/// </summary>
/// <param name="Id">数据库自增主键。</param>
/// <param name="ChatName">群聊名称。</param>
/// <param name="IsActive">是否启用（软删除标记）。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record FollowedChat(
    long Id,
    string ChatName,
    bool IsActive,
    DateTimeOffset CreatedAt);
