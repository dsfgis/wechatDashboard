using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 邮件到统一消息的公共映射。IMAP 与本地缓存两条读取路径共用同一套字段语义，
/// 保证同一封邮件无论从哪条路进来，进消息流后的样子、去重键和待办标题都一致。
/// </summary>
internal static class MailMessageMapper
{
    /// <summary>
    /// 单封邮件映射为 <see cref="CapturedMessage"/>：主题进 <c>ChatName</c>（待办标题与消息流都读它），
    /// 发件人地址作 <c>ChatId</c>，Message-ID 作跨源去重键。
    /// </summary>
    /// <param name="source">写入消息流与数据库的来源标识。</param>
    /// <param name="envelope">已解析的邮件。</param>
    /// <param name="maxBodyLength">正文保留的最大字符数。</param>
    /// <param name="currentAccount">当前采集账号的完整邮箱地址，用于判断是否为唯一直接收件人。</param>
    public static CapturedMessage Map(
        string source,
        MailEnvelope envelope,
        int maxBodyLength,
        string? currentAccount = null)
    {
        var subject = MailSourcePolicy.CleanSubject(envelope.Subject);
        var address = string.IsNullOrWhiteSpace(envelope.FromAddress)
            ? "unknown-sender"
            : envelope.FromAddress.Trim();
        var sender = string.IsNullOrWhiteSpace(envelope.FromDisplayName)
            ? address
            : envelope.FromDisplayName.Trim();

        var to = NormalizeAddresses(envelope.To);
        var cc = NormalizeAddresses(envelope.Cc);
        var bcc = NormalizeAddresses(envelope.Bcc);
        var account = currentAccount?.Trim() ?? string.Empty;
        var isDirectToAccount = account.Length > 0
            && to.Count == 1
            && string.Equals(to[0], account, StringComparison.OrdinalIgnoreCase)
            && cc.Count == 0
            && bcc.Count == 0;

        // 有抄送/密送、多个 To，或唯一 To 明确不是当前账号时，只能确认是多人/非直接邮件。
        // 账号缺失或收件人信息不足时保持 Unknown，不把“落在收件箱”误当成私聊证据。
        var isClearlyNonDirect = cc.Count > 0
            || bcc.Count > 0
            || to.Count > 1
            || (account.Length > 0 && to.Count == 1 && !isDirectToAccount);
        var chatType = isDirectToAccount
            ? ChatType.Private
            : isClearlyNonDirect ? ChatType.Group : ChatType.Unknown;
        var direction = chatType == ChatType.Unknown ? Direction.Unknown : Direction.Incoming;

        return new CapturedMessage(
            Source: source,
            SourceMessageKey: BuildSourceKey(envelope),
            ChatId: address,
            ChatName: subject.Length > 0 ? subject : "(无主题)",
            SenderName: sender,
            Content: BuildContent(envelope, maxBodyLength),
            SentAt: envelope.SentAt,
            MessageType: MessageType.Text,
            // 只有唯一 To 等于当前账号且无 Cc/Bcc 时，才保留发件人邮箱作为直接消息身份。
            SenderExternalId: isDirectToAccount && !string.IsNullOrWhiteSpace(envelope.FromAddress)
                ? envelope.FromAddress.Trim()
                : null,
            ChatType: chatType,
            Direction: direction);
    }

    /// <summary>邮箱地址修剪、去空白并按不区分大小写去重，使重复头项不影响“唯一 To”判断。</summary>
    private static IReadOnlyList<string> NormalizeAddresses(IReadOnlyList<string>? addresses)
    {
        return (addresses ?? Array.Empty<string>())
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 去重键优先使用 Message-ID（RFC 5322 全局唯一，跨 UIDVALIDITY 与文件重写都稳定）；
    /// 缺失时回退到 UID，代价是同一封邮件可能被重复计入，属于可接受的降级。
    /// </summary>
    public static string BuildSourceKey(MailEnvelope envelope)
    {
        var messageId = envelope.MessageId?.Trim().Trim('<', '>');
        return string.IsNullOrWhiteSpace(messageId) ? $"uid:{envelope.Uid}" : messageId;
    }

    /// <summary>正文按上限截断，并在有附件时追加计数占位，不下载附件内容。</summary>
    public static string BuildContent(MailEnvelope envelope, int maxBodyLength)
    {
        var body = envelope.Body?.Trim() ?? string.Empty;
        var limit = maxBodyLength <= 0 ? 4000 : maxBodyLength;
        if (body.Length > limit)
        {
            body = body[..limit] + "\n…（正文已截断）";
        }

        if (envelope.AttachmentCount > 0)
        {
            body = body.Length == 0
                ? $"[附件 {envelope.AttachmentCount} 个]"
                : $"{body}\n[附件 {envelope.AttachmentCount} 个]";
        }

        return body.Length == 0 ? "(无正文)" : body;
    }
}
