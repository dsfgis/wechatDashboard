using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Weekly;

/// <summary>
/// 项目周报编排服务：归集成员直接周报、项目消息和待办，
/// 优先使用模型生成结构化草稿，失败时保留有依据的本地摘要。
/// </summary>
public sealed class WeeklyReportService
{
    // 超过该规模后按成员分批送模，避免长周报被单次请求截断。
    private const int ModelBatchCharacterLimit = 30000;
    private static readonly string[] IssueStatuses = { "打开", "延迟", "继续", "暂停", "关闭" };
    private static readonly string[] RiskStatuses = { "监控中", "风险被缓解，关闭", "风险已发生，转为问题，关闭" };
    private static readonly HashSet<string> Acknowledgements = new(StringComparer.OrdinalIgnoreCase)
    {
        "收到", "好的", "好", "谢谢", "感谢", "辛苦了", "OK", "嗯", "嗯嗯"
    };

    private readonly WeeklyReportCollector _collector;
    private readonly ITodoRepository _todoRepository;
    private readonly IWeeklyReportModel? _model;

    public WeeklyReportService(
        WeeklyReportCollector collector,
        ITodoRepository todoRepository,
        IWeeklyReportModel? model = null)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _todoRepository = todoRepository ?? throw new ArgumentNullException(nameof(todoRepository));
        _model = model;
    }

    /// <summary>生成指定项目周报；完全没有素材时拒绝生成空报告。</summary>
    public async Task<WeeklyReportOutcome> GenerateAsync(
        long projectId,
        string projectName,
        IReadOnlyCollection<ProjectMember> members,
        IReadOnlyCollection<FollowedProjectKeyword> keywords,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset reportDate,
        IReadOnlyCollection<string>? excludedSenders = null,
        CancellationToken cancellationToken = default)
    {
        var reportingMembers = members.Where(member => member.IsReporting).ToArray();
        var material = await _collector.CollectAsync(
            projectId,
            projectName,
            reportingMembers,
            keywords,
            from,
            to,
            excludedSenders,
            cancellationToken);
        var todos = await _todoRepository.GetWeeklyReportAsync(projectId, from, to, cancellationToken);
        var memberMessageCount = material.Contributions.Sum(contribution => contribution.Messages.Count);
        if (memberMessageCount + material.ProjectMessages.Count + todos.Count == 0)
        {
            throw new InvalidOperationException("当前周期没有可用的成员私聊周报、项目消息或项目待办。");
        }

        var metadata = new WeeklyReportMetadata(projectName, from, to, reportDate);
        var modelRequest = new WeeklyReportModelRequest(metadata, material, material.ProjectMessages, todos);
        if (_model is not null)
        {
            try
            {
                var draft = await GenerateWithModelAsync(modelRequest, cancellationToken);
                ValidateDraft(draft, material, todos);
                return new WeeklyReportOutcome(draft, true, null, material.Diagnostics, todos.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 用户主动取消不属于模型失败，不能转而继续导出。
                throw;
            }
            catch (Exception exception)
            {
                var reason = CaptureErrorSanitizer.Sanitize(exception.Message);
                return new WeeklyReportOutcome(
                    BuildFallbackDraft(modelRequest),
                    false,
                    reason,
                    material.Diagnostics,
                    todos.Count);
            }
        }

        return new WeeklyReportOutcome(
            BuildFallbackDraft(modelRequest),
            false,
            "模型未配置",
            material.Diagnostics,
            todos.Count);
    }

    /// <summary>普通规模一次送模；大规模素材按成员和项目补充材料分批后合并。</summary>
    private async Task<WeeklyReportDraft> GenerateWithModelAsync(
        WeeklyReportModelRequest request,
        CancellationToken cancellationToken)
    {
        var characterCount = request.Material.Contributions
            .SelectMany(contribution => contribution.Messages)
            .Concat(request.ProjectMessages)
            .Sum(message => message.Content.Length);
        if (characterCount <= ModelBatchCharacterLimit)
        {
            return await _model!.GenerateAsync(request, cancellationToken);
        }

        var drafts = new List<WeeklyReportDraft>();
        foreach (var contribution in request.Material.Contributions.Where(item => item.Messages.Count > 0))
        {
            var memberMaterial = new WeeklyReportMaterial(
                request.Material.ProjectName,
                request.Material.From,
                request.Material.To,
                new[] { contribution },
                Array.Empty<Message>(),
                Array.Empty<Message>(),
                request.Material.Diagnostics);
            drafts.Add(await _model!.GenerateAsync(
                new WeeklyReportModelRequest(request.Metadata, memberMaterial, Array.Empty<Message>(), Array.Empty<TodoItem>()),
                cancellationToken));
        }

        if (request.ProjectMessages.Count > 0 || request.Todos.Count > 0)
        {
            var projectMaterial = new WeeklyReportMaterial(
                request.Material.ProjectName,
                request.Material.From,
                request.Material.To,
                Array.Empty<WeeklyContribution>(),
                Array.Empty<Message>(),
                Array.Empty<Message>(),
                request.Material.Diagnostics);
            drafts.Add(await _model!.GenerateAsync(
                new WeeklyReportModelRequest(request.Metadata, projectMaterial, request.ProjectMessages, request.Todos),
                cancellationToken));
        }

        return MergeDrafts(request.Metadata, drafts);
    }

    /// <summary>合并分批结果，不额外生成新事实，也不丢弃任何来源条目。</summary>
    private static WeeklyReportDraft MergeDrafts(
        WeeklyReportMetadata metadata,
        IReadOnlyList<WeeklyReportDraft> drafts) => new(
        metadata,
        string.Join("\n", drafts.Select(draft => draft.Summary).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct()),
        drafts.SelectMany(draft => draft.WorkItems).ToArray(),
        drafts.SelectMany(draft => draft.Issues).ToArray(),
        drafts.SelectMany(draft => draft.Risks).ToArray(),
        string.Join("\n", drafts.Select(draft => draft.NextWeekGoal).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct()),
        drafts.SelectMany(draft => draft.Plans).ToArray());

    /// <summary>模型不可用时的保守摘要：只整理原文要点和已有待办，不生成进度或工时。</summary>
    private static WeeklyReportDraft BuildFallbackDraft(WeeklyReportModelRequest request)
    {
        var workItems = new List<WeeklyWorkItem>();
        foreach (var contribution in request.Material.Contributions)
        {
            var messages = contribution.Messages
                .Where(message => !Acknowledgements.Contains(CurrentContent(message.Content).Trim()))
                .OrderBy(message => message.SentAt)
                .ToArray();
            if (messages.Length == 0) continue;
            var lines = messages.Select((message, index) => $"{index + 1}、{CurrentContent(message.Content).Trim()}");
            workItems.Add(new WeeklyWorkItem(
                $"{contribution.Member.DisplayName}：\n{string.Join("\n", lines)}",
                new[] { contribution.Member.DisplayName },
                null,
                null,
                null,
                null,
                messages.Select(message => message.Id).ToArray(),
                Array.Empty<long>()));
        }

        var completed = request.Todos.Where(todo => todo.Status == TodoStatus.Done).ToArray();
        if (completed.Length > 0)
        {
            workItems.Add(new WeeklyWorkItem(
                "本周完成待办：\n" + string.Join("\n", completed.Select((todo, index) => $"{index + 1}、{todo.Title}")),
                Array.Empty<string>(),
                null,
                null,
                null,
                null,
                completed.Where(todo => todo.SourceMessageId.HasValue).Select(todo => todo.SourceMessageId!.Value).ToArray(),
                completed.Select(todo => todo.Id).ToArray()));
        }

        var active = request.Todos.Where(todo => todo.Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting).ToArray();
        var risks = active.Where(todo => todo.Status == TodoStatus.Waiting || todo.DueAt < request.Metadata.To)
            .Select(todo => new WeeklyRisk(
                $"待办“{todo.Title}”当前为{todo.Status}，请关注进展。",
                "请相关负责人核实阻塞或逾期原因并跟进处理。",
                Array.Empty<string>(),
                "监控中",
                todo.SourceMessageId.HasValue ? new[] { todo.SourceMessageId.Value } : Array.Empty<long>(),
                new[] { todo.Id }))
            .ToArray();
        var plans = active.Select(todo => new WeeklyPlan(
            todo.Title,
            "完成该待办并反馈处理结果。",
            Array.Empty<string>(),
            null,
            todo.SourceMessageId.HasValue ? new[] { todo.SourceMessageId.Value } : Array.Empty<long>(),
            new[] { todo.Id })).ToArray();
        var summary = $"本期归集 {request.Material.Contributions.Sum(item => item.Messages.Count)} 条成员直接周报素材、"
            + $"{request.ProjectMessages.Count} 条项目消息和 {request.Todos.Count} 条相关待办；当前为模型不可用时的本地摘要。";
        return new WeeklyReportDraft(
            request.Metadata,
            summary,
            workItems,
            Array.Empty<WeeklyIssue>(),
            risks,
            active.Length == 0 ? string.Empty : "继续跟进未完成待办。",
            plans);
    }

    /// <summary>校验模型来源、数值范围和状态词，任何虚构或无效结果都触发整体回退。</summary>
    private static void ValidateDraft(
        WeeklyReportDraft draft,
        WeeklyReportMaterial material,
        IReadOnlyList<TodoItem> todos)
    {
        var messageIds = material.Contributions.SelectMany(item => item.Messages)
            .Concat(material.ProjectMessages)
            .Select(message => message.Id)
            .ToHashSet();
        var todoIds = todos.Select(todo => todo.Id).ToHashSet();
        // 责任人只能来自已配置成员，防止模型把昵称、引用发送人或虚构姓名写入正式周报。
        var allowedOwners = material.Contributions
            .Select(contribution => contribution.Member.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in draft.WorkItems)
        {
            ValidateSources(item.SourceMessageIds, item.SourceTodoIds, messageIds, todoIds);
            ValidateOwners(item.Owners, allowedOwners);
            ValidateProgress(item.ExpectedProgress);
            ValidateProgress(item.CurrentProgress);
            ValidateHours(item.PlannedHours);
            ValidateHours(item.ActualHours);
        }

        foreach (var item in draft.Issues)
        {
            ValidateSources(item.SourceMessageIds, item.SourceTodoIds, messageIds, todoIds);
            ValidateOwners(item.Owners, allowedOwners);
            if (!IssueStatuses.Contains(item.Status)) throw new InvalidOperationException("模型返回了无效问题状态。");
        }

        foreach (var item in draft.Risks)
        {
            ValidateSources(item.SourceMessageIds, item.SourceTodoIds, messageIds, todoIds);
            ValidateOwners(item.Owners, allowedOwners);
            if (!RiskStatuses.Contains(item.Status)) throw new InvalidOperationException("模型返回了无效风险状态。");
        }

        foreach (var item in draft.Plans)
        {
            ValidateSources(item.SourceMessageIds, item.SourceTodoIds, messageIds, todoIds);
            ValidateOwners(item.Owners, allowedOwners);
            ValidateHours(item.PlannedHours);
        }
    }

    /// <summary>责任人可以为空，但出现的每个姓名都必须是当前项目已配置的周报成员。</summary>
    private static void ValidateOwners(IReadOnlyList<string> owners, HashSet<string> allowedOwners)
    {
        if (owners.Any(owner => string.IsNullOrWhiteSpace(owner) || !allowedOwners.Contains(owner.Trim())))
        {
            throw new InvalidOperationException("模型周报包含未配置或空白的责任人。");
        }
    }

    /// <summary>每个结构化条目至少需要一个真实来源，且所有ID必须属于本次输入。</summary>
    private static void ValidateSources(
        IReadOnlyList<long> messageIds,
        IReadOnlyList<long> todoIds,
        HashSet<long> allowedMessageIds,
        HashSet<long> allowedTodoIds)
    {
        if (messageIds.Count + todoIds.Count == 0
            || messageIds.Any(id => !allowedMessageIds.Contains(id))
            || todoIds.Any(id => !allowedTodoIds.Contains(id)))
        {
            throw new InvalidOperationException("模型周报包含不存在或缺失的来源。");
        }
    }

    private static void ValidateProgress(double? value)
    {
        if (value is < 0 or > 1) throw new InvalidOperationException("模型周报进度超出0到1范围。");
    }

    private static void ValidateHours(double? value)
    {
        if (value < 0) throw new InvalidOperationException("模型周报工时不能为负数。");
    }

    /// <summary>引用正文只作上下文，本地回退不得把引用事项归到当前发送人。</summary>
    private static string CurrentContent(string content)
    {
        var marker = content.IndexOf("\n引用消息：", StringComparison.Ordinal);
        return marker >= 0 ? content[..marker] : content;
    }
}
