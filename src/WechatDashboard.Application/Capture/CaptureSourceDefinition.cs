namespace WechatDashboard.Application.Capture;

public sealed record CaptureSourceDefinition(
    string Source,
    string DisplayName,
    CaptureSourceKind Kind,
    string Location,
    bool IsEnabled = true);
