using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Weekly;

/// <summary>单周窗口内某位成员通过可靠私聊或直接邮件发送的工作内容。</summary>
public sealed record WeeklyContribution(
    ProjectMember Member,
    IReadOnlyList<Message> Messages);

/// <summary>
/// 周报素材：成员直接周报、项目关键词消息和无法归属成员的项目消息分别保存。
/// 两条通道互不替代，避免项目群消息被误记到成员，也避免私聊限制削弱项目整体进展。
/// </summary>
public sealed record WeeklyReportMaterial(
    string ProjectName,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<WeeklyContribution> Contributions,
    IReadOnlyList<Message> Unattributed,
    IReadOnlyList<Message> ProjectMessages,
    WeeklyCollectionDiagnostics Diagnostics);

/// <summary>周报素材归集服务：只做完整取数、路由校验和归人，不生成总结文本。</summary>
public sealed class WeeklyReportCollector
{
    private readonly IMessageRepository _messageRepository;

    public WeeklyReportCollector(IMessageRepository messageRepository)
    {
        _messageRepository = messageRepository ?? throw new ArgumentNullException(nameof(messageRepository));
    }

    /// <summary>
    /// 归集指定项目在完整时间窗口[from,to]内的素材。
    /// 成员消息必须Private+Incoming且wxid/邮箱精确匹配；姓名别名只用于排除诊断。
    /// </summary>
    public async Task<WeeklyReportMaterial> CollectAsync(
        long projectId,
        string projectName,
        IReadOnlyCollection<ProjectMember> members,
        IReadOnlyCollection<FollowedProjectKeyword> keywords,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<string>? excludedSenders = null,
        CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "时间窗口终点不得早于起点。");
        }

        var excluded = excludedSenders ?? Array.Empty<string>();
        // 仓储内部稳定分页读完整个窗口，不能先截断再过滤造成早期周报漏数。
        var candidates = await _messageRepository.GetAllSentBetweenAsync(from, to, cancellationToken);
        var relevant = candidates
            .Where(message => message.Direction != Direction.Outgoing)
            .Where(message => !IsExcluded(message, excluded))
            .ToList();
        var projectMessages = relevant
            .Where(message => FollowedProjectMatcher.Match(message, keywords)?.ProjectId == projectId)
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.Id)
            .ToArray();

        var contributions = new List<WeeklyContribution>();
        var attributed = new HashSet<long>();
        var memberDiagnostics = new List<WeeklyMemberDiagnostic>();
        foreach (var member in members)
        {
            var memberMessages = relevant
                .Where(message => IsDirectToMember(message, member))
                .OrderBy(message => message.SentAt)
                .ThenBy(message => message.Id)
                .ToArray();
            foreach (var message in memberMessages)
            {
                attributed.Add(message.Id);
            }

            contributions.Add(new WeeklyContribution(member, memberMessages));
            memberDiagnostics.Add(new WeeklyMemberDiagnostic(
                member.DisplayName,
                memberMessages.Length,
                !string.IsNullOrWhiteSpace(member.WeChatUserId) || !string.IsNullOrWhiteSpace(member.Email)));
        }

        // 仅对姓名/别名看起来像成员的消息统计排除原因，绝不用这种匹配采用消息。
        var possibleMemberMessages = relevant
            .Where(message => members.Any(member => MatchesMemberName(message, member)))
            .ToArray();
        var unknownExcluded = possibleMemberMessages.Count(message =>
            message.ChatType == ChatType.Unknown || message.Direction == Direction.Unknown);
        var nonPrivateExcluded = possibleMemberMessages.Count(message =>
            message.ChatType != ChatType.Unknown
            && message.Direction != Direction.Unknown
            && (message.ChatType != ChatType.Private || message.Direction != Direction.Incoming));
        var diagnostics = new WeeklyCollectionDiagnostics(
            memberDiagnostics,
            unknownExcluded,
            nonPrivateExcluded,
            projectMessages.Length);
        var unattributed = projectMessages
            .Where(message => !attributed.Contains(message.Id))
            .ToArray();
        return new WeeklyReportMaterial(
            projectName,
            from,
            to,
            contributions,
            unattributed,
            projectMessages,
            diagnostics);
    }

    /// <summary>稳定外部ID与可靠DirectToMe路由必须同时成立，显示名不能替代身份证据。</summary>
    private static bool IsDirectToMember(Message message, ProjectMember member)
    {
        if (message.ChatType != ChatType.Private
            || message.Direction != Direction.Incoming
            || string.IsNullOrWhiteSpace(message.SenderExternalId))
        {
            return false;
        }

        var externalId = message.SenderExternalId.Trim();
        return (!string.IsNullOrWhiteSpace(member.WeChatUserId)
                && string.Equals(externalId, member.WeChatUserId.Trim(), StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(member.Email)
                && string.Equals(externalId, member.Email.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>姓名或别名精确匹配只用于诊断可能的成员消息，不参与周报采用。</summary>
    private static bool MatchesMemberName(Message message, ProjectMember member)
    {
        var sender = message.SenderName?.Trim() ?? string.Empty;
        return sender.Length > 0 && NameTokens(member).Any(token =>
            string.Equals(sender, token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>成员诊断匹配词：姓名加逗号、分号或顿号分隔的别名。</summary>
    private static IEnumerable<string> NameTokens(ProjectMember member)
    {
        if (!string.IsNullOrWhiteSpace(member.DisplayName))
        {
            yield return member.DisplayName.Trim();
        }

        if (string.IsNullOrWhiteSpace(member.Alias)) yield break;
        foreach (var part in member.Alias.Split(',', ';', '、'))
        {
            if (!string.IsNullOrWhiteSpace(part)) yield return part.Trim();
        }
    }

    private static bool IsExcluded(Message message, IReadOnlyCollection<string> excluded) =>
        excluded.Count > 0
        && excluded.Any(name => !string.IsNullOrWhiteSpace(name)
            && string.Equals(message.SenderName?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
}
