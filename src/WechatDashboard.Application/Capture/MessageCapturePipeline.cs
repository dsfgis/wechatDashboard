using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Capture;

/// <summary>Agent 对 @我 消息的分析结果，控制是否创建并覆盖自动待办的优先级、说明和时间提醒。</summary>
/// <param name="Priority">仅用于自动待办的模型优先级。</param>
/// <param name="Description">模型生成的待办说明；空白时回退默认说明。</param>
/// <param name="ShouldCreate">是否值得自动创建待办；默认 true 兼容未启用筛选的调用。</param>
/// <param name="DueAt">消息中识别出的明确完成/截止时间；空白时回退本地日期规则。</param>
/// <param name="FirstReminderAt">消息中识别出的明确提醒时间；空白时按会议/任务规则推导。</param>
public sealed record MentionMessageAnalysis(
    PriorityLevel Priority,
    string? Description,
    bool ShouldCreate = true,
    DateTimeOffset? DueAt = null,
    DateTimeOffset? FirstReminderAt = null);

/// <summary>
/// 消息采集管线：协调多个适配器完成"采集 -> 去重 -> 持久化 -> 分类 -> 紧急度评分 -> 自动建待办"的完整流程。
/// 每次运行会依次执行各启用的适配器，并将处理进度（偏移量）持久化以便增量采集。
/// </summary>
public sealed class MessageCapturePipeline
{
    // 已注册的采集适配器列表
    private readonly IReadOnlyList<IMessageCaptureAdapter> _adapters;
    private readonly IMessageProcessingUnitOfWork _messageProcessingUnitOfWork;
    private readonly IProcessingOffsetRepository _offsetRepository;
    private readonly MentionDetector _mentionDetector;
    private readonly ProjectClassifier _projectClassifier;
    private readonly UrgencyRanker _urgencyRanker;
    private readonly Func<Message, long?> _managedProjectMatcher;
    // Agent 分析依赖均为可选，未装配时完整保留原自动待办行为。
    private readonly IMessageRepository? _messageRepository;
    private readonly Func<bool> _analyzeMessageContent;
    private readonly Func<Message, CancellationToken, Task<MentionMessageAnalysis>>? _analyzeMentionAsync;
    // 跨群去重：命中既有待办时把消息合并进去，不再重复建待办。
    private readonly Func<Message, CancellationToken, Task<TodoItem?>>? _detectDuplicateAsync;
    // 待办创建时刻的可注入时钟；默认取系统当前时间，测试可注入固定时刻消除时间敏感性。
    private readonly Func<DateTimeOffset> _clock;

    public MessageCapturePipeline(
        IEnumerable<IMessageCaptureAdapter> adapters,
        IMessageProcessingUnitOfWork messageProcessingUnitOfWork,
        IProcessingOffsetRepository offsetRepository,
        MentionDetector mentionDetector,
        ProjectClassifier projectClassifier,
        UrgencyRanker urgencyRanker,
        Func<Message, long?>? managedProjectMatcher = null,
        IMessageRepository? messageRepository = null,
        Func<bool>? analyzeMessageContent = null,
        Func<Message, CancellationToken, Task<MentionMessageAnalysis>>? analyzeMentionAsync = null,
        Func<Message, CancellationToken, Task<TodoItem?>>? detectDuplicateAsync = null,
        Func<DateTimeOffset>? clock = null)
    {
        _adapters = adapters.ToArray();
        _messageProcessingUnitOfWork = messageProcessingUnitOfWork;
        _offsetRepository = offsetRepository;
        _mentionDetector = mentionDetector;
        _projectClassifier = projectClassifier;
        _urgencyRanker = urgencyRanker;
        _managedProjectMatcher = managedProjectMatcher ?? (_ => null);
        _messageRepository = messageRepository;
        _analyzeMessageContent = analyzeMessageContent ?? (() => false);
        _analyzeMentionAsync = analyzeMentionAsync;
        _detectDuplicateAsync = detectDuplicateAsync;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// 执行一轮采集。
    /// </summary>
    /// <returns>本轮采集的统计结果。</returns>
    public async Task<CaptureRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        // 读取各适配器上次的偏移量，构建采集上下文
        var offsets = await _offsetRepository.GetAllAsync(cancellationToken);
        var context = new CaptureContext(offsets);
        var captured = 0;
        var persisted = 0;
        var duplicates = 0;
        var createdTodos = 0;
        var sourceResults = new List<CaptureSourceRunResult>(_adapters.Count);

        // 依次执行每个适配器
        foreach (var adapter in _adapters)
        {
            var currentStage = "Capture";
            var sourceCaptured = 0;
            CaptureRunResult? batchResult = null;
            try
            {
                var batch = await adapter.CaptureAsync(context, cancellationToken);
                sourceCaptured = batch.Messages.Count;
                captured += batch.Messages.Count;

                // 复用统一的"去重 -> 持久化 -> 分类 -> 评分 -> 建待办"流程
                currentStage = WeChatReaderStage.PipelineCommit.ToString();
                batchResult = await ProcessAsync(batch.Messages, cancellationToken);
                persisted += batchResult.PersistedCount;
                duplicates += batchResult.DuplicateCount;
                createdTodos += batchResult.CreatedTodoCount;

                // 只有采集、处理和偏移提交全部成功后才推进本来源偏移量。
                currentStage = "OffsetCommit";
                await _offsetRepository.SaveAsync(adapter.Name, batch.NextOffset, cancellationToken);
                sourceResults.Add(new CaptureSourceRunResult(
                    adapter.Name,
                    batch.Messages.Count == 0 ? CaptureSourceRunStatus.Empty : CaptureSourceRunStatus.Succeeded,
                    batch.Messages.Count,
                    batchResult.PersistedCount,
                    batchResult.DuplicateCount,
                    batchResult.CreatedTodoCount,
                    Stage: null,
                    ErrorCode: null,
                    SanitizedError: null,
                    DateTimeOffset.Now));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var readerException = ex as WeChatReaderException;
                var sanitizedError = CaptureErrorSanitizer.Sanitize(ex.Message);
                sourceResults.Add(new CaptureSourceRunResult(
                    adapter.Name,
                    CaptureSourceRunStatus.Failed,
                    sourceCaptured,
                    batchResult?.PersistedCount ?? 0,
                    batchResult?.DuplicateCount ?? 0,
                    batchResult?.CreatedTodoCount ?? 0,
                    readerException?.Stage.ToString() ?? currentStage,
                    readerException?.ErrorCode.ToString() ?? WeChatReaderErrorCode.Unknown.ToString(),
                    sanitizedError,
                    DateTimeOffset.Now));
                System.Diagnostics.Debug.WriteLine($"Capture adapter '{adapter.Name}' failed: {sanitizedError}");
            }
        }

        return new CaptureRunResult(captured, persisted, duplicates, createdTodos, sourceResults);
    }

