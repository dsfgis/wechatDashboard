using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

/// <summary>集中管理由原消息生成 Todo 时的默认字段，避免 UI 和采集管线各自拼装。</summary>
public static class TodoFactory
{
    public static TodoItem CreateFromMessage(
        Message message,
        long? suggestedProjectId,
        PriorityLevel suggestedPriority,
        DateTimeOffset now,
        string? title = null,
        string? description = null,
        long? projectId = null,
        PriorityLevel? priority = null,
        DateTimeOffset? dueAt = null,
        bool isAutoCreated = false)
    {
        return new TodoItem(
            Id: 0,
            SourceMessageId: message.Id,
            ProjectId: projectId ?? suggestedProjectId,
            Title: string.IsNullOrWhiteSpace(title) ? BuildTitle(message.Content) : title.Trim(),
            Description: description ?? $"{message.ChatName} / {message.SenderName}: {message.Content}",
            Status: TodoStatus.Pending,
            Priority: priority ?? suggestedPriority,
            DueAt: dueAt,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null,
            IsAutoCreated: isAutoCreated);
    }

    public static string BuildTitle(string content)
    {
        var normalized = string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 80 ? normalized : normalized[..77] + "...";
    }
}
