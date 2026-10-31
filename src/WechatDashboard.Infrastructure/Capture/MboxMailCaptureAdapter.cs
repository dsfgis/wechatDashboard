using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 石化邮本地缓存采集适配器：读取客户端（Thunderbird 内核）已同步到本机的 mbox 收件箱。
/// <para>
/// 与 IMAP 路径相比这条路径**不需要账号口令**，断网也能用，代价是延迟等于客户端的收信间隔。
/// 首次运行把游标锚定在文件末尾，只采集此后新增的邮件，不回溯 2 GB 历史。
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

    public MboxMailCaptureAdapter(
        MboxMailArchiveReader reader,
        string source,
        int maxMessagesPerRun = 20,
        int maxBodyLength = 4000)
    {
        _reader = reader;
        _source = source;
        _maxMessagesPerRun = maxMessagesPerRun;
        _maxBodyLength = maxBodyLength;
    }

    public string Name => AdapterKey;

    public Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var saved = MboxCursor.Parse(context.GetOffset(Name));
        // 首次运行：只采新增，把起点放在当前文件末尾。
        var offset = saved ?? _reader.ReadLength();
        var result = _reader.ReadNew(offset, _maxMessagesPerRun);

        var messages = new List<CapturedMessage>(result.Messages.Count);
        foreach (var envelope in result.Messages)
        {
            messages.Add(MailMessageMapper.Map(_source, envelope, _maxBodyLength));
        }

        return Task.FromResult(new CaptureBatch(Name, messages, MboxCursor.Format(result.NextOffset)));
    }
}