    /// <summary>
    /// 处理一批已采集的消息：去重 -> 持久化 -> 项目分类 -> 紧急度评分 -> @我 自动建待办。
    /// 该方法与 <see cref="RunOnceAsync"/> 共享同一套单消息处理逻辑，
    /// 亦可供外部入口（如"读取当天微信消息"按钮）直接复用，无需经过适配器采集环节。
    /// 通过 Source+SourceMessageKey 去重，保证同一消息多次读取不会重复入库或重复建待办。
    /// </summary>
    /// <param name="messages">待处理的消息列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本批处理统计：CapturedCount=入参总数，PersistedCount=新入库数，DuplicateCount=已存在跳过数，CreatedTodoCount=新建待办数。</returns>
    public async Task<CaptureRunResult> ProcessAsync(
        IEnumerable<CapturedMessage> messages,
        CancellationToken cancellationToken)
    {
        var captured = 0;
        var persisted = 0;
        var duplicates = 0;
        var createdTodos = 0;

        foreach (var capturedMessage in messages)
        {
            captured++;

            // 判断是否 @我
            var isMentionMe = _mentionDetector.IsMentioned(capturedMessage.Content);
            var isMailSource = MailSourcePolicy.IsMailSource(capturedMessage.Source);
            var message = ToMessage(capturedMessage, isMentionMe);
            // 网络分析必须在数据库事务之前完成，失败时以 null 回退本地优先级和默认说明。
            // 邮件没有 @提及 语义，一律交给 Agent 判定是否需要行动；聊天消息仍只在 @我 时调用。
            var needsAgentDecision = isMentionMe || isMailSource;
            var mentionAnalysis = await PrepareMentionAnalysisAsync(message, needsAgentDecision, cancellationToken);
            // 聊天消息：仅模型明确判定无需创建时跳过；未启用分析或模型失败(null)仍保留原自动待办。
            // 邮件：默认不进待办，只有模型明确判定需要行动才创建，否则收件箱会淹没待办列表。
            var shouldCreateTodo = isMailSource
                ? mentionAnalysis?.ShouldCreate == true
                : isMentionMe && (mentionAnalysis is null || mentionAnalysis.ShouldCreate);
            // 语义去重同样在事务之前完成：命中既有待办时只追加来源消息，不再新建待办。
            var mergeIntoTodoId = shouldCreateTodo
                ? await PrepareDuplicateTargetAsync(message, cancellationToken)
                : null;
            var writeResult = await _messageProcessingUnitOfWork.TryProcessAsync(
                message,
                savedMessage =>
                {
                    // 分类和评分保持在应用层；待办与消息由工作单元在同一事务中提交。
                    var classification = _projectClassifier.Classify(savedMessage);
                    var urgency = _urgencyRanker.Calculate(savedMessage, isMentionMe, classification);
                    // 即使命中既有待办也照常构造草稿：并发完成导致合并失败时仍能按原逻辑新建待办。
                    var draft = shouldCreateTodo
                        ? TodoService.CreateFromMention(
                            savedMessage,
                            classification,
                            urgency,
                            _clock(),
                            TimeZoneInfo.Local,
                            managedProjectId: _managedProjectMatcher(savedMessage),
                            description: mentionAnalysis?.Description,
                            // 模型优先级只进入自动待办，不改写消息流使用的本地评分。
                            priority: mentionAnalysis?.Priority,
                            analysisDueAt: mentionAnalysis?.DueAt,
                            analysisFirstReminderAt: mentionAnalysis?.FirstReminderAt)
                        : null;
                    // “那就5点半”“改到16点”这类确认表达代表大家最终认同的事件时间：
                    // 合并时允许把既有待办的截止与提醒改早；询问式时间仍只向后取更晚。
                    var confirmedDueAt = (DateTimeOffset?)null;
                    if (mergeIntoTodoId is not null
                        && TodoService.TryResolveConfirmedClockTime(savedMessage, TimeZoneInfo.Local, out var confirmed))
                    {
                        confirmedDueAt = confirmed;
                    }

                    var timeConfirmed = confirmedDueAt is not null;
                    // 自动待办的提醒时间由创建规则一并计算，并随消息事务原子持久化；
                    // 合并路径下同一时间由工作单元按"仅取更晚/确认覆盖"规则决定是否改期。
                    return new MessageProcessingArtifacts(
                        classification,
                        urgency,
                        draft?.Todo,
                        draft?.FirstReminderAt,
                        mergeIntoTodoId,
                        mergeIntoTodoId is null ? null : mentionAnalysis?.DueAt ?? confirmedDueAt,
                        timeConfirmed);
                },
                cancellationToken);

            if (!writeResult.Persisted)
            {
                duplicates++;
                continue;
            }

            persisted++;
            if (writeResult.CreatedTodo)
            {
                createdTodos++;
            }
        }

        return new CaptureRunResult(captured, persisted, duplicates, createdTodos);
    }

