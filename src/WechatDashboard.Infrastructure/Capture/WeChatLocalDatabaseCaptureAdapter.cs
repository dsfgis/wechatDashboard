using WechatDashboard.Application.Capture;
using System.Text.Json;

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
                WeChatReaderStage.ProtectedKey,
                WeChatReaderErrorCode.ProtectedKeyUnavailable,
                "微信本地数据库尚未初始化，请在“采集诊断”中点击“自动提取 Key”。");
        }

        var committedOffset = WeChatCaptureOffset.Parse(context.GetOffset(Name));
        var result = await _reader.ReadAsync(
            new WeChatReadRequest(
                Start: null,
                End: null,
                Offset: 0,
                Limit: 0,
                CommittedHighWatermark: committedOffset.HighWatermark,
                IncrementalLookback: TimeSpan.FromSeconds(10),
                CommittedSourceFingerprint: committedOffset.SourceFingerprint),
            cancellationToken);
        LastStages = result.Stages;
        return new CaptureBatch(
            Name,
            result.Messages,
            new WeChatCaptureOffset(result.ProposedNextOffset, result.SourceFingerprint).ToString());
    }
}

internal sealed record WeChatCaptureOffset(string HighWatermark, string? SourceFingerprint)
{
    public static WeChatCaptureOffset Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new WeChatCaptureOffset("", null);
        }

        if (value[0] == '{')
        {
            try
            {
                return JsonSerializer.Deserialize<WeChatCaptureOffset>(value) ?? new WeChatCaptureOffset("", null);
            }
            catch (JsonException)
            {
            }
        }

        return new WeChatCaptureOffset(value, null);
    }

    public override string ToString() => JsonSerializer.Serialize(this);
}
