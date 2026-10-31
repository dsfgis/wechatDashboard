using System.IO;
using MimeKit;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>一次 mbox 增量读取的结果。</summary>
/// <param name="Messages">解析出的邮件。</param>
/// <param name="NextOffset">下次读取的字节偏移。</param>
/// <param name="Reset">文件被重写（长度回退）导致游标重建。</param>
public sealed record MboxReadResult(IReadOnlyList<MailEnvelope> Messages, long NextOffset, bool Reset);

/// <summary>
/// 石化邮客户端本地缓存（Thunderbird mbox）的增量读取器。
/// <para>
/// 采用文件偏移做游标：只解析上次结束位置之后新增的字节，从不重扫整个 2 GB 邮箱。
/// mbox 用 <c>From </c> 行分隔消息，正文中的同名行会被客户端转义为 <c>&gt;From </c>，
/// 因此该分隔符是可靠的。解析交给 MimeKit 的 Mbox 解析器，MIME、RFC 2047 与字符集都无需自行处理。
/// </para>
/// </summary>
public sealed class MboxMailArchiveReader
{
    /// <summary>单条消息允许加载的最大字节数，超出则跳过，避免超大附件把进程内存打满。</summary>
    private const long MaxMessageBytes = 32L * 1024 * 1024;

    private readonly string _path;

    public MboxMailArchiveReader(string path) => _path = path;

    public bool Exists => File.Exists(_path);

    public string Path => _path;

    /// <summary>当前文件字节长度；文件不存在时为 0。</summary>
    public long ReadLength()
    {
        try
        {
            return Exists ? new FileInfo(_path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 从 <paramref name="offset"/> 起读取最多 <paramref name="maxMessages"/> 封新邮件。
    /// 文件被压缩重写（长度小于游标）时重置游标，依靠 Message-ID 去重避免重复入库。
    /// </summary>
    public MboxReadResult ReadNew(long offset, int maxMessages)
    {
        if (!Exists)
        {
            throw new FileNotFoundException("未找到石化邮本地邮件缓存文件。", _path);
        }

        var limit = maxMessages <= 0 ? 20 : maxMessages;
        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            // 客户端可能正在写入，必须允许共享读写。
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.SequentialScan);

        var length = stream.Length;
        var reset = false;
        var start = offset;
        if (start < 0 || start > length)
        {
            // 文件变短说明被客户端压缩重写过，原偏移失去意义。
            start = 0;
            reset = true;
        }

        if (start == length)
        {
            return new MboxReadResult([], length, reset);
        }

        stream.Seek(start, SeekOrigin.Begin);
        var parser = new MimeParser(stream, MimeFormat.Mbox);
        var messages = new List<MailEnvelope>();
        var position = start;
        var sequence = 0;

        while (!parser.IsEndOfStream && messages.Count < limit)
        {
            var messageStart = parser.Position;
            MimeMessage message;
            try
            {
                message = parser.ParseMessage();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 尾部可能是客户端正在写入的半条消息，保留游标等待下一轮。
                break;
            }

            var messageEnd = parser.Position;
            var envelope = ToEnvelope(message, ref sequence);

            // 没有 Message-ID 的消息无法稳定去重：可能是半条写入，回退游标而不是超前推进。
            if (envelope is null)
            {
                position = messageStart;
                break;
            }

            messages.Add(envelope);
            position = messageEnd;

            if (messageEnd - messageStart > MaxMessageBytes)
            {
                // 超大消息已解析但正文会按上限截断，这里只记录跳过，不影响游标。
                continue;
            }
        }

        return new MboxReadResult(messages, position, reset);
    }

    /// <summary>把 MimeMessage 转成统一的邮件快照；缺少 Message-ID 时返回 null 由调用方回退游标。</summary>
    private static MailEnvelope? ToEnvelope(MimeMessage message, ref int sequence)
    {
        var messageId = message.MessageId?.Trim();
        if (string.IsNullOrEmpty(messageId))
        {
            return null;
        }

        sequence++;
        var from = message.From.Mailboxes.FirstOrDefault();
        var body = ReadBody(message);

        return new MailEnvelope(
            Uid: (uint)sequence,
            MessageId: messageId,
            Subject: message.Subject ?? string.Empty,
            FromDisplayName: from?.Name ?? string.Empty,
            FromAddress: from?.Address ?? string.Empty,
            SentAt: message.Date == default ? DateTimeOffset.Now : message.Date,
            Body: body,
            AttachmentCount: CountAttachments(message),
            IsUnread: !message.Headers.Contains(HeaderId.Status) || !IsSeen(message));
    }

    /// <summary>优先 text/plain；仅当没有纯文本时才把 HTML 转成文本。</summary>
    private static string ReadBody(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            return message.TextBody;
        }

        return string.IsNullOrWhiteSpace(message.HtmlBody)
            ? string.Empty
            : HtmlTextConverter.ToPlainText(message.HtmlBody);
    }

    /// <summary>统计附件数量：内联资源与正文部分不计入。</summary>
    private static int CountAttachments(MimeMessage message)
    {
        var count = 0;
        foreach (var part in message.Attachments)
        {
            if (part.IsAttachment)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>mbox 的 Status 头记录已读状态（R = 已读）。</summary>
    private static bool IsSeen(MimeMessage message)
    {
        var status = message.Headers["Status"];
        return !string.IsNullOrEmpty(status) && status.Contains('R');
    }
}
