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
    public static CapturedMessage Map(string source, MailEnvelope envelope, int maxBodyLength)
    {
        var subject = MailSourcePolicy.CleanSubject(envelope.Subject);
        var address = string.IsNullOrWhiteSpace(envelope.FromAddress)
            ? "unknown-sender"
            : envelope.FromAddress.Trim();
        var sender = string.IsNullOrWhiteSpace(envelope.FromDisplayName)
            ? address
            : envelope.FromDisplayName.Trim();

        return new CapturedMessage(
            Source: source,
            SourceMessageKey: BuildSourceKey(envelope),
            ChatId: address,
            ChatName: subject.Length > 0 ? subject : "(无主题)",
            SenderName: sender,
            Content: BuildContent(envelope, maxBodyLength),
            SentAt: envelope.SentAt,
            MessageType: MessageType.Text);
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
