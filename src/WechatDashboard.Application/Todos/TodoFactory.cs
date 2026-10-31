using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

/// <summary>集中管理由原消息生成 Todo 时的默认字段，避免 UI 和采集管线各自拼装。</summary>
public static class TodoFactory
{
    /// <summary>由消息生成的待办及随同创建的首个提醒时间。</summary>
    public sealed record TodoDraft(TodoItem Todo, DateTimeOffset? FirstReminderAt);

    public static TodoDraft CreateFromMessage(
        Message message,
        long? suggestedProjectId,
        PriorityLevel suggestedPriority,
        DateTimeOffset now,
        string? title = null,
        string? description = null,
        long? projectId = null,
        PriorityLevel? priority = null,
        DateTimeOffset? dueAt = null,
        bool isAutoCreated = false,
        long? managedProjectId = null,
        DateTimeOffset? firstReminderAt = null)
    {
        var todo = new TodoItem(
            Id: 0,
            SourceMessageId: message.Id,
            ProjectId: projectId ?? suggestedProjectId,
            Title: ResolveTitle(message, title),
            Description: description ?? $"{message.ChatName} / {message.SenderName}: {message.Content}",
            Status: TodoStatus.Pending,
            Priority: priority ?? suggestedPriority,
            DueAt: dueAt,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null,
            IsAutoCreated: isAutoCreated)
        {
            ManagedProjectId = managedProjectId
        };
        return new TodoDraft(todo, firstReminderAt);
    }

    /// <summary>
    /// 解析待办标题：显式标题优先；邮件来源使用清洗后的邮件主题（用户要求待办标题用邮件标题）；
    /// 其余来源从正文提炼一句简短标题。
    /// </summary>
    private static string ResolveTitle(Message message, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        if (MailSourcePolicy.IsMailSource(message.Source))
        {
            var subject = MailSourcePolicy.CleanSubject(message.ChatName);
            if (subject.Length > 0)
            {
                return subject;
            }
        }

        return BuildTitle(message.Content);
    }

    /// <summary>待办标题的最大长度（字符）。标题用于列表与详情窗口标题栏，需要保持一行可读。</summary>
    public const int TitleMaxLength = 30;

    /// <summary>
    /// 由消息正文提炼一句简短标题：折叠空白、截到第一个句末标点，再按 <see cref="TitleMaxLength"/> 截断。
    /// 开头的 @提及 会被保留，因为它是识别消息来源对象的有效信息，也是既有约定的标题组成部分。
    /// 这只是本地启发式，语义级总结由 Agent 的说明字段承担。
    /// </summary>
    public static string BuildTitle(string content)
    {
        var normalized = string.Join(" ", content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return "新待办";
        }

        var sentenceEnd = normalized.IndexOfAny(['。', '；', '！', '？', ';', '!', '?']);
        if (sentenceEnd > 0)
        {
            normalized = normalized[..sentenceEnd];
        }

        return normalized.Length <= TitleMaxLength
            ? normalized
            : normalized[..(TitleMaxLength - 1)] + "…";
    }
}
