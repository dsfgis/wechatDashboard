using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Agent;

public enum MessageActionDecision { NeedContext, NoAction, SuggestTodo, AskUser }
public enum TodoSuggestionReviewStatus { Pending, Accepted, Dismissed }

public sealed record MessageActionRequest(
    Message Anchor,
    IReadOnlyList<Message> Context,
    DateTimeOffset Now,
    string? UserAnswer = null);

public sealed record MessageActionDraft(
    MessageActionDecision Decision,
    PriorityLevel Priority,
    string? Title,
    string? Description,
    DateTimeOffset? DueAt,
    string Reason,
    string? Question);

public sealed record TodoSuggestion(
    long MessageId,
    MessageActionDecision Decision,
    PriorityLevel Priority,
    string? Title,
    string? Description,
    DateTimeOffset? DueAt,
    string Reason,
    string? Question,
    TodoSuggestionReviewStatus ReviewStatus,
    DateTimeOffset AnalyzedAt);

public interface IMessageActionModel
{
    Task<MessageActionDraft> EvaluateAsync(MessageActionRequest request, CancellationToken cancellationToken);
}

public interface ITodoSuggestionRepository
{
    Task<TodoSuggestion?> GetAsync(long messageId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<long, TodoSuggestion>> GetByMessageIdsAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<long, PriorityLevel>> GetEffectivePrioritiesAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken);
    Task SaveAsync(TodoSuggestion suggestion, CancellationToken cancellationToken);
    Task ReplacePendingAsync(TodoSuggestion suggestion, CancellationToken cancellationToken);
    Task SetReviewStatusAsync(long messageId, TodoSuggestionReviewStatus status, CancellationToken cancellationToken);
}

/// <summary>
/// Reads one saved message, asks the model for an action, and retrieves nearby messages only if requested.
/// It saves a proposal, never creates a Todo or changes the capture cursor.
/// </summary>
public sealed class TodoSuggestionAgent
{
    private readonly IMessageRepository _messages;
    private readonly ITodoSuggestionRepository _suggestions;
    private readonly IMessageActionModel _model;
    private readonly IClock _clock;

    public TodoSuggestionAgent(IMessageRepository messages, ITodoSuggestionRepository suggestions,
        IMessageActionModel model, IClock clock)
    {
        _messages = messages;
        _suggestions = suggestions;
        _model = model;
        _clock = clock;
    }

    public async Task<TodoSuggestion> AnalyzeAsync(long messageId, CancellationToken cancellationToken)
    {
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        var saved = await _suggestions.GetAsync(messageId, cancellationToken);
        if (saved is not null) return saved;

        var anchor = await _messages.GetByIdAsync(messageId, cancellationToken)
            ?? throw new InvalidOperationException("原始消息已不存在，无法分析。");
        var now = _clock.Now;
        var draft = await _model.EvaluateAsync(new MessageActionRequest(anchor, Array.Empty<Message>(), now), cancellationToken);
        if (draft.Decision == MessageActionDecision.NeedContext)
        {
            var neighbors = await GetNeighborsAsync(anchor, cancellationToken);
            draft = neighbors.Length == 0
                ? new MessageActionDraft(MessageActionDecision.AskUser, draft.Priority, null, null, null,
                    "缺少可核实的相邻消息。", "这条消息是否要求你完成一项具体工作？")
                : await _model.EvaluateAsync(new MessageActionRequest(anchor, neighbors, now), cancellationToken);
            if (draft.Decision == MessageActionDecision.NeedContext)
                draft = draft with { Decision = MessageActionDecision.AskUser,
                    Question = "已查看相邻消息。请确认这条消息是否需要形成待办。" };
        }

        var suggestion = Validate(messageId, draft, now);
        await _suggestions.SaveAsync(suggestion, cancellationToken);
        return suggestion;
    }

