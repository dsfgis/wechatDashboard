namespace WechatDashboard.Domain.Entities;

public sealed record CaptureSourceSettings(
    long Id,
    string Source,
    string DisplayName,
    string Kind,
    string Location,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
