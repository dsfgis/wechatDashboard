namespace WechatDashboard.Infrastructure.Capture;

public sealed record WindowTextSnapshot(
    string WindowTitle,
    string Text,
    DateTimeOffset CapturedAt);
