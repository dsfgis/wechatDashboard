using System.Globalization;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Briefing;

/// <summary>
/// 项目简报单次生成结果：简报正文与运行统计。
/// FromModel=false 表示模型不可用或失败，简报由本地统计规则生成。
/// </summary>
/// <param name="Briefing">简报正文（摘要、进展、风险、行动项）。</param>
/// <param name="FromModel">简报是否由模型生成。</param>
/// <param name="MatchedCount">时间窗口内匹配该项目的消息总数。</param>
/// <param name="SentToModelCount">实际送入模型的消息条数（受条数上限约束）。</param>
public sealed record ProjectBriefingOutcome(
    ProjectBriefing Briefing,
    bool FromModel,
    int MatchedCount,
    int SentToModelCount)
{
    /// <summary>待用户批量确认的项目记忆建议；生成简报本身不会写入这些建议。</summary>
    public IReadOnlyList<ProjectMemoryProposal> MemoryProposals { get; init; } =
        Array.Empty<ProjectMemoryProposal>();
}

/// <summary>
/// 项目简报服务：跨微信、石化通、石化邮等来源按时间窗口取消息，
/// 复用关注项目关键词（FollowedProjectMatcher）过滤出与指定项目相关的消息，
/// 送入简报模型总结；模型未配置或失败时回退为本地统计摘要，不向调用方抛出模型异常。
/// </summary>
public sealed class ProjectBriefingService
{
    /// <summary>单次送入模型的最大消息条数；超出时保留最新的一批。</summary>
    public const int MaxModelMessages = 80;

    /// <summary>送入模型的单条正文截断长度。</summary>
    public const int MaxContentChars = 200;

    // 从仓储拉取候选消息的上限；覆盖单用户一天到一周的正常消息量。
    private const int CandidateLimit = 500;

    // 本地回退摘要里风险关键词与行动项的最大条数。
    private const int MaxFallbackItems = 5;

    private static readonly string[] RiskKeywords = { "故障", "阻塞", "事故", "延期", "问题", "风险", "失败" };

    // 完成与废弃属于强状态语义，必须在被引用来源正文中找到明确证据。
    private static readonly string[] CompletionEvidenceKeywords =
        { "完成", "交付", "验收", "已上线", "已发布", "已解决", "已修复" };
    private static readonly string[] DiscardEvidenceKeywords =
        { "取消", "废弃", "不再需要", "不再做", "终止", "撤回" };

    private readonly IMessageRepository _messageRepository;
    private readonly IProjectBriefingModel? _model;
    private readonly IProjectMemoryRepository? _memoryRepository;

    public ProjectBriefingService(
        IMessageRepository messageRepository,
        IProjectBriefingModel? model = null,
        IProjectMemoryRepository? memoryRepository = null)
    {
        _messageRepository = messageRepository ?? throw new ArgumentNullException(nameof(messageRepository));
        _model = model;
        // 旧调用方可以不接入记忆仓储，此时仅保留原有文本简报能力。
        _memoryRepository = memoryRepository;
    }

    /// <summary>
    /// 生成指定项目在时间窗口 [from, to] 内的跨源简报。
    /// keywords 为全部关注项目关键词（与待办归并同一套匹配语义），本方法只保留命中该项目的消息。
    /// </summary>
    public async Task<ProjectBriefingOutcome> GenerateAsync(
        long projectId,
        string projectName,
        IReadOnlyCollection<FollowedProjectKeyword> keywords,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ArgumentException("项目名不能为空。", nameof(projectName));
        }

