using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 石化邮采集适配器（IMAP 路径）：把 <see cref="IMailInboxReader"/> 读到的邮件映射为统一的
/// <see cref="CapturedMessage"/>，并维护 <c>uidValidity:nextUid</c> 游标。
/// 字段映射复用 <see cref="MailMessageMapper"/>，与本地缓存路径保持完全一致。
/// </summary>
public sealed class SinopecMailCaptureAdapter : IMessageCaptureAdapter
{
    private readonly IMailInboxReader _reader;
    private readonly SinopecMailOptions _options;

    public SinopecMailCaptureAdapter(IMailInboxReader reader, SinopecMailOptions options)
    {
        _reader = reader;
        _options = options;
    }

    /// <summary>适配器名称同时作为偏移量存储的键；与消息来源标识分离，便于同一来源存在多条读取路径。</summary>
    public string Name => string.IsNullOrWhiteSpace(_options.Source)
        ? MailSourcePolicy.SinopecMail
        : _options.Source;

    public async Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("石化邮采集源尚未配置登录账号。");
        }

        var (uidValidity, lastUid) = MailCursor.Parse(context.GetOffset(Name));
        var result = await _reader.FetchAsync(
            new MailFetchRequest(
                Folder: _options.Folder,
                UidValidity: uidValidity,
                LastSeenUid: lastUid,
                MaxMessages: _options.MaxMessagesPerRun,
                UnreadOnly: _options.UnreadOnly),
            cancellationToken);

        var messages = new List<CapturedMessage>(result.Messages.Count);
        foreach (var envelope in result.Messages)
        {
            messages.Add(MailMessageMapper.Map(Name, envelope, _options.MaxBodyLength));
        }

        return new CaptureBatch(Name, messages, MailCursor.Format(result.UidValidity, result.NextUid));
    }
}
