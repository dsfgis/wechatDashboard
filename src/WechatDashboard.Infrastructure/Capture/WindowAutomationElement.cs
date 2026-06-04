namespace WechatDashboard.Infrastructure.Capture;

public sealed record WindowAutomationElement(
    string Name,
    string Text,
    IReadOnlyList<WindowAutomationElement> Children);
