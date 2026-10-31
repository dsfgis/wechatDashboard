using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 基于 IMAP4rev1 的石化邮收件箱读取器（MailKit 实现）。
/// <para>
/// 设计要点：
/// 1. 只读打开文件夹（<see cref="FolderAccess.ReadOnly"/>），永不写入、移动或标记已读；
/// 2. 增量游标为 UIDVALIDITY + UID，UIDVALIDITY 变化时只回退读取最近一批而不是全量；
/// 3. 只下载正文部分，附件仅计数，避免大附件拖慢单轮采集；
/// 4. 密码只在内存中短暂存在，不进入任何日志或异常消息。
/// </para>
/// </summary>
public sealed class ImapMailInboxReader : IMailInboxReader
{
    private readonly SinopecMailOptions _options;
    private readonly Func<ImapCredential?> _credentialAccessor;

    /// <param name="options">连接与读取配置。</param>
    /// <param name="credentialAccessor">凭据读取委托；每次采集时调用，便于用户在运行期更新密码。</param>
    public ImapMailInboxReader(SinopecMailOptions options, Func<ImapCredential?> credentialAccessor)
    {
        _options = options;
        _credentialAccessor = credentialAccessor;
    }

    public async Task<MailFetchResult> FetchAsync(MailFetchRequest request, CancellationToken cancellationToken)
    {
        var credential = _credentialAccessor();
        if (credential is null || !credential.IsComplete)
        {
            throw new InvalidOperationException("尚未配置石化邮账号密码，请在采集源设置中填写。");
        }

        using var client = new ImapClient();
        try
        {
            await client.ConnectAsync(
                _options.Host,
                _options.Port,
                SecureSocketOptions.SslOnConnect,
                cancellationToken).ConfigureAwait(false);

            await client.AuthenticateAsync(
                credential.Account,
                credential.Password,
                cancellationToken).ConfigureAwait(false);

            var folder = await ResolveFolderAsync(client, request.Folder, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            var uidValidity = folder.UidValidity;
            var cursorReset = request.UidValidity is null || request.UidValidity.Value != uidValidity;
            var fromUid = cursorReset ? null : request.LastSeenUid;

            var envelopes = await ReadEnvelopesAsync(
                folder,
                fromUid,
                request.MaxMessages,
                request.UnreadOnly,
                cancellationToken).ConfigureAwait(false);

            var nextUid = envelopes.Count == 0
                ? fromUid ?? 1
                : Math.Max(envelopes.Max(item => item.Uid) + 1, fromUid ?? 1);

            await folder.CloseAsync(expunge: false, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);

            return new MailFetchResult(envelopes, uidValidity, nextUid, cursorReset);
        }
        catch (AuthenticationException exception)
        {
            throw new InvalidOperationException(
                $"石化邮登录失败：{_options.Host} 拒绝了账号或密码，或该账号尚未开启 IMAP 权限。",
                exception);
        }
        catch (ImapCommandException exception)
        {
            throw new InvalidOperationException($"石化邮 IMAP 命令被拒绝：{exception.ResponseText}", exception);
        }
        catch (ImapProtocolException exception)
        {
            throw new InvalidOperationException("石化邮 IMAP 协议协商失败，服务器可能限制了该客户端的访问。", exception);
        }
        catch (ServiceNotConnectedException exception)
        {
            throw new InvalidOperationException(
                $"无法连接 {_options.Host}:{_options.Port}，请确认当前在公司内网。",
                exception);
        }
    }

    /// <summary>按配置的文件夹名定位邮箱文件夹；找不到时回退 INBOX。</summary>
    private static async Task<IMailFolder> ResolveFolderAsync(
        ImapClient client,
        string folderName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderName) ||
            string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return client.Inbox;
        }

        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        var folders = await personal.GetSubfoldersAsync(false, cancellationToken).ConfigureAwait(false);
        return folders.FirstOrDefault(folder => string.Equals(folder.Name, folderName, StringComparison.OrdinalIgnoreCase))
            ?? client.Inbox;
    }

    /// <summary>
    /// 读取一批邮件的摘要与正文。游标有效时按 UID 范围向后取，游标重建时只取最近一批。
    /// 始终按 UID 升序推进，保证积压时不会跳过中间邮件。
    /// </summary>
    private async Task<List<MailEnvelope>> ReadEnvelopesAsync(
        IMailFolder folder,
        uint? fromUid,
        int maxMessages,
        bool unreadOnly,
        CancellationToken cancellationToken)
    {
        var limit = maxMessages <= 0 ? 20 : maxMessages;
        var searchQuery = unreadOnly ? SearchQuery.NotSeen : SearchQuery.All;
        var matched = await folder.SearchAsync(searchQuery, cancellationToken).ConfigureAwait(false);
        var ordered = matched.OrderBy(uid => uid.Id).ToList();

        if (fromUid is not null)
        {
            ordered = ordered.Where(uid => uid.Id >= fromUid.Value).ToList();
        }
        else if (ordered.Count > limit)
        {
            // 首次读取或 UID 空间重建：只回溯最近一批，避免一次性拉取整个邮箱。
            ordered = ordered.Skip(ordered.Count - limit).ToList();
        }

        var targets = ordered.Take(limit).ToList();
        if (targets.Count == 0)
        {
            return [];
        }

        var summaries = await folder.FetchAsync(
            targets,
            MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.InternalDate,
            cancellationToken).ConfigureAwait(false);

        var result = new List<MailEnvelope>(summaries.Count);
        foreach (var summary in summaries.OrderBy(item => item.UniqueId.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(await BuildEnvelopeAsync(folder, summary, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    private static async Task<MailEnvelope> BuildEnvelopeAsync(
        IMailFolder folder,
        IMessageSummary summary,
        CancellationToken cancellationToken)
    {
        var envelope = summary.Envelope;
        var body = await ReadBodyTextAsync(folder, summary, cancellationToken).ConfigureAwait(false);
        var from = envelope?.From.Mailboxes.FirstOrDefault();

        return new MailEnvelope(
            Uid: summary.UniqueId.Id,
            MessageId: envelope?.MessageId ?? string.Empty,
            Subject: envelope?.Subject ?? string.Empty,
            FromDisplayName: from?.Name ?? string.Empty,
            FromAddress: from?.Address ?? string.Empty,
            SentAt: envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.Now,
            Body: body,
            AttachmentCount: CountAttachments(summary),
            IsUnread: !summary.Flags.HasValue || !summary.Flags.Value.HasFlag(MessageFlags.Seen));
    }

    /// <summary>优先取 text/plain，其次 text/html 转纯文本；只下载选中的那一个 MIME 部分。</summary>
    private static async Task<string> ReadBodyTextAsync(
        IMailFolder folder,
        IMessageSummary summary,
        CancellationToken cancellationToken)
    {
        var parts = summary.BodyParts?.ToList();
        if (parts is null || parts.Count == 0)
        {
            return string.Empty;
        }

        var plain = parts.FirstOrDefault(part => IsTextPart(part, "plain"));
        var html = parts.FirstOrDefault(part => IsTextPart(part, "html"));
        var target = plain ?? html;
        if (target is null)
        {
            return string.Empty;
        }

        try
        {
            var entity = await folder.GetBodyPartAsync(summary.UniqueId, target, cancellationToken).ConfigureAwait(false);
            if (entity is not TextPart textPart)
            {
                return string.Empty;
            }

            return plain is not null ? textPart.Text : HtmlTextConverter.ToPlainText(textPart.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 单封邮件正文解析失败不应中断整批采集。
            return string.Empty;
        }
    }

    private static bool IsTextPart(BodyPartBasic part, string subtype)
        => string.Equals(part.ContentType.MediaType, "text", StringComparison.OrdinalIgnoreCase)
           && string.Equals(part.ContentType.MediaSubtype, subtype, StringComparison.OrdinalIgnoreCase)
           && part.IsAttachment != true;

    /// <summary>统计附件数量：正文与内联资源不计入，仅统计被标记为附件的部分。</summary>
    private static int CountAttachments(IMessageSummary summary)
        => summary.BodyParts?.Count(part => part.IsAttachment == true) ?? 0;
}
