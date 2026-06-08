namespace WechatDashboard.Infrastructure.Capture;

public sealed record WeChatLocalCommandOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public string? TemporaryDirectory { get; init; }
}
