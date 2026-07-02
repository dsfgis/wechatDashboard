using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

/// <summary>
/// 待办事项服务：提供从消息自动生成待办的工厂方法。
/// </summary>
public static class TodoService
{
    /// <summary>
    /// 当一条消息 @我 时，自动创建一条待办。
    /// 待办标题取自消息正文（截断到 80 字），优先级沿用紧急度评分。
    /// </summary>
    /// <param name="message">触发待办的消息。</param>
    /// <param name="classification">该消息的分类结果。</param>
    /// <param name="urgency">该消息的紧急度评分。</param>
    /// <returns>新建的待办实体（尚未持久化，Id=0）。</returns>
    public static TodoItem CreateFromMention(Message message, ClassificationResult classification, UrgencyScore urgency)
    {
        var now = DateTimeOffset.Now;
        return new TodoItem(
            Id: 0,
            SourceMessageId: message.Id,
            ProjectId: classification.ProjectId,
            Title: BuildTitle(message.Content),
            // 描述里带上群名/发送人/正文，便于回溯上下文
            Description: $"{message.ChatName} / {message.SenderName}: {message.Content}",
            Status: TodoStatus.Pending,
            Priority: urgency.Priority,
            DueAt: null,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null,
            IsAutoCreated: true);
    }

    /// <summary>
    /// 将消息正文整理为待办标题：折叠多余空白并截断到 80 字。
    /// </summary>
    private static string BuildTitle(string content)
    {
        // 折叠连续空白为单个空格
        var normalized = string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= 80)
        {
            return normalized;
        }

        // 超长截断并加省略号
        return normalized[..77] + "...";
    }
}
