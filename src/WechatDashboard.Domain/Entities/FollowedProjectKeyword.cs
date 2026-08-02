namespace WechatDashboard.Domain.Entities;

/// <summary>关联到关注项目的可配置匹配关键字。</summary>
public sealed record FollowedProjectKeyword(
    long Id,
    long ProjectId,
    string ProjectName,
    string Keyword,
    DateTimeOffset CreatedAt);
