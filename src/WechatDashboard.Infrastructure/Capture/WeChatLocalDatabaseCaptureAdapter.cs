using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>直接调用原生 C# reader 的微信本地数据库采集适配器。</summary>
public sealed class WeChatLocalDatabaseCaptureAdapter : IMessageCaptureAdapter
{
    private readonly IWeChatLocalDatabaseReader _reader;

    public WeChatLocalDatabaseCaptureAdapter(IWeChatLocalDatabaseReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public string Name => "WeChat.LocalDatabase";

    public IReadOnlyList<WeChatReaderStageResult> LastStages { get; private set; } =
        Array.Empty<WeChatReaderStageResult>();

    public async Task<CaptureBatch> CaptureAsync(
        CaptureContext context,
        CancellationToken cancellationToken)
    {
        if (!_reader.IsInitialized)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.KeyValidation,
                WeChatReaderErrorCode.KeyInvalid,
                "The native WeChat reader has not been initialized.");
        }

        var committedOffset = context.GetOffset(Name);
        var result = await _reader.ReadAsync(
            new WeChatReadRequest(
                Start: null,
                End: null,
                Offset: 0,
                Limit: 0,
                CommittedHighWatermark: committedOffset,
                IncrementalLookback: TimeSpan.FromSeconds(10)),
            cancellationToken);
        LastStages = result.Stages;
        return new CaptureBatch(Name, result.Messages, result.ProposedNextOffset);
    }
}
