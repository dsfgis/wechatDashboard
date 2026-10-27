using System.Globalization;
using System.Text.RegularExpressions;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Todos;

/// <summary>
/// 待办事项服务：提供从消息自动生成待办的工厂方法。
/// </summary>
public static class TodoService
{
    /// <summary>会议提醒相对开始时间的默认提前量。</summary>
    public static readonly TimeSpan MeetingReminderLead = TimeSpan.FromMinutes(5);

    /// <summary>普通任务按截止时间预留的默认处理时长。</summary>
    public static readonly TimeSpan TaskEffortLead = TimeSpan.FromHours(1);

    // 腾讯会议等引用卡片会给出明确会议时间，优先使用开始时间作为行动截止时间。
    private static readonly Regex MeetingStartRegex = new(
        @"会议时间\s*[：:]\s*(?<year>\d{4})\s*[年/-]\s*(?<month>\d{1,2})\s*[月/-]\s*(?<day>\d{1,2})日?\s+(?<hour>\d{1,2})[:：](?<minute>\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 仅处理明确属于本周的中文星期表达，避免把“周三”误判到错误周。
    private static readonly Regex CurrentWeekdayRegex = new(
        @"(?:本周|这周|本星期|这星期)(?<weekday>[一二三四五六日天])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] MeetingTimeFormats = { "yyyy/M/d H:mm", "yyyy/M/d HH:mm" };

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
        return CreateFromMention(message, classification, urgency, DateTimeOffset.Now, TimeZoneInfo.Local).Todo;
    }

    /// <summary>使用指定时钟和时区创建自动识别待办，便于稳定计算到期日。</summary>
    /// <param name="analysisDueAt">Agent 识别的明确完成时间；提供时优先于本地日期规则。</param>
    /// <param name="analysisFirstReminderAt">Agent 建议的提醒时间；提供时优先于按截止时间推导的默认提醒。</param>
    public static TodoFactory.TodoDraft CreateFromMention(
        Message message,
        ClassificationResult classification,
        UrgencyScore urgency,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        long? managedProjectId = null,
        string? description = null,
        PriorityLevel? priority = null,
        DateTimeOffset? analysisDueAt = null,
        DateTimeOffset? analysisFirstReminderAt = null)
    {
        // 优先识别正文和引用会议中的明确时间，其次识别“本周三”等本周日期；
        // 都没有时仍按消息发送日期计算，补采历史消息不会误用采集当天。
        var dueAt = ResolveAutomaticDueAt(message, timeZone, analysisDueAt);
        var firstReminderAt = ResolveAutomaticFirstReminderAt(
            message.Content,
            dueAt,
            now,
            timeZone,
            analysisFirstReminderAt);
        return TodoFactory.CreateFromMessage(
            message,
            classification.ProjectId,
            // Agent 只覆盖待办优先级；未提供时仍使用本地紧急度评分。
            priority ?? urgency.Priority,
            now,
            // 分析结果只覆盖优先级和说明；null 说明仍由工厂按原消息生成。
            description: description,
            dueAt: dueAt,
            isAutoCreated: true,
            managedProjectId: managedProjectId,
            firstReminderAt: firstReminderAt);
    }

    /// <summary>
    /// 计算自动待办的首个提醒时间：会议按开始时间提前五分钟，任务按预计处理时长提前提醒。
    /// Agent 提供的明确提醒时间优先；提醒早于当前时间或无法落到未来时不创建提醒。
    /// </summary>
    public static DateTimeOffset? ResolveAutomaticFirstReminderAt(
        string content,
        DateTimeOffset? dueAt,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        DateTimeOffset? analysisFirstReminderAt = null)
    {
        if (analysisFirstReminderAt is { } preferred)
        {
            return preferred > now ? preferred : null;
        }

        if (dueAt is not { } target)
        {
            return null;
        }

        var lead = MeetingStartRegex.IsMatch(content) ? MeetingReminderLead : TaskEffortLead;
        var scheduledAt = target - lead;
        return scheduledAt > now ? scheduledAt : null;
    }

    /// <summary>从消息正文、Agent 明确时间和已合并的引用内容计算自动待办截止时间。</summary>
    private static DateTimeOffset ResolveAutomaticDueAt(
        Message message,
        TimeZoneInfo timeZone,
        DateTimeOffset? analysisDueAt = null)
    {
        // Agent 识别出的明确完成/截止时间优先；本地规则继续作为模型未启用或失败时的可靠回退。
        if (analysisDueAt is { } analyzed)
        {
            return analyzed;
        }

        // 明确的“会议时间：2026/10/28 09:00-12:00”使用开始时刻，避免会议结束后才到期。
        var meetingMatch = MeetingStartRegex.Match(message.Content);
        if (meetingMatch.Success)
        {
            var meetingText = string.Join('/',
                meetingMatch.Groups["year"].Value,
                meetingMatch.Groups["month"].Value,
                meetingMatch.Groups["day"].Value)
                + $" {meetingMatch.Groups["hour"].Value}:{meetingMatch.Groups["minute"].Value}";
            if (DateTime.TryParseExact(
                    meetingText,
                    MeetingTimeFormats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var meetingStart))
            {
                return new DateTimeOffset(meetingStart, timeZone.GetUtcOffset(meetingStart));
            }
        }

        var weekdayMatch = CurrentWeekdayRegex.Match(message.Content);
        if (weekdayMatch.Success)
        {
            // 中文自然周按周一开始；目标日期完全基于消息发送日期，历史补采结果保持稳定。
            var localSentAt = TimeZoneInfo.ConvertTime(message.SentAt, timeZone);
            var daysSinceMonday = ((int)localSentAt.DayOfWeek + 6) % 7;
            var weekStart = localSentAt.Date.AddDays(-daysSinceMonday);
            var weekdayOffset = weekdayMatch.Groups["weekday"].Value switch
            {
                "一" => 0,
                "二" => 1,
                "三" => 2,
                "四" => 3,
                "五" => 4,
                "六" => 5,
                _ => 6
            };
            var localEnd = weekStart.AddDays(weekdayOffset).AddDays(1).AddTicks(-1);
            return new DateTimeOffset(localEnd, timeZone.GetUtcOffset(localEnd));
        }

        // 未出现可识别日期时保留原有默认规则，不影响既有自动待办。
        return new TodoDueBucketPolicy(timeZone).EndOfToday(message.SentAt);
    }
}
