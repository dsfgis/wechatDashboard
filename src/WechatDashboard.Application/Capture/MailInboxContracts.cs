namespace WechatDashboard.Application.Capture;

/// <summary>
/// 一次邮件增量读取请求。游标由 <paramref name="UidValidity"/> 与 <paramref name="LastSeenUid"/> 共同表达：
/// UIDVALIDITY 变化意味着服务器重建了 UID 空间，此前保存的 UID 全部失效。
/// </summary>
/// <param name="Folder">要读取的邮箱文件夹，通常是 INBOX。</param>
/// <param name="UidValidity">上次读取时记录的 UIDVALIDITY，首次读取为 null。</param>
/// <param name="LastSeenUid">上次读取到的最大 UID，首次读取为 null。</param>
/// <param name="MaxMessages">本次最多返回的邮件数，用于限制单轮耗时与模型调用量。</param>
/// <param name="UnreadOnly">是否只读取未读邮件。</param>
public sealed record MailFetchRequest(
    string Folder,
    long? UidValidity,
    uint? LastSeenUid,
    int MaxMessages,
    bool UnreadOnly);

/// <summary>
/// 一封已解析邮件的必要字段（正文已转为纯文本，附件只保留数量）。
/// </summary>
/// <param name="Uid">IMAP UID，在同一 UIDVALIDITY 下单调递增且唯一。</param>
/// <param name="MessageId">RFC 5322 Message-ID，作为跨源去重键。</param>
/// <param name="Subject">原始主题（未清洗）。</param>
/// <param name="FromDisplayName">发件人显示名，缺失时回退为地址。</param>
/// <param name="FromAddress">发件人邮件地址。</param>
/// <param name="SentAt">邮件 Date 头解析出的发送时间。</param>
/// <param name="Body">已转为纯文本的正文。</param>
/// <param name="AttachmentCount">附件数量，仅计数，不下载内容。</param>
/// <param name="IsUnread">是否未读。</param>
public sealed record MailEnvelope(
    uint Uid,
    string MessageId,
    string Subject,
    string FromDisplayName,
    string FromAddress,
    DateTimeOffset SentAt,
    string Body,
    int AttachmentCount,
    bool IsUnread);

/// <summary>一次邮件读取的结果与更新后的游标。</summary>
/// <param name="Messages">本次读取到的邮件，按 UID 升序。</param>
/// <param name="UidValidity">本次读取确认的 UIDVALIDITY。</param>
/// <param name="NextUid">下次读取的起始 UID（本次最大 UID + 1）。</param>
/// <param name="CursorReset">UIDVALIDITY 已变化，本次是从头重建的结果。</param>
public sealed record MailFetchResult(
    IReadOnlyList<MailEnvelope> Messages,
    long UidValidity,
    uint NextUid,
    bool CursorReset);

/// <summary>
/// 邮件收件箱读取抽象。协议细节留在基础设施层，采集管线只依赖该契约，
/// 使适配器可以在没有任何真实邮件服务器的情况下被测试。
/// </summary>
public interface IMailInboxReader
{
    /// <summary>读取一次邮件增量。</summary>
    Task<MailFetchResult> FetchAsync(MailFetchRequest request, CancellationToken cancellationToken);
}