    /// <summary>判断新 @我 消息是否重复要求某条既有活动待办；任何失败都按新待办处理。</summary>
    private async Task<long?> PrepareDuplicateTargetAsync(Message message, CancellationToken cancellationToken)
    {
        if (_detectDuplicateAsync is null)
        {
            return null;
        }

        try
        {
            var existing = await _detectDuplicateAsync(message, cancellationToken);
            return existing?.Id;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Todo duplicate detection failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// 为需要判定的消息准备 Agent 筛选与内容分析。聊天消息由 @我 触发，邮件由来源触发；
    /// 任何模型故障都返回 null，由调用方按来源采取各自的回退策略。
    /// </summary>
    private async Task<MentionMessageAnalysis?> PrepareMentionAnalysisAsync(
        Message message,
        bool needsAgentDecision,
        CancellationToken cancellationToken)
    {
        if (!needsAgentDecision
            || !_analyzeMessageContent()
            || _messageRepository is null
            || _analyzeMentionAsync is null)
        {
            return null;
        }

        try
        {
            // 重复消息仍走原工作单元去重，但不再产生一次模型调用费用。
            var exists = await _messageRepository.ExistsAsync(
                message.Source,
                message.SourceMessageKey,
                cancellationToken);
            if (exists)
            {
                return null;
            }

            var analysis = await _analyzeMentionAsync(message, cancellationToken);
            // 空白说明只回退说明文本，模型已经返回的有效优先级仍然保留。
            return string.IsNullOrWhiteSpace(analysis.Description)
                ? analysis with { Description = null }
                : analysis with { Description = analysis.Description.Trim() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动停止采集时必须继续传播，不能伪装成模型失败。
            throw;
        }
        catch (Exception exception)
        {
            // 包括模型未配置、超时、网络错误和分析前去重失败，均保留原待办路径。
            System.Diagnostics.Debug.WriteLine(
                $"Mention analysis failed: {CaptureErrorSanitizer.Sanitize(exception.Message)}");
            return null;
        }
    }

    /// <summary>
    /// 将适配器产出的 CapturedMessage 转换为可持久化的 Message 实体。
    /// 会话 ID 通过对"来源:会话ID"做 FNV 哈希得到稳定正整数。
    /// </summary>
    private static Message ToMessage(CapturedMessage capturedMessage, bool isMentionMe)
    {
        return new Message(
            Id: 0,
            Source: capturedMessage.Source,
            SourceMessageKey: capturedMessage.SourceMessageKey,
            // 用稳定哈希生成会话 ID，避免依赖外部自增
            ChatSessionId: StablePositiveId($"{capturedMessage.Source}:{capturedMessage.ChatId}"),
            ChatName: capturedMessage.ChatName,
            SenderName: capturedMessage.SenderName,
            Content: capturedMessage.Content,
            SentAt: capturedMessage.SentAt,
            CapturedAt: DateTimeOffset.Now,
            MessageType: capturedMessage.MessageType,
            IsMentionMe: isMentionMe);
    }

    /// <summary>
    /// 使用 FNV-1a 算法对字符串计算稳定哈希，并映射为正 long 值。
    /// </summary>
    private static long StablePositiveId(string value)
    {
        var hash = 1469598103934665603UL;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        // 取符号位为 0，保证结果为正数
        return (long)(hash & 0x7FFFFFFFFFFFFFFF);
    }
}