    public async Task<TodoSuggestion> ClarifyAsync(long messageId, string answer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 1000)
            throw new ArgumentException("请填写不超过 1000 字的澄清回答。", nameof(answer));
        var previous = await _suggestions.GetAsync(messageId, cancellationToken);
        if (previous is not { Decision: MessageActionDecision.AskUser, ReviewStatus: TodoSuggestionReviewStatus.Pending })
            throw new InvalidOperationException("这条消息当前没有待回答的澄清问题。");
        var anchor = await _messages.GetByIdAsync(messageId, cancellationToken)
            ?? throw new InvalidOperationException("原始消息已不存在，无法继续分析。");
        var now = _clock.Now;
        var draft = await _model.EvaluateAsync(new MessageActionRequest(anchor, Array.Empty<Message>(), now, answer.Trim()), cancellationToken);
        if (draft.Decision == MessageActionDecision.NeedContext)
        {
            var neighbors = await GetNeighborsAsync(anchor, cancellationToken);
            draft = neighbors.Length == 0
                ? new MessageActionDraft(MessageActionDecision.AskUser, draft.Priority, null, null, null,
                    "回答后仍缺少可核实的会话上下文。", "请补充这项工作的具体要求。")
                : await _model.EvaluateAsync(new MessageActionRequest(anchor, neighbors, now, answer.Trim()), cancellationToken);
            if (draft.Decision == MessageActionDecision.NeedContext)
                draft = draft with { Decision = MessageActionDecision.AskUser,
                    Question = "请补充这项工作的具体要求。" };
        }
        var suggestion = Validate(messageId, draft, now);
        await _suggestions.ReplacePendingAsync(suggestion, cancellationToken);
        return suggestion;
    }

    private async Task<Message[]> GetNeighborsAsync(Message anchor, CancellationToken cancellationToken)
    {
        var context = await _messages.GetContextAsync(anchor.Id, before: 100, after: 100, cancellationToken);
        return context?.Messages
            .Where(message => message.Id != anchor.Id && message.Source == anchor.Source
                && (anchor.ChatSessionId != 0
                    ? message.ChatSessionId == anchor.ChatSessionId
                    : message.ChatName == anchor.ChatName))
            .OrderBy(message => Math.Abs((message.SentAt - anchor.SentAt).Ticks))
            .Take(6)
            .OrderBy(message => message.SentAt)
            .ThenBy(message => message.Id)
            .ToArray() ?? [];
    }

    private static TodoSuggestion Validate(long messageId, MessageActionDraft draft, DateTimeOffset now)
    {
        if (!Enum.IsDefined(draft.Decision) || draft.Decision == MessageActionDecision.NeedContext
            || !Enum.IsDefined(draft.Priority))
            throw new InvalidOperationException("模型返回了不支持的判断结果。");
        var reason = draft.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new InvalidOperationException("模型未提供有效的判断依据。");
        var title = draft.Title?.Trim();
        if (draft.Decision == MessageActionDecision.SuggestTodo &&
            (string.IsNullOrWhiteSpace(title) || title.Length > 120))
            throw new InvalidOperationException("模型未提供有效的待办标题。");
        var question = draft.Question?.Trim();
        if (draft.Decision == MessageActionDecision.AskUser &&
            (string.IsNullOrWhiteSpace(question) || question.Length > 300))
            throw new InvalidOperationException("模型未提供有效的澄清问题。");
        if (draft.Description?.Length > 2000)
            throw new InvalidOperationException("模型返回的待办描述过长。");
        return new TodoSuggestion(messageId, draft.Decision, draft.Priority,
            draft.Decision == MessageActionDecision.SuggestTodo ? title : null,
            draft.Decision == MessageActionDecision.SuggestTodo ? draft.Description?.Trim() : null,
            draft.Decision == MessageActionDecision.SuggestTodo ? draft.DueAt : null,
            reason, draft.Decision == MessageActionDecision.AskUser ? question : null,
            TodoSuggestionReviewStatus.Pending, now);
    }
}
