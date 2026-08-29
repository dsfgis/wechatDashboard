namespace WechatDashboard.Application.Capture;

public enum CaptureSourceRunStatus
{
    Succeeded,
    Empty,
    Skipped,
    Failed
}

/// <summary>单个采集源在本轮运行中的独立结果；错误内容必须是已脱敏文本。</summary>
public sealed record CaptureSourceRunResult(
    string AdapterName,
    CaptureSourceRunStatus Status,
    int CapturedCount,
    int PersistedCount,
    int DuplicateCount,
    int CreatedTodoCount,
    string? Stage,
    string? ErrorCode,
    string? SanitizedError,
    DateTimeOffset CompletedAt);

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
    int CreatedTodoCount,
    IReadOnlyList<CaptureSourceRunResult> Sources)
{
    public CaptureRunResult(
        int capturedCount,
        int persistedCount,
        int duplicateCount,
        int createdTodoCount)
        : this(
            capturedCount,
            persistedCount,
            duplicateCount,
            createdTodoCount,
            Array.Empty<CaptureSourceRunResult>())
    {
    }

    public bool HasFailures => Sources.Any(source => source.Status == CaptureSourceRunStatus.Failed);

    public bool AllSourcesFailed => Sources.Count > 0 && Sources.All(source => source.Status == CaptureSourceRunStatus.Failed);
}
