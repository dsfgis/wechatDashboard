namespace WechatDashboard.Application.Capture;

/// <summary>
/// 采集运行结果汇总。
/// </summary>
/// <param name="CapturedCount">本次采集到的消息总数。</param>
/// <param name="PersistedCount">实际入库的消息数（去重后）。</param>
/// <param name="DuplicateCount">因重复被跳过的消息数。</param>
/// <param name="CreatedTodoCount">本次新创建的待办数。</param>
public sealed record CaptureRunResult(
    int CapturedCount,
    int PersistedCount,
    int DuplicateCount,
    int CreatedTodoCount);
