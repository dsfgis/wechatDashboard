namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 关注项目实体，对应 followed_projects 表。
/// 维护用户关注的项目名称列表，用于判断群名是否包含项目名从而重点关注。
/// 例如添加"CRM"后，群名包含"CRM"的群消息会被标记为重点关注。
/// </summary>
/// <param name="Id">数据库自增主键。</param>
/// <param name="ProjectName">项目名称（用于与群名做包含匹配）。</param>
/// <param name="IsActive">是否启用（软删除标记）。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record FollowedProject(
    long Id,
    string ProjectName,
    bool IsActive,
    DateTimeOffset CreatedAt);
