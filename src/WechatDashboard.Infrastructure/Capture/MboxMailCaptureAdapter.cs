using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 石化邮本地缓存采集适配器：读取客户端（Thunderbird 内核）已同步到本机的 mbox 收件箱。
/// <para>
/// 与 IMAP 路径相比这条路径**不需要账号口令**，断网也能用，代价是延迟等于客户端的收信间隔。
/// 首次运行回溯最近 <see cref="DefaultBackfillMessages"/> 封已收邮件（窗口受字节上限约束），
/// 之后按文件偏移增量读取，不重复解析整个邮箱。
/// </para>
/// </summary>
public sealed class MboxMailCaptureAdapter : IMessageCaptureAdapter
{
    /// <summary>偏移量存储键。与消息来源标识分开，避免和 IMAP 路径共用同一个游标。</summary>
    public const string AdapterKey = "SinopecMail.LocalCache";

    private readonly MboxMailArchiveReader _reader;
    private readonly string _source;
    private readonly int _maxMessagesPerRun;
    private readonly int _maxBodyLength;
    private readonly string? _currentAccount;
    private readonly int _backfillMessages;
    private readonly long _backfillBytes;

    /// <summary>首次运行回溯的默认封数：足够覆盖近几周的项目邮件，又不会一次解析整年历史。</summary>
    public const int DefaultBackfillMessages = 200;

    /// <summary>首次运行回溯的默认字节窗口上限（48 MB）。</summary>
    public const long DefaultBackfillBytes = 48L * 1024 * 1024;

    /// <param name="reader">mbox 读取器。</param>
    /// <param name="source">消息来源标识。</param>
    /// <param name="maxMessagesPerRun">增量轮次单次最多读取的邮件数。</param>
    /// <param name="maxBodyLength">正文保留字符上限。</param>
    /// <param name="currentAccount">当前采集账号，用于唯一 To 判定。</param>
    /// <param name="backfillMessages">首次运行回溯封数；0 表示不回溯，直接锚定文件末尾。</param>
    /// <param name="backfillBytes">首次运行回溯的字节窗口上限。</param>
    public MboxMailCaptureAdapter(
        MboxMailArchiveReader reader,
        string source,
        int maxMessagesPerRun = 20,
        int maxBodyLength = 4000,
        string? currentAccount = null,
        int backfillMessages = DefaultBackfillMessages,
        long backfillBytes = DefaultBackfillBytes)
    {
        _reader = reader;
        _source = source;
        _maxMessagesPerRun = maxMessagesPerRun;
        _maxBodyLength = maxBodyLength;
        // 未配置账号时保留 null，映射层会安全地将无法判别的邮件标为 Unknown。
        _currentAccount = currentAccount;
        _backfillMessages = backfillMessages;
        _backfillBytes = backfillBytes;
    }

    public string Name => AdapterKey;

    public Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var saved = MboxCursor.Parse(context.GetOffset(Name));
        // 首次运行回溯客户端已经收到的邮件，而不是直接锚定末尾只收录此后新增的邮件。
        var result = saved is null
            ? _reader.ReadTail(_backfillMessages, _backfillBytes)
            : _reader.ReadNew(saved.Value, _maxMessagesPerRun);

        var messages = new List<CapturedMessage>(result.Messages.Count);
        foreach (var envelope in result.Messages)
        {
            messages.Add(MailMessageMapper.Map(_source, envelope, _maxBodyLength, _currentAccount));
        }

        return Task.FromResult(new CaptureBatch(Name, messages, MboxCursor.Format(result.NextOffset)));
    }
}
