namespace WechatDashboard.Infrastructure.Capture;

public sealed record WeChatLocalExportOptions(string DirectoryPath)
{
    public string Source { get; init; } = "WeChat";
}
