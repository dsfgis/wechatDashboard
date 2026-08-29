namespace WechatDashboard.Application.Capture;

/// <summary>原生微信读取器的稳定处理阶段。</summary>
public enum WeChatReaderStage
{
    PathDiscovery,
    ProtectedKey,
    KeyProbe,
    KeyValidation,
    Snapshot,
    DatabaseOpen,
    Schema,
    ContentDecode,
    Query,
    PipelineCommit
}

/// <summary>原生微信读取器的稳定错误码。</summary>
public enum WeChatReaderErrorCode
{
    None,
    Cancelled,
    WeixinProcessMissing,
    DatabaseDirectoryMissing,
    RequiredDatabaseMissing,
    ProtectedKeyUnavailable,
    ProtectedKeyInvalid,
    DllMissing,
    ExportMissing,
    KeyTimeout,
    KeyInvalid,
    SnapshotFailed,
    CipherUnsupported,
    DatabaseCorrupt,
    SchemaUnsupported,
    ContentCorrupt,
    QueryFailed,
    Unknown
}

/// <summary>不含密钥和消息正文的单阶段诊断。</summary>
public sealed record WeChatReaderStageResult(
    WeChatReaderStage Stage,
    bool Succeeded,
    WeChatReaderErrorCode ErrorCode,
    string SanitizedMessage,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyDictionary<string, long>? Counts = null);

/// <summary>原生 reader 初始化请求。</summary>
public sealed record WeChatInitializationRequest(
    string DatabaseRoot,
    int ProcessId,
    TimeSpan KeyTimeout,
    string BootstrapRange = "30d");

/// <summary>使用受保护的本机缓存恢复 reader 的请求，不需要微信进程或 KeyProbe。</summary>
public sealed record WeChatProtectedKeyRestoreRequest(
    string DatabaseRoot,
    string BootstrapRange = "30d");

/// <summary>原生 reader 初始化结果。</summary>
public sealed record WeChatInitializationResult(
    bool Succeeded,
    IReadOnlyList<WeChatReaderStageResult> Stages);

/// <summary>统一的分页或增量读取请求。</summary>
public sealed record WeChatReadRequest(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    int Offset,
    int Limit,
    string? CommittedHighWatermark = null,
    TimeSpan? IncrementalLookback = null,
    string? CommittedSourceFingerprint = null);

/// <summary>统一的分页或增量读取结果。</summary>
public sealed record WeChatReadResult(
    IReadOnlyList<CapturedMessage> Messages,
    int TotalCount,
    string ProposedNextOffset,
    IReadOnlyList<WeChatReaderStageResult> Stages,
    string? SourceFingerprint = null);

/// <summary>带稳定阶段和错误码的原生 reader 异常。</summary>
public sealed class WeChatReaderException : Exception
{
    public WeChatReaderException(
        WeChatReaderStage stage,
        WeChatReaderErrorCode errorCode,
        string sanitizedMessage,
        Exception? innerException = null)
        : base(sanitizedMessage, innerException)
    {
        Stage = stage;
        ErrorCode = errorCode;
    }

    public WeChatReaderStage Stage { get; }

    public WeChatReaderErrorCode ErrorCode { get; }
}

/// <summary>原生微信本地数据库 reader 边界。</summary>
public interface IWeChatLocalDatabaseReader : IAsyncDisposable
{
    bool IsAvailable { get; }

    bool IsInitialized { get; }

    IReadOnlyList<WeChatReaderStageResult> Diagnostics { get; }

    Task<WeChatInitializationResult> InitializeAsync(
        WeChatInitializationRequest request,
        CancellationToken cancellationToken);

    Task<WeChatInitializationResult> RestoreProtectedKeyAsync(
        WeChatProtectedKeyRestoreRequest request,
        CancellationToken cancellationToken);

    Task ClearProtectedKeyAsync(CancellationToken cancellationToken);

    Task<WeChatReadResult> ReadAsync(
        WeChatReadRequest request,
        CancellationToken cancellationToken);
}
