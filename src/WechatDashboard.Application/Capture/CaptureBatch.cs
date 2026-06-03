namespace WechatDashboard.Application.Capture;

public sealed record CaptureBatch(
    string AdapterName,
    IReadOnlyList<CapturedMessage> Messages,
    string NextOffset);
