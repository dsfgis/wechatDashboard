using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

public static class TodoService
{
    public static TodoItem CreateFromMention(Message message, ClassificationResult classification, UrgencyScore urgency)
    {
        var now = DateTimeOffset.Now;
        return new TodoItem(
            Id: 0,
            SourceMessageId: message.Id,
            ProjectId: classification.ProjectId,
            Title: BuildTitle(message.Content),
            Description: $"{message.ChatName} / {message.SenderName}: {message.Content}",
            Status: TodoStatus.Pending,
            Priority: urgency.Priority,
            DueAt: null,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null,
            IsAutoCreated: true);
    }

    private static string BuildTitle(string content)
    {
        var normalized = string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 80)
        {
            return normalized;
        }

        return normalized[..77] + "...";
    }
}
