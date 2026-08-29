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
        return CreateFromMention(message, classification, urgency, DateTimeOffset.Now, TimeZoneInfo.Local);
    }

    /// <summary>使用指定时钟和时区创建自动识别待办，便于稳定计算“今日到期”。</summary>
    public static TodoItem CreateFromMention(
        Message message,
        ClassificationResult classification,
        UrgencyScore urgency,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var dueAt = new TodoDueBucketPolicy(timeZone).EndOfToday(now);
        return TodoFactory.CreateFromMessage(
            message,
            classification.ProjectId,
            urgency.Priority,
            now,
            dueAt: dueAt,
            isAutoCreated: true);
    }
}
