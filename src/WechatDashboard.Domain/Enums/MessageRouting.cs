namespace WechatDashboard.Domain.Enums;

/// <summary>
/// 会话类型。只有采集源能够提供可靠证据时才标记私聊或群聊，
/// 其他来源与历史消息必须保留 Unknown，避免被误当作直接发给当前用户的内容。
/// </summary>
public enum ChatType
{
    /// <summary>无法确认会话类型。</summary>
    Unknown,

    /// <summary>一对一私聊。</summary>
    Private,

    /// <summary>群聊或多人会话。</summary>
    Group
}

/// <summary>
/// 消息相对当前采集账号的方向。Unknown 是显式的安全默认值，
/// 不代表 Incoming；只有来源字段语义明确时才标记收发方向。
/// </summary>
public enum Direction
{
    /// <summary>无法确认收发方向。</summary>
    Unknown,

    /// <summary>当前采集账号收到的消息。</summary>
    Incoming,

    /// <summary>当前采集账号发出的消息。</summary>
    Outgoing
}