        if (to < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "时间窗口终点不得早于起点。");
        }

        // 候选按发送时间倒序返回；匹配语义与待办归并一致：群名或正文包含关键词。
        var candidates = await _messageRepository.GetSentBetweenAsync(from, to, CandidateLimit, cancellationToken);
        var matched = candidates
            .Where(message => FollowedProjectMatcher.Match(message, keywords)?.ProjectId == projectId)
            .ToList();

        if (matched.Count == 0)
        {
            // 没有匹配消息时直接给本地结果，不产生一次模型调用费用。
            return new ProjectBriefingOutcome(
                BuildEmptyBriefing(projectName, from, to),
                FromModel: false,
                MatchedCount: 0,
                SentToModelCount: 0);
        }

        // 既有事项严格按当前项目读取，跨项目 ID 永远不会进入模型白名单。
        IReadOnlyList<ProjectMemoryItem> existingItems = _memoryRepository is null
            ? Array.Empty<ProjectMemoryItem>()
            : await _memoryRepository.GetByProjectAsync(projectId, cancellationToken);

        // 超出条数上限时保留最新的一批，再恢复时间正序，方便模型按时间线总结。
        var selected = matched.Take(MaxModelMessages).Reverse().ToList();
        var request = new ProjectBriefingRequest(
            projectName,
            from,
            to,
            selected.Select(ToBriefingMessage).ToList())
        {
            ExistingItems = existingItems.Select(item => new ProjectBriefingExistingItem(
                item.Id,
                item.Category,
                item.Title,
                item.Status)).ToArray()
        };

        if (_model is not null)
        {
            try
            {
                var briefing = await _model.SummarizeProjectAsync(request, cancellationToken);
                // 任一伪造 ID、无证据状态或非法迁移都会使模型结果整体回退，避免部分污染。
                var proposals = CreateModelProposals(briefing.Items, existingItems, selected);
                return new ProjectBriefingOutcome(briefing, FromModel: true, matched.Count, selected.Count)
                {
                    MemoryProposals = proposals
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 调用方主动取消必须继续传播，不能伪装成模型失败。
                throw;
            }
            catch (Exception exception)
            {
                // 模型未配置、超时、网络错误或响应无效：回退本地统计摘要，不阻断界面。
                System.Diagnostics.Debug.WriteLine(
                    $"Project briefing model failed: {CaptureErrorSanitizer.Sanitize(exception.Message)}");
            }
        }

        return new ProjectBriefingOutcome(
            BuildFallbackBriefing(projectName, matched, from, to),
            FromModel: false,
            matched.Count,
            selected.Count)
        {
            // 本地规则只识别明确风险和 @我 行动，不猜测完成、搁置或废弃。
            MemoryProposals = BuildFallbackProposals(matched)
        };
    }

    /// <summary>把消息实体转为送入模型的有界简报消息。</summary>
    private static BriefingMessage ToBriefingMessage(Message message) => new(
        message.Source,
        Limit(message.ChatName, 100),
        Limit(message.SenderName, 100),
        Limit(message.Content, MaxContentChars),
        message.SentAt,
        message.Id);

    /// <summary>没有匹配消息时的空简报。</summary>
    private static ProjectBriefing BuildEmptyBriefing(string projectName, DateTimeOffset from, DateTimeOffset to)
    {
        var window = FormatWindow(from, to);
        return new ProjectBriefing(
            $"{projectName} 在 {window} 内没有匹配关注关键词的消息，未生成简报。",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
    }

    /// <summary>
    /// 模型不可用时的本地统计摘要：按来源/群/发送人聚合，
    /// 用关键词启发式提取风险条目，@我 消息进入行动项。
    /// </summary>
    private static ProjectBriefing BuildFallbackBriefing(
        string projectName,
        IReadOnlyList<Message> matched,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var window = FormatWindow(from, to);

        // 按来源统计，输出形如"微信 12 条、石化通 3 条"。
        var sourceStats = matched
            .GroupBy(message => message.Source, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} {group.Count()} 条")
            .ToArray();

        // 覆盖的群/会话，最多列 5 个。
        var chats = matched
            .Select(message => message.ChatName)
            .Where(chat => !string.IsNullOrWhiteSpace(chat))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(chat => chat, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFallbackItems)
            .ToArray();

        var summary = $"{projectName} 在 {window} 内共有 {matched.Count} 条相关消息"
            + (sourceStats.Length == 0 ? "。" : $"（{string.Join("、", sourceStats)}）。模型不可用，以下为本地统计摘要。");

        // 风险启发式：正文包含风险关键词的最新消息。
        var risks = matched
            .Where(message => RiskKeywords.Any(keyword => message.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Take(MaxFallbackItems)
            .Select(message => $"[{FormatTimestamp(message.SentAt)}] {Limit(message.Content, 80)}")
            .ToList();

        // 行动项启发式：@我 消息即可能需要当前用户跟进。
        var actions = matched
            .Where(message => message.IsMentionMe)
            .Take(MaxFallbackItems)
            .Select(message => $"[{FormatTimestamp(message.SentAt)}] {Limit(message.Content, 80)}")
            .ToList();

        var highlights = chats.Length == 0
            ? Array.Empty<string>()
            : new[] { $"消息覆盖 {chats.Length} 个群聊：{string.Join("、", chats)}" };

        return new ProjectBriefing(summary, highlights, risks, actions);
    }

    /// <summary>
    /// 把模型事项转换为可确认建议，并验证稳定 ID、来源白名单和关键状态证据。
    /// 验证失败抛出异常，由 GenerateAsync 统一执行本地简报降级。
    /// </summary>
    private static IReadOnlyList<ProjectMemoryProposal> CreateModelProposals(
        IReadOnlyList<ProjectBriefingItem> modelItems,
        IReadOnlyList<ProjectMemoryItem> existingItems,
        IReadOnlyList<Message> selectedMessages)
    {
        var existingById = existingItems.ToDictionary(item => item.Id);
        var messagesById = selectedMessages.ToDictionary(message => message.Id);
        var proposals = new List<ProjectMemoryProposal>(modelItems.Count);

        foreach (var item in modelItems)
        {
            if (!Enum.IsDefined(item.Category) || !Enum.IsDefined(item.ProposedStatus))
            {
                throw new InvalidOperationException("模型简报包含无效的事项类别或状态。");
            }

            var title = item.Title.Trim();
            var reason = item.Reason.Trim();
            var sourceIds = item.SourceMessageIds.Distinct().ToArray();
            if (title.Length == 0 || reason.Length == 0 || sourceIds.Length == 0
                || sourceIds.Any(id => id <= 0 || !messagesById.ContainsKey(id)))
            {
                throw new InvalidOperationException("模型简报事项缺少有效标题、原因或本次输入来源证据。");
            }

            ProjectMemoryItem? existing = null;
            if (item.ItemId is long itemId)
            {
                // 既有 ID 必须来自当前项目请求快照，不能引用其他项目或伪造主键。
                if (!existingById.TryGetValue(itemId, out existing))
                {
                    throw new InvalidOperationException($"模型简报引用了未知或跨项目事项 ID {itemId}。");
                }

                if (IsTerminal(existing.Status) && item.ProposedStatus != existing.Status)
                {
                    throw new InvalidOperationException($"模型不得重开终态事项“{existing.Title}”。");
                }
            }

            var evidence = sourceIds.Select(id => messagesById[id]).ToArray();
            EnsureStrongStatusEvidence(item.ProposedStatus, evidence);
            proposals.Add(new ProjectMemoryProposal(
                item.ItemId,
                item.Category,
                title,
                existing?.Status,
                item.ProposedStatus,
                reason,
                sourceIds));
        }

        return proposals;
    }

    /// <summary>完成和废弃不能仅凭模型判断，必须由至少一条引用消息直接支持。</summary>
    private static void EnsureStrongStatusEvidence(
        ProjectMemoryStatus status,
        IReadOnlyList<Message> evidence)
    {
        var keywords = status switch
        {
            ProjectMemoryStatus.Completed => CompletionEvidenceKeywords,
            ProjectMemoryStatus.Discarded => DiscardEvidenceKeywords,
            _ => Array.Empty<string>()
        };
        if (keywords.Length > 0
            && !evidence.Any(message => keywords.Any(keyword =>
                message.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException($"状态 {status} 缺少明确的来源正文证据。");
        }
    }

    /// <summary>本地降级仅形成保守的新事项建议，不修改任何既有事项状态。</summary>
    private static IReadOnlyList<ProjectMemoryProposal> BuildFallbackProposals(
        IReadOnlyList<Message> matched)
    {
        var proposals = new List<ProjectMemoryProposal>();
        var usedMessageIds = new HashSet<long>();

        // 风险消息对应 Blocked，仍须由用户在确认表格中决定是否入库。
        foreach (var message in matched.Where(message =>
                     RiskKeywords.Any(keyword => message.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                     .Take(MaxFallbackItems))
        {
            proposals.Add(new ProjectMemoryProposal(
                null,
                ProjectMemoryCategory.Risk,
                ToProposalTitle(message),
                null,
                ProjectMemoryStatus.Blocked,
                "本地规则检测到明确风险关键词，请人工确认。",
                new[] { message.Id }));
            usedMessageIds.Add(message.Id);
        }

        // @我 消息只生成 Open 行动建议，不推断完成、搁置、废弃或既有事项变化。
        foreach (var message in matched.Where(message => message.IsMentionMe && !usedMessageIds.Contains(message.Id))
                     .Take(MaxFallbackItems))
        {
            proposals.Add(new ProjectMemoryProposal(
                null,
                ProjectMemoryCategory.Action,
                ToProposalTitle(message),
                null,
                ProjectMemoryStatus.Open,
                "本地规则检测到 @我 行动消息，请人工确认。",
                new[] { message.Id }));
        }

        return proposals;
    }

    /// <summary>将消息正文折叠为单行短标题，避免本地建议携带大段正文。</summary>
    private static string ToProposalTitle(Message message)
    {
        var title = string.Join(' ', message.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length == 0 ? $"{message.ChatName} 项目消息" : Limit(title, 80);
    }

    private static bool IsTerminal(ProjectMemoryStatus status) =>
        status is ProjectMemoryStatus.Completed or ProjectMemoryStatus.Discarded;

    private static string FormatWindow(DateTimeOffset from, DateTimeOffset to) =>
        $"{from.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)} 至 {to.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}";

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
