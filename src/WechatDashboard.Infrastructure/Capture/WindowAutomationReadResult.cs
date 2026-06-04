namespace WechatDashboard.Infrastructure.Capture;

public sealed record WindowAutomationReadResult(
    IReadOnlyList<WindowAutomationElement> Windows,
    DateTimeOffset CapturedAt);
