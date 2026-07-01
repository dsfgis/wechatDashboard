namespace WechatDashboard.Domain.Entities;

public sealed record UserAlias(
    long Id,
    string Alias,
    bool IsActive,
    DateTimeOffset CreatedAt);
