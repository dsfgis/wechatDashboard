namespace WechatDashboard.Infrastructure.Capture;

public sealed record WindowCaptureDiagnosticRow(
    string WindowTitle,
    DateTimeOffset CapturedAt,
    int TextLength,
    string Preview);
