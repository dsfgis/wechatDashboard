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

    /// <summary>正文识别出具体钟点时，提醒相对该时刻的提前量（留出回复/到场时间）。</summary>
    public static readonly TimeSpan ClockTimeReminderLead = TimeSpan.FromMinutes(15);

    /// <summary>用户显式指定截止时间时，按预留一小时处理时长倒推提醒。</summary>
    public static readonly TimeSpan TaskEffortLead = TimeSpan.FromHours(1);

    /// <summary>只有日终截止时间时，提醒放在截止日早上的默认时刻。</summary>
    public static readonly TimeSpan TaskMorningReminderOffset = TimeSpan.FromHours(9);

    /// <summary>既无具体时刻又已过截止日早上时，收到后多久提醒一次。</summary>
    public static readonly TimeSpan FallbackReminderLead = TimeSpan.FromHours(1);

    // 腾讯会议等引用卡片会给出明确会议时间，优先使用开始时间作为行动截止时间。
    private static readonly Regex MeetingStartRegex = new(
        @"会议时间\s*[：:]\s*(?<year>\d{4})\s*[年/-]\s*(?<month>\d{1,2})\s*[月/-]\s*(?<day>\d{1,2})日?\s+(?<hour>\d{1,2})[:：](?<minute>\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 仅处理明确属于本周的中文星期表达，避免把“周三”误判到错误周。
    private static readonly Regex CurrentWeekdayRegex = new(
        @"(?:本周|这周|本星期|这星期)(?<weekday>[一二三四五六日天])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] MeetingTimeFormats = { "yyyy/M/d H:mm", "yyyy/M/d HH:mm" };

    // 正文里的口语钟点：5点半、17:30、下午3点、16点左右、明天上午10点。
    // 用负向后行断言排除“第3点要求”这类条目编号，避免把序号当成时钟。
    private static readonly Regex ExplicitClockTimeRegex = new(
        @"(?<![\d第])(?<day>今天|今日|明天|明日|后天)?\s*(?<period>上午|下午|中午|晚上|傍晚|凌晨|早上|清晨|今晚|明早)?\s*(?<hour>\d{1,2})\s*(?:[:：]\s*(?<minute>\d{2})|[点时](?:\s*(?<half>半)|(?<minute2>\d{1,2})\s*分?)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 正文写出完整年月日时说明是另一种写法，交给既有会议/星期规则，避免只截取钟点造成日期错位。
    private static readonly Regex FullDateRegex = new(
        @"\d{4}\s*[年/-]\s*\d{1,2}\s*[月/-]\s*\d{1,2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 把时间“定下来”的确认表达：好吧那就5点半 / 改到16点 / 定在下午3点 / 确认明天上午10点。
    // 与单纯的提问（“5点半方便么？”）区分开：只有确认表达才允许把既有待办的时间改早。
    private static readonly Regex ConfirmedClockTimeRegex = new(
        @"(?<confirm>那就|就|定在|定于|定了|改为|改至|改到|改成|调整到|调整至|调整为|挪到|挪至|推迟到|推迟至|提前到|提前至|延后到|延后至|确认|确定|敲定|好吧)\s*(?<day>今天|今日|明天|明日|后天)?\s*(?<period>上午|下午|中午|晚上|傍晚|凌晨|早上|清晨|今晚|明早)?\s*(?<hour>\d{1,2})\s*(?:[:：]\s*(?<minute>\d{2})|[点时](?:\s*(?<half>半)|(?<minute2>\d{1,2})\s*分?)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
    /// 计算自动待办的首个提醒时间。优先级：Agent 明确时间 → 会议卡片提前五分钟 →
    /// 正文明确钟点提前十五分钟 → 日终截止的任务放在截止日早上九点，已过则收到后一小时。
    /// 提醒早于当前时间或落在截止时间之后时不创建提醒。
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

        if (MeetingStartRegex.IsMatch(content))
        {
            return Schedulable(target - MeetingReminderLead, target, now);
        }

        // 正文只给出“5点半”“下午3点”这类钟点时，提前十五分钟提醒，留出回复或到场时间。
        if (!FullDateRegex.IsMatch(content) && ExplicitClockTimeRegex.IsMatch(content))
        {
            return Schedulable(target - ClockTimeReminderLead, target, now);
        }

        // 截止时间是“当天/当周某日的末尾”，说明消息没有给出具体时刻：
        // 放在截止日早上提醒，已过早上则收到后一小时提醒，避免出现 22:59 这种临近午夜的提醒。
        var localDue = TimeZoneInfo.ConvertTime(target, timeZone);
        if (!IsDayEndSentinel(localDue))
        {
            return Schedulable(target - TaskEffortLead, target, now);
        }

        var morning = MorningOf(localDue, timeZone);
        if (morning > now && morning < target)
        {
            return morning;
        }

        return Schedulable(now + FallbackReminderLead, target, now);
    }

    /// <summary>判断截止时间是否为“某日最后一刻”的兜底值（本地 23:59 之后）。</summary>
    private static bool IsDayEndSentinel(DateTimeOffset localInstant) =>
        localInstant.TimeOfDay >= new TimeSpan(23, 59, 0);

    /// <summary>只有当提醒严格晚于当前时间且早于截止时间时才可用。</summary>
    private static DateTimeOffset? Schedulable(DateTimeOffset candidate, DateTimeOffset dueAt, DateTimeOffset now) =>
        candidate > now && candidate < dueAt ? candidate : null;

    /// <summary>取截止日当天九点（含时区偏移），用作日终截止任务的默认提醒时刻。</summary>
    private static DateTimeOffset MorningOf(DateTimeOffset localInstant, TimeZoneInfo timeZone)
    {
        var localMorning = localInstant.Date + TaskMorningReminderOffset;
        return new DateTimeOffset(localMorning, timeZone.GetUtcOffset(localMorning));
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

        var localSentAt = TimeZoneInfo.ConvertTime(message.SentAt, timeZone);
        // 正文已写出完整年月日时交给既有规则，避免只截取到钟点导致日期错位。
        var hasFullDate = FullDateRegex.IsMatch(message.Content);
        var weekdayDate = TryResolveWeekdayDate(message.Content, localSentAt);

        // 正文给出“5点半”“下午3点”“16点左右”这类口语钟点时，以该时刻作为截止时间，
        // 否则文件会退回日终时间，导致提醒落在深夜（例如 22:59 才提醒回复 17:30 的确认）。
        if (!hasFullDate
            && ExplicitClockTimeRegex.Match(message.Content) is { Success: true } clockMatch
            && TryResolveClockInstant(clockMatch, localSentAt, weekdayDate, timeZone, out var clockInstant))
        {
            return clockInstant;
        }

        // 只识别出“本周三”而没有钟点时，取该自然日末尾。
        if (weekdayDate is { } weekday)
        {
            var localEnd = weekday.AddDays(1).AddTicks(-1);
            return new DateTimeOffset(localEnd, timeZone.GetUtcOffset(localEnd));
        }

        // 未出现可识别日期时保留原有默认规则，不影响既有自动待办。
        return new TodoDueBucketPolicy(timeZone).EndOfToday(message.SentAt);
    }

    /// <summary>识别“本周三”这类本周内的明确星期，返回对应的本地自然日。</summary>
    private static DateTime? TryResolveWeekdayDate(string content, DateTimeOffset localSentAt)
    {
        var weekdayMatch = CurrentWeekdayRegex.Match(content);
        if (!weekdayMatch.Success)
        {
            return null;
        }

        // 中文自然周按周一开始；目标日期完全基于消息发送日期，历史补采结果保持稳定。
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
        return weekStart.AddDays(weekdayOffset);
    }

    /// <summary>把正文里的口语钟点解析为具体时刻，支持“5点半”“下午3点”“16点”“明天上午10点”“17:30”。</summary>
    private static bool TryResolveClockInstant(
        Match match,
        DateTimeOffset localSentAt,
        DateTime? weekdayDate,
        TimeZoneInfo timeZone,
        out DateTimeOffset instant)
    {
        instant = default;
        if (!int.TryParse(match.Groups["hour"].Value, out var hour) || hour > 23)
        {
            return false;
        }

        var minute = 0;
        if (match.Groups["minute"].Success)
        {
            int.TryParse(match.Groups["minute"].Value, out minute);
        }
        else if (match.Groups["half"].Success)
        {
            minute = 30;
        }
        else if (match.Groups["minute2"].Success)
        {
            int.TryParse(match.Groups["minute2"].Value, out minute);
        }

        if (minute > 59)
        {
            return false;
        }

        var dayText = match.Groups["day"].Value;
        var period = match.Groups["period"].Value;
        var dayOffset = dayText switch
        {
            "明天" or "明日" or "明早" => 1,
            "后天" => 2,
            _ => 0
        };
        var baseDate = dayText.Length > 0
            ? localSentAt.Date.AddDays(dayOffset)
            : weekdayDate ?? localSentAt.Date;

        hour = NormalizeHour(hour, period);

        // 没有时段词的“5点半”可能指凌晨也可能指下午：优先取不早于消息发送时刻的最近解释。
        var candidate = baseDate.AddHours(hour).AddMinutes(minute);
        var hasExplicitDay = dayText.Length > 0 || weekdayDate is not null;
        if (!match.Groups["minute"].Success
            && period.Length == 0
            && !hasExplicitDay
            && hour <= 11
            && ToInstant(candidate, timeZone) < localSentAt)
        {
            var afternoon = candidate.AddHours(12);
            if (afternoon.Hour <= 23 && ToInstant(afternoon, timeZone) > localSentAt)
            {
                candidate = afternoon;
            }
        }

        instant = ToInstant(candidate, timeZone);
        return true;
    }

    /// <summary>把“上午/下午/晚上/中午/凌晨”等时段词折算成 24 小时制。</summary>
    private static int NormalizeHour(int hour, string period) => period switch
    {
        "下午" or "晚上" or "傍晚" or "今晚" => hour < 12 ? hour + 12 : hour,
        "中午" => hour < 11 ? hour + 12 : hour,
        "凌晨" or "上午" or "早上" or "清晨" or "明早" => hour == 12 ? 0 : hour,
        _ => hour
    };

    private static DateTimeOffset ToInstant(DateTime localDateTime, TimeZoneInfo timeZone) =>
        new(localDateTime, timeZone.GetUtcOffset(localDateTime));

    /// <summary>
    /// 识别“那就5点半”“改到16点”“定在下午3点”这类把时间确定下来的确认表达。
    /// 合并重复提醒时，只有确认时间才允许把既有待办的截止与提醒改早；
    /// “请问5点半方便么”这类询问仍沿用“只向后取更晚”的规则。
    /// </summary>
    public static bool TryResolveConfirmedClockTime(
        Message message,
        TimeZoneInfo timeZone,
        out DateTimeOffset confirmedAt)
    {
        confirmedAt = default;
        if (string.IsNullOrEmpty(message?.Content) || FullDateRegex.IsMatch(message.Content))
        {
            return false;
        }

        var match = ConfirmedClockTimeRegex.Match(message.Content);
        if (!match.Success)
        {
            return false;
        }

        var localSentAt = TimeZoneInfo.ConvertTime(message.SentAt, timeZone);
        return TryResolveClockInstant(match, localSentAt, weekdayDate: null, timeZone, out confirmedAt);
    }

    /// <summary>确认时间沿用正文钟点口径：提前十五分钟提醒；已不足以提醒时返回 null。</summary>
    public static DateTimeOffset? ResolveConfirmedReminder(DateTimeOffset confirmedAt, DateTimeOffset now)
    {
        var reminderAt = confirmedAt - ClockTimeReminderLead;
        return reminderAt > now ? reminderAt : null;
    }
}
