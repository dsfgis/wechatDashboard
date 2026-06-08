using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

public sealed class MessageCapturePipeline
{
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

    public async Task<CaptureRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var offsets = await _offsetRepository.GetAllAsync(cancellationToken);
        var context = new CaptureContext(offsets);
        var captured = 0;
        var persisted = 0;
        var duplicates = 0;
        var createdTodos = 0;

        foreach (var adapter in _adapters)
        {
            CaptureBatch batch;
            try
            {
                batch = await adapter.CaptureAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Capture adapter '{adapter.Name}' failed: {ex.Message}");
                continue;
            }

            captured += batch.Messages.Count;

            foreach (var capturedMessage in batch.Messages)
            {
                if (await _messageRepository.ExistsAsync(capturedMessage.Source, capturedMessage.SourceMessageKey, cancellationToken))
                {
                    duplicates++;
                    continue;
                }

                var isMentionMe = _mentionDetector.IsMentioned(capturedMessage.Content);
                var message = ToMessage(capturedMessage, isMentionMe);
                var savedMessage = await _messageRepository.SaveAsync(message, cancellationToken);
                persisted++;

                var classification = _projectClassifier.Classify(savedMessage);
                var urgency = _urgencyRanker.Calculate(savedMessage, isMentionMe, classification);

                if (isMentionMe)
                {
                    await _todoRepository.SaveAsync(TodoService.CreateFromMention(savedMessage, classification, urgency), cancellationToken);
                    createdTodos++;
                }
            }

            await _offsetRepository.SaveAsync(adapter.Name, batch.NextOffset, cancellationToken);
        }

        return new CaptureRunResult(captured, persisted, duplicates, createdTodos);
    }

    private static Message ToMessage(CapturedMessage capturedMessage, bool isMentionMe)
    {
        return new Message(
            Id: 0,
            Source: capturedMessage.Source,
            SourceMessageKey: capturedMessage.SourceMessageKey,
            ChatSessionId: StablePositiveId($"{capturedMessage.Source}:{capturedMessage.ChatId}"),
            ChatName: capturedMessage.ChatName,
            SenderName: capturedMessage.SenderName,
            Content: capturedMessage.Content,
            SentAt: capturedMessage.SentAt,
            CapturedAt: DateTimeOffset.Now,
            MessageType: capturedMessage.MessageType,
            IsMentionMe: isMentionMe);
    }

    private static long StablePositiveId(string value)
    {
        var hash = 1469598103934665603UL;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return (long)(hash & 0x7FFFFFFFFFFFFFFF);
    }
}
