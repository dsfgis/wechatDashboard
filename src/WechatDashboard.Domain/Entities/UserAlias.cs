namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 用户别名实体，对应 user_aliases 表。
/// 维护当前用户的多个称呼（如"白驹过隙""戴少峰"），用于 @我 检测。
/// 别名可由界面增删，MentionDetector 会使用这些别名判断消息是否 @ 到自己。
/// </summary>
/// <param name="Id">数据库自增主键。</param>
/// <param name="Alias">别名文本。</param>
/// <param name="IsActive">是否启用（软删除标记）。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record UserAlias(
    long Id,
    string Alias,
    bool IsActive,
    DateTimeOffset CreatedAt);
