namespace WechatDashboard.Application.Capture;

public sealed record CaptureRunResult(
    int CapturedCount,
    int PersistedCount,
    int DuplicateCount,
    int CreatedTodoCount);
