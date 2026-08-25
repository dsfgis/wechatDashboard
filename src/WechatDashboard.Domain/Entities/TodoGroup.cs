namespace WechatDashboard.Domain.Entities;

/// <summary>用户自定义待办分组；与消息项目和截止时间分类相互独立。</summary>
public sealed record TodoGroup(
    long Id,
    string Name,
    string NormalizedName,
    string Color,
    DateTimeOffset CreatedAt);
