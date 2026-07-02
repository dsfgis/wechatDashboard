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
    private readonly IMessageRepository _messageRepository;
    private readonly ITodoRepository _todoRepository;
    private readonly IProcessingOffsetRepository _offsetRepository;
    private readonly MentionDetector _mentionDetector;
    private readonly ProjectClassifier _projectClassifier;
    private readonly UrgencyRanker _urgencyRanker;

    public MessageCapturePipeline(
        IEnumerable<IMessageCaptureAdapter> adapters,
        IMessageRepository messageRepository,
        ITodoRepository todoRepository,
        IProcessingOffsetRepository offsetRepository,
        MentionDetector mentionDetector,
        ProjectClassifier projectClassifier,
        UrgencyRanker urgencyRanker)
    {
        _adapters = adapters.ToArray();
        _messageRepository = messageRepository;
        _todoRepository = todoRepository;
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

            // 逐条去重并持久化
            foreach (var capturedMessage in batch.Messages)
            {
                if (await _messageRepository.ExistsAsync(capturedMessage.Source, capturedMessage.SourceMessageKey, cancellationToken))
                {
                    duplicates++;
                    continue;
                }

                // 判断是否 @我
                var isMentionMe = _mentionDetector.IsMentioned(capturedMessage.Content);
                var message = ToMessage(capturedMessage, isMentionMe);
                var savedMessage = await _messageRepository.SaveAsync(message, cancellationToken);
                persisted++;

                // 分类 + 紧急度评分
                var classification = _projectClassifier.Classify(savedMessage);
                var urgency = _urgencyRanker.Calculate(savedMessage, isMentionMe, classification);

                // @我 的消息自动创建待办
                if (isMentionMe)
                {
                    await _todoRepository.SaveAsync(TodoService.CreateFromMention(savedMessage, classification, urgency), cancellationToken);
                    createdTodos++;
                }
            }

            // 保存本适配器最新偏移量，支持增量采集
            await _offsetRepository.SaveAsync(adapter.Name, batch.NextOffset, cancellationToken);
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
