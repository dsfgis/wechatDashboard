using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

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

    public MessageCapturePipeline(
        IEnumerable<IMessageCaptureAdapter> adapters,
        IMessageProcessingUnitOfWork messageProcessingUnitOfWork,
        IProcessingOffsetRepository offsetRepository,
        MentionDetector mentionDetector,
        ProjectClassifier projectClassifier,
        UrgencyRanker urgencyRanker)
    {
        _adapters = adapters.ToArray();
        _messageProcessingUnitOfWork = messageProcessingUnitOfWork;
        _offsetRepository = offsetRepository;
        _mentionDetector = mentionDetector;
        _projectClassifier = projectClassifier;
        _urgencyRanker = urgencyRanker;
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

        // 依次执行每个适配器
        foreach (var adapter in _adapters)
        {
            CaptureBatch batch;
            try
            {
                batch = await adapter.CaptureAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个适配器失败不影响其它适配器，仅记录调试信息
                System.Diagnostics.Debug.WriteLine($"Capture adapter '{adapter.Name}' failed: {ex.Message}");
                continue;
            }

            captured += batch.Messages.Count;

            // 复用统一的"去重 -> 持久化 -> 分类 -> 评分 -> 建待办"流程
            var batchResult = await ProcessAsync(batch.Messages, cancellationToken);
            persisted += batchResult.PersistedCount;
            duplicates += batchResult.DuplicateCount;
            createdTodos += batchResult.CreatedTodoCount;

            // 保存本适配器最新偏移量，支持增量采集
            await _offsetRepository.SaveAsync(adapter.Name, batch.NextOffset, cancellationToken);
        }

        return new CaptureRunResult(captured, persisted, duplicates, createdTodos);
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
            var message = ToMessage(capturedMessage, isMentionMe);
            var writeResult = await _messageProcessingUnitOfWork.TryProcessAsync(
                message,
                savedMessage =>
                {
                    // 分类和评分保持在应用层；待办与消息由工作单元在同一事务中提交。
                    var classification = _projectClassifier.Classify(savedMessage);
                    var urgency = _urgencyRanker.Calculate(savedMessage, isMentionMe, classification);
                    var todo = isMentionMe
                        ? TodoService.CreateFromMention(savedMessage, classification, urgency)
                        : null;
                    return new MessageProcessingArtifacts(classification, urgency, todo);
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
