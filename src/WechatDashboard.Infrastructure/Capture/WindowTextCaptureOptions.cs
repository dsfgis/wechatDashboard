namespace WechatDashboard.Infrastructure.Capture;

public sealed record WindowTextCaptureOptions(
    string Source,
    string DisplayName,
    string WindowTitleContains,
    string ChatId,
    string ChatName)
{
    public IReadOnlyList<string> IgnoreLinePrefixes { get; init; } = Array.Empty<string>();
}
