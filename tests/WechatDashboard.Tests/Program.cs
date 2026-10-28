using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WechatDashboard.App.Services;
using WechatDashboard.App.ViewModels.Messages;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Application.Agent;
using Microsoft.Data.Sqlite;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Agent;
using WechatDashboard.Infrastructure.Capture;
using WechatDashboard.Infrastructure.Background;
using WechatDashboard.Infrastructure.Persistence;
using WechatDashboard.KeyProbe;
using ZstdSharp;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Mention detector matches aliases and WeChat mention hints", TestMentionDetectorAsync),
    ("Project classifier uses chat and keyword rules", TestProjectClassifierAsync),
    ("Urgency ranker promotes mentioned incident due today to P0", TestUrgencyRankerAsync),
    ("Todo service creates a pending todo from a mention message", TestTodoCreationAsync),
    ("Todo due bucket policy separates overdue, today, upcoming and undated items", TestTodoDueBucketPolicyAsync),
    ("Todo workbench summary matches its mutually exclusive visible groups", TestTodoWorkbenchBucketSummaryAsync),
    ("Any persisted message converts to one Todo with an optional reminder", TestManualMessageToTodoAsync),
    ("Todo repository reports existing Todo state for message feed rows", TestMessageTodoStateLookupAsync),
    ("A reminder from another chat merges into the existing Todo and only later times reschedule it", TestTodoMergeAcrossChatsAsync),
    ("Migration v6 backfills existing source messages into the link table", TestTodoSourceMessageMigrationAsync),    ("Capture pipeline reuses the existing Todo for a repeated cross-chat mention", TestCapturePipelineReusesTodoAcrossChatsAsync),
    ("Todo detail shows every source message of a merged Todo", TestTodoDetailShowsMultipleSourceMessagesAsync),
    ("Message feed manual conversion applies Agent output with safe fallback", TestMessageFeedManualAgentConversionAsync),
    ("Manual Agent priority stays isolated and rolls back with Todo failures", TestManualAgentPriorityTransactionAsync),
    ("DeepSeek model uses saved API key and preserves environment fallback", TestDeepSeekMessageActionModelAsync),
    ("DeepSeek mention analysis returns creation decision, priority and summary JSON", TestDeepSeekMentionAnalysisAsync),
    ("Reminder snooze preserves history without changing the Todo due time", TestReminderSnoozeAsync),
    ("Reminder worker claims and delivers a due reminder only once", TestReminderWorkerAsync),
    ("Reminder worker recovers a stale dispatch claim after restart", TestReminderWorkerRecoversStaleClaimAsync),
    ("Message context query locates the source message by stable id", TestMessageContextQueryAsync),
    ("Message feed keeps the source context when a page refresh races with navigation", TestMessageFeedContextSurvivesRefreshAsync),
    ("DataGrid scroll behavior skips scrolling when the selection is cleared first", TestDataGridScrollSelectionClearedAsync),
    ("SQLite repositories initialize schema and round-trip message and todo", TestSqliteRoundTripAsync),
    ("Todo group migration preserves existing todos and is idempotent", TestTodoGroupMigrationAsync),
    ("Project assignment migration maps only same-name groups and clears deleted projects", TestTodoProjectAssignmentMigrationAsync),
    ("Todo group migration rolls back when schema creation fails", TestTodoGroupMigrationRollbackAsync),
    ("Todo groups normalize names and reject duplicates", TestTodoGroupsAsync),
    ("Todo detail project choices match configured projects", TestTodoDetailProjectOptionsAsync),
    ("Todo detail pins and unpins an active todo", TestTodoDetailPinToggleAsync),
    ("Todo detail window renders the header pin button", TestTodoDetailWindowPinButtonRenderingAsync),
    ("Todo detail saves an editable description above the source message", TestTodoDetailDescriptionAsync),
    ("Todo detail renders the description editor above the original message", TestTodoDetailDescriptionSectionRenderingAsync),
    ("Manual Todo creation persists a group and optional reminder atomically", TestManualTodoCreationAsync),
    ("Todo group changes persist through completion", TestTodoGroupLifecycleAsync),
    ("Manual Todo create view model selects a project and prevents duplicate submission", TestTodoCreateViewModelAsync),
    ("Todo rows show projects and truthful manual source", TestTodoRowPresentationAsync),
    ("Todo workbench preserves checked rows across background refresh", TestTodoWorkbenchSelectionRefreshAsync),
    ("Desktop calendar groups scheduled Todos by local due date", TestDesktopCalendarViewModelAsync),
    ("Calendar checkbox and detail button persist Todo completion", TestTodoCompletionEntryPointsAsync),
    ("Detail completion keeps the finished Todo visible in the calendar", TestDetailCompletionKeepsTodoVisibleInCalendarAsync),
    ("Desktop calendar window preferences round-trip", TestDesktopCalendarStateStoreAsync),
    ("Desktop calendar edge retraction preserves its size", TestDesktopCalendarDockLayoutAsync),
    ("Desktop calendar window renders readonly Todo bindings", TestDesktopCalendarWindowRenderingAsync),
    ("Desktop calendar list uses a theme-aware scrollbar in both themes", TestDesktopCalendarScrollBarThemeAsync),
    ("Embedded desktop calendar stays above the raised desktop through Win+D", TestDesktopCalendarShowDesktopSurvivalAsync),
    ("Auto start service toggles the current user Run entry", TestAutoStartServiceAsync),
    ("SQLite message processing transaction rolls back message when Todo insert fails", TestMessageTodoTransactionRollbackAsync),
    ("Todo repository moves a checked todo to completed", TestTodoCompletionAsync),
    ("Todo repository persists pinning and sorts pinned active todos first", TestTodoPinningAsync),
    ("Todo repository completes only explicitly selected active todos", TestSelectedTodoCompletionAsync),
    ("Todo repository retains and clears Done and Ignored history", TestDeleteCompletedTodosAsync),
    ("Todo repository sorts pending todos by source message time descending", TestTodoOrderByMessageTimeAsync),
    ("Default mention aliases include current user's WeChat display names", TestDefaultMentionAliasesAsync),
    ("Capture adapter factory creates enabled adapters for collaboration sources", TestCaptureAdapterFactoryAsync),
    ("Capture adapter factory creates live WeChat visible-window adapters", TestLiveCaptureAdapterFactoryAsync),
    ("Shihuatong decoder and referenced meeting content are preserved", TestShihuatongMessageContentDecoderAsync),
    ("Shihuatong source stamp ignores volatile shared-memory changes", TestShihuatongSourceStampAsync),
    ("Window text adapter captures normalized visible messages with stable keys", TestWindowTextCaptureAdapterAsync),
    ("Window text adapter captures UIA split visible message blocks", TestWindowTextCaptureAdapterSplitBlocksAsync),
    ("Window text adapter ignores dashboard status when inferring chat name", TestWindowTextCaptureAdapterSkipsStatusChatNameAsync),
    ("Window text adapter rejects dashboard snapshots", TestWindowTextCaptureAdapterRejectsDashboardSnapshotAsync),
    ("Windows UI Automation snapshot provider filters windows and aggregates visible text", TestWindowsUiAutomationSnapshotProviderAsync),
    ("WeChat OCR crop calculator focuses the chat panel", TestWeChatOcrCropCalculatorAsync),
    ("Windows OCR snapshot provider reads WeChat text when UIA exposes only chrome", TestWindowsOcrSnapshotProviderAsync),
    ("Window capture diagnostics service summarizes matching snapshots", TestWindowCaptureDiagnosticsServiceAsync),
    ("JSONL directory adapter captures only new messages and preserves source identity", TestJsonlDirectoryCaptureAdapterAsync),
    ("WeChat local export adapter captures local database export messages incrementally", TestWeChatLocalExportCaptureAdapterAsync),
    ("KeyProbe options accept only non-sensitive bounded arguments", TestKeyProbeOptionsAsync),
    ("Native hook provider rejects missing binaries before process access", TestNativeHookKeyProviderRejectsMissingBinariesAsync),
    ("WeChat local reader service initializes through the native C# reader", TestWeChatLocalReaderServiceExtractsNativeDatabaseKeyAsync),
    ("WeChat local reader service reads native pages without a command runner", TestWeChatLocalReaderServiceReadsPagedMessagesAsync),
    ("Protected WeChat key store uses DPAPI CurrentUser and rejects corrupt files", TestProtectedWeChatDatabaseKeyStoreAsync),
    ("Protected DeepSeek API key store round-trips and rejects corrupt files", TestProtectedDeepSeekApiKeyStoreAsync),
    ("Native WeChat reader restores a validated key without KeyProbe", TestNativeWeChatReaderRestoresProtectedKeyAsync),
    ("Native WeChat key derivation validates SQLCipher V4 databases", TestNativeWeChatKeyDerivationAsync),
    ("Native WeChat reader queries V4 shards and adapter offsets", TestNativeWeChatReaderAsync),
    ("Native WeChat reader matches the frozen Python oracle field by field", TestNativeWeChatReaderMatchesPythonOracleAsync),
    ("Capture pipeline persists messages and creates todos for mentions", TestCapturePipelineAsync),
    ("Capture pipeline applies immediate Agent priorities and summaries with safe fallback", TestCapturePipelineMentionAnalysisAsync),
    ("Capture pipeline reports one source failure and continues healthy sources", TestCapturePipelineReportsIndividualSourceFailuresAsync),
    ("Capture pipeline persists live WeChat local export messages", TestLiveWeChatLocalExportCapturePipelineAsync),
    ("Capture source settings repository saves and loads enabled sources", TestCaptureSourceSettingsRepositoryAsync),
    ("Model settings repository persists the message analysis switch", TestModelSettingsRepositoryAsync),
    ("Capture pipeline uses saved source settings for adapter construction", TestCapturePipelineWithSavedSettingsAsync),
    ("User alias repository saves, loads and deletes configurable mention aliases", TestUserAliasRepositoryAsync),
    ("Project keyword repository supports multiple keywords per project", TestFollowedProjectKeywordRepositoryAsync),
    ("Project settings exposes a fixed-palette editable color column", TestProjectColorSettingsXamlAsync),
};

var failures = new List<string>();

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex);
    }
}

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Failures:");
    foreach (var failure in failures)
    {
        Console.WriteLine($"- {failure}");
    }

    Environment.Exit(1);
}

Console.WriteLine();
Console.WriteLine($"All {tests.Length} tests passed.");

static Task TestMentionDetectorAsync()
{
    var detector = new MentionDetector(new[] { "张三", "zhangsan" });

    AssertTrue(detector.IsMentioned("@张三 今天下班前处理线上故障"), "Chinese alias should be detected.");
    AssertTrue(detector.IsMentioned("有人@我 请确认接口变更", hasWechatMentionHint: true), "WeChat mention hint should be detected.");
    // 使用微信消息中的 U+2005 空白，验证特殊空白不会影响 @所有人 的文本识别。
    const string mentionAllContent = "@所有人\u2005 ai agent 开发测试";
    AssertTrue(detector.IsMentioned(mentionAllContent), "A group mention-all message should be detected.");
    AssertTrue(detector.ExtractMentionedAliases(mentionAllContent).Contains("所有人"), "Mention-all should be extracted for consistent message presentation.");
    AssertFalse(detector.IsMentioned("@李四 帮忙看一下"), "Other people's mentions should not match.");

    return Task.CompletedTask;
}

static Task TestDefaultMentionAliasesAsync()
{
    var detector = new MentionDetector(DefaultMentionAliases.All);

    AssertTrue(detector.IsMentioned("@白驹过隙 这个问题需要你确认"), "WeChat display name should be treated as current user.");
    AssertTrue(detector.IsMentioned("@戴少峰 这个问题需要你确认"), "Chinese name should be treated as current user.");
    AssertFalse(detector.IsMentioned("@张三 今天下班前处理线上故障"), "Other people's mentions should not match default aliases.");

    return Task.CompletedTask;
}

static Task TestProjectClassifierAsync()
{
    var classifier = new ProjectClassifier(new[]
    {
        new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100),
        new ProjectRule(2, "支付平台", ProjectRuleType.Keyword, "支付", 80)
    });

    var message = CreateMessage(
        id: 10,
        chatName: "CRM项目群",
        senderName: "王经理",
        content: "@张三 今天处理 CRM 线上故障");

    var result = classifier.Classify(message);

    AssertEqual(1L, result.ProjectId, "Chat-name rule should win.");
    AssertEqual("CRM升级", result.ProjectName, "Project name should come from the winning rule.");
    AssertEqual(MessageCategory.Incident, result.Category, "Incident keywords should classify the category.");
    AssertTrue(result.Confidence >= 0.90, "High-weight exact chat rule should be high confidence.");

    return Task.CompletedTask;
}

static Task TestUrgencyRankerAsync()
{
    var message = CreateMessage(
        id: 20,
        chatName: "CRM项目群",
        senderName: "王经理",
        content: "@张三 紧急，今天下班前处理线上故障");
    var classification = new ClassificationResult(20, 1, "CRM升级", MessageCategory.Incident, 0.98, "chat rule", "Rules");

    var ranker = new UrgencyRanker(
        priorityContacts: new[] { "王经理" },
        priorityProjectIds: new[] { 1L });

    var score = ranker.Calculate(message, isMentionMe: true, classification);

    AssertEqual(PriorityLevel.P0, score.Priority, "Mentioned urgent incident should be P0.");
    AssertTrue(score.Score >= 85, "P0 score should be at least 85.");
    AssertTrue(score.Reason.Contains("@我"), "Reason should explain mention contribution.");
    AssertTrue(score.Reason.Contains("线上"), "Reason should explain incident contribution.");

    return Task.CompletedTask;
}

static Task TestTodoCreationAsync()
{
    var now = new DateTimeOffset(2026, 8, 9, 14, 0, 0, TimeSpan.FromHours(8));
    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var message = CreateMessage(
        id: 30,
        chatName: "支付项目群",
        senderName: "赵经理",
        content: "@zhangsan 请今天给出支付联调问题反馈") with
    {
        SentAt = now.AddMinutes(-30)
    };
    var classification = new ClassificationResult(30, 2, "支付平台", MessageCategory.Question, 0.85, "keyword rule", "Rules");
    var urgency = new UrgencyScore(30, 72, PriorityLevel.P1, "@我; 今天");

    var todo = TodoService.CreateFromMention(message, classification, urgency, now, timeZone).Todo;

    AssertEqual(TodoStatus.Pending, todo.Status, "Mention-created todo should start as pending.");
    AssertEqual(PriorityLevel.P1, todo.Priority, "Todo priority should follow urgency.");
    AssertEqual(30L, todo.SourceMessageId, "Todo should point to source message.");
    AssertEqual(2L, todo.ProjectId, "Todo should carry project classification.");
    AssertTrue(todo.IsAutoCreated, "Mention-created todo should be marked as automatic.");
    AssertTrue(todo.Title.Length <= 80, "Generated title should fit list display.");
    AssertEqual(
        new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.FromHours(8)).AddTicks(-1),
        todo.DueAt,
        "Automatically recognized Todo should expire at the end of the message's local send day.");
    AssertEqual(
        TodoDueBucket.DueToday,
        new TodoDueBucketPolicy(timeZone).Classify(todo.DueAt, now),
        "A todo from a message sent today should appear in the due-today bucket.");
    AssertEqual(
        "支付项目群 / 赵经理: @zhangsan 请今天给出支付联调问题反馈",
        todo.Description,
        "A null summary must preserve the original default description.");

    // 自定义摘要只能覆盖说明，其他自动待办字段必须逐项保持一致。
    var summarizedTodo = TodoService.CreateFromMention(
        message,
        classification,
        urgency,
        now,
        timeZone,
        description: "请今天反馈支付联调问题").Todo;
    AssertEqual("请今天反馈支付联调问题", summarizedTodo.Description,
        "A model summary should be written to the Todo description.");
    AssertEqual(todo.Title, summarizedTodo.Title, "A summary must not change the Todo title.");
    AssertEqual(todo.Priority, summarizedTodo.Priority, "A summary must not change the Todo priority.");
    AssertEqual(todo.ProjectId, summarizedTodo.ProjectId, "A summary must not change the Todo project.");
    AssertEqual(todo.DueAt, summarizedTodo.DueAt, "A summary must not change the Todo due time.");
    AssertTrue(summarizedTodo.IsAutoCreated, "A summarized Todo must remain automatically created.");

    // 补采场景：消息昨天发送、今天才采集，待办应落在发送当天而非采集当天。
    var lateCapturedMessage = message with { SentAt = now.AddDays(-1).AddHours(-2) };
    var lateTodo = TodoService.CreateFromMention(lateCapturedMessage, classification, urgency, now, timeZone).Todo;
    AssertEqual(
        new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.FromHours(8)).AddTicks(-1),
        lateTodo.DueAt,
        "A todo from a back-filled message should expire at the end of the message's send day.");
    AssertEqual(
        TodoDueBucket.Overdue,
        new TodoDueBucketPolicy(timeZone).Classify(lateTodo.DueAt, now),
        "A back-filled todo from yesterday should appear in the overdue bucket.");

    // 只有“本周三”时，截止时间为发送消息所在自然周的周三末尾。
    var meetingSentAt = new DateTimeOffset(2026, 10, 26, 11, 10, 0, TimeSpan.FromHours(8));
    var meetingMessage = CreateMessage(
        id: 31,
        chatName: "项目协作群",
        senderName: "李工",
        content: "@张三 本周三上午对接项目进展，请协调相关人员参会") with
    {
        SentAt = meetingSentAt
    };
    var weekdayTodo = TodoService.CreateFromMention(
        meetingMessage, classification, urgency, meetingSentAt.AddHours(1), timeZone).Todo;
    AssertEqual(
        new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.FromHours(8)).AddTicks(-1),
        weekdayTodo.DueAt,
        "An explicit current-week Wednesday should move the Todo and calendar entry to Wednesday.");
    AssertEqual(TodoDueBucket.Upcoming,
        new TodoDueBucketPolicy(timeZone).Classify(weekdayTodo.DueAt, meetingSentAt),
        "Wednesday's Todo should remain in the upcoming bucket on Monday.");

    // 引用会议存在明确开始时间时，应优先于相对日期和默认日末时间。
    var quotedMeetingMessage = meetingMessage with
    {
        Content = meetingMessage.Content
            + "\n\n引用消息：\n李工 邀请您参加腾讯会议\n会议时间：2026/10/28 09:00-12:00 (GMT+08:00)"
    };
    var meetingDraft = TodoService.CreateFromMention(
        quotedMeetingMessage, classification, urgency, meetingSentAt.AddHours(1), timeZone);
    var meetingTodo = meetingDraft.Todo;
    AssertEqual(
        new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(8)),
        meetingTodo.DueAt,
        "A referenced meeting start time should take precedence over the relative Wednesday date.");
    AssertEqual(
        new DateTimeOffset(2026, 10, 28, 8, 55, 0, TimeSpan.FromHours(8)),
        meetingDraft.FirstReminderAt,
        "A meeting Todo should be reminded five minutes before its start time.");

    // 普通任务按预计处理时长（默认一小时）从截止时间倒推提醒。
    var taskDraft = TodoService.CreateFromMention(
        message, classification, urgency, now, timeZone);
    AssertEqual(
        todo.DueAt - TimeSpan.FromHours(1),
        taskDraft.FirstReminderAt,
        "A task Todo should reserve one hour of effort before its deadline for the first reminder.");

    // Agent 识别出的明确完成时间优先，且可直接给出更合适的提醒时间。
    var analyzedDraft = TodoService.CreateFromMention(
        quotedMeetingMessage,
        classification,
        urgency,
        meetingSentAt.AddHours(1),
        timeZone,
        analysisDueAt: new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.FromHours(8)),
        analysisFirstReminderAt: new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8)));
    AssertEqual(
        new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.FromHours(8)),
        analyzedDraft.Todo.DueAt,
        "An analyzed task completion time should override local meeting parsing.");
    AssertEqual(
        new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8)),
        analyzedDraft.FirstReminderAt,
        "An analyzed reminder time should override the default meeting/task lead.");

    return Task.CompletedTask;
}

static Task TestTodoDueBucketPolicyAsync()
{
    var now = new DateTimeOffset(2026, 8, 9, 14, 0, 0, TimeSpan.FromHours(8));
    var policy = new TodoDueBucketPolicy(TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));

    AssertEqual(TodoDueBucket.DueToday, policy.Classify(now.AddMinutes(-1), now), "An earlier due time on the current local date should remain due today.");
    AssertEqual(TodoDueBucket.DueToday, policy.Classify(now.AddHours(2), now), "A later due time today should be due today.");
    AssertEqual(TodoDueBucket.Upcoming, policy.Classify(now.AddDays(1), now), "A due time tomorrow should be upcoming.");
    AssertEqual(TodoDueBucket.NoDueDate, policy.Classify(null, now), "A missing due time should be undated.");
    AssertEqual(TodoDueBucket.DueToday, policy.Classify(now, now), "A Todo due exactly now should remain due today.");
    AssertEqual(TodoDueBucket.Overdue, policy.Classify(now.AddDays(-1), now), "Only a due date before the current local date should be overdue.");

    return Task.CompletedTask;
}

static async Task TestTodoWorkbenchBucketSummaryAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var todos = new SqliteTodoRepository(databasePath);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var todoService = new TodoApplicationService(unitOfWork, clock);
    var pinnedOverdue = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("已置顶且昨天到期", null, PriorityLevel.P0, now.AddDays(-1), null, null),
        CancellationToken.None)).Todo!;
    await todoService.CreateManualAsync(
        new CreateManualTodoRequest("昨天到期", null, PriorityLevel.P1, now.AddDays(-1), null, null),
        CancellationToken.None);
    await todoService.CreateManualAsync(
        new CreateManualTodoRequest("今天早些时候到期", null, PriorityLevel.P2, now.AddHours(-1), null, null),
        CancellationToken.None);
    await todoService.CreateManualAsync(
        new CreateManualTodoRequest("今天稍后到期", null, PriorityLevel.P3, now.AddHours(1), null, null),
        CancellationToken.None);
    await todoService.CreateManualAsync(
        new CreateManualTodoRequest("明天到期", null, PriorityLevel.P0, now.AddDays(1), null, null),
        CancellationToken.None);
    await todoService.CreateManualAsync(
        new CreateManualTodoRequest("无截止时间", null, PriorityLevel.P1, null, null, null),
        CancellationToken.None);
    await todos.SetPinnedAsync(pinnedOverdue.Id, true, now, CancellationToken.None);

    // 已办记录使用 P0，验证它既不会增加活动总数，也不会污染优先级统计。
    var completed = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("已办理 P0", null, PriorityLevel.P0, now.AddDays(-2), null, null),
        CancellationToken.None)).Todo!;
    await todos.MarkCompletedAsync(completed.Id, now, CancellationToken.None);

    var workbench = new TodoWorkbenchViewModel(
        todos,
        new SqliteMessageRepository(databasePath),
        new SqliteReminderRepository(databasePath),
        todoService,
        new ReminderApplicationService(unitOfWork, clock),
        new SqliteFollowedProjectRepository(databasePath),
        new TodoDueBucketPolicy(timeZone),
        clock,
        new FakeTodoDialogService(),
        _ => Task.CompletedTask,
        () => Task.CompletedTask);

    await workbench.RefreshAsync();

    AssertEqual(6, workbench.ActiveCount, "The active total should equal the sum of all visible mutually exclusive groups.");
    AssertEqual(1, workbench.Pinned.Count, "Pinned Todos should appear only in the pinned group.");
    AssertEqual(1, workbench.Overdue.Count, "The overdue group should exclude pinned Todos and include only dates before today.");
    AssertEqual(1, workbench.OverdueCount, "The summary overdue count should match the visible overdue group.");
    AssertEqual(2, workbench.DueToday.Count, "Past and future times on today's local date should both be due today.");
    AssertEqual(1, workbench.Upcoming.Count, "Tomorrow should be upcoming.");
    AssertEqual(1, workbench.NoDueDate.Count, "A missing deadline should remain undated.");

    // 优先级是独立维度，包含置顶活动待办，但不得包含上面的已办 P0。
    AssertEqual(2, workbench.Priority0.Count, "P0 should include active pinned and unpinned Todos, but exclude completed Todos.");
    AssertEqual(2, workbench.Priority1.Count, "P1 should contain only active Todos.");
    AssertEqual(1, workbench.Priority2.Count, "P2 should contain only active Todos.");
    AssertEqual(1, workbench.Priority3.Count, "P3 should contain only active Todos.");
    AssertEqual(1, workbench.Completed.Count, "The completed Todo should remain only in the completed history.");
    AssertEqual(
        "活动待办 6 条：已置顶 1 条、已逾期 1 条、今日到期 2 条、后续到期 1 条、无截止时间 1 条。",
        workbench.StatusText,
        "The summary should use the same mutually exclusive counts shown by the group headers.");
}

static async Task TestManualMessageToTodoAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var reminderRepository = new SqliteReminderRepository(databasePath);
    var now = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8));
    var message = await messageRepository.SaveAsync(
        CreateMessage(0, "普通项目群", "李工", "请下周确认接口字段") with
        {
            SourceMessageKey = $"manual-todo-{Guid.NewGuid():N}",
            IsMentionMe = false
        },
        CancellationToken.None);
    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    var dueAt = now.AddDays(2);
    var reminderAt = now.AddHours(1);
    var request = new CreateTodoFromMessageRequest(
        message.Id,
        Title: null,
        Description: "人工备注",
        ProjectId: null,
        Priority: PriorityLevel.P2,
        DueAt: dueAt,
        FirstReminderAt: reminderAt);

    var created = await service.CreateFromMessageAsync(request, CancellationToken.None);
    var repeated = await service.CreateFromMessageAsync(request, CancellationToken.None);
    var todos = await todoRepository.GetActiveAsync(CancellationToken.None);
    var reminders = await reminderRepository.GetForTodoAsync(created.Todo!.Id, CancellationToken.None);

    AssertEqual(CreateTodoOutcome.Created, created.Outcome, "The first command should create a Todo.");
    AssertEqual(CreateTodoOutcome.ExistingTodo, repeated.Outcome, "A repeated command should return the existing Todo.");
    AssertEqual(created.Todo.Id, repeated.Todo!.Id, "Repeated conversion should point to the same Todo.");
    AssertEqual(1, todos.Count, "Only one active Todo should be created from the message.");
    AssertEqual(message.Id, todos.Single().SourceMessageId, "The manual Todo should preserve the source message id.");
    AssertEqual(dueAt, todos.Single().DueAt, "The selected due time should persist.");
    AssertFalse(todos.Single().IsAutoCreated, "A user conversion should be marked as manual.");
    AssertEqual(1, reminders.Count, "The optional initial reminder should be persisted atomically.");
    AssertEqual(ReminderStatus.Scheduled, reminders.Single().Status, "The initial reminder should be scheduled.");
    AssertEqual(reminderAt, reminders.Single().ScheduledAt, "The selected reminder time should persist.");
}

static async Task TestMessageTodoStateLookupAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.FromHours(8));
    var automaticMessage = await messageRepository.SaveAsync(
        CreateMessage(0, "自动待办群", "王工", "@张三 请处理") with
        {
            SourceMessageKey = $"auto-state-{Guid.NewGuid():N}",
            IsMentionMe = true
        },
        CancellationToken.None);
    var manualMessage = await messageRepository.SaveAsync(
        CreateMessage(0, "手动待办群", "李工", "请核对") with
        {
            SourceMessageKey = $"manual-state-{Guid.NewGuid():N}",
            IsMentionMe = false
        },
        CancellationToken.None);
    var noTodoMessage = await messageRepository.SaveAsync(
        CreateMessage(0, "普通消息群", "赵工", "已收到") with
        {
            SourceMessageKey = $"no-todo-state-{Guid.NewGuid():N}",
            IsMentionMe = false
        },
        CancellationToken.None);

    var automaticTodo = await todoRepository.SaveAsync(new TodoItem(
        0, automaticMessage.Id, null, automaticMessage.Content, null,
        TodoStatus.Pending, PriorityLevel.P2, null, now, now, null, true),
        CancellationToken.None);
    await todoRepository.SaveAsync(new TodoItem(
        0, manualMessage.Id, null, manualMessage.Content, null,
        TodoStatus.Done, PriorityLevel.P2, null, now, now, now, false),
        CancellationToken.None);

    var manualConversionAttempt = await new TodoApplicationService(
        new SqliteTodoUnitOfWork(databasePath),
        new FixedClock(now.AddMinutes(1))).CreateFromMessageAsync(
            new CreateTodoFromMessageRequest(automaticMessage.Id, null, null, null, null, null, null),
            CancellationToken.None);

    var states = await todoRepository.GetBySourceMessageIdsAsync(
        new[] { automaticMessage.Id, manualMessage.Id, noTodoMessage.Id, automaticMessage.Id },
        CancellationToken.None);

    AssertEqual(2, states.Count, "Only messages with a Todo should be returned once.");
    AssertEqual(CreateTodoOutcome.ExistingTodo, manualConversionAttempt.Outcome, "Manual conversion must reuse an existing automatic Todo.");
    AssertEqual(automaticTodo.Id, manualConversionAttempt.Todo!.Id, "Manual conversion must not replace the automatic Todo.");
    AssertTrue(states[automaticMessage.Id].IsAutoCreated, "Automatic Todo state must remain distinguishable in the message feed.");
    AssertEqual(TodoStatus.Pending, states[automaticMessage.Id].Status, "The active automatic Todo status should be returned.");
    AssertFalse(states[manualMessage.Id].IsAutoCreated, "Manual Todo state must remain distinguishable in the message feed.");
    AssertEqual(TodoStatus.Done, states[manualMessage.Id].Status, "Completed Todo status should be returned.");
    AssertFalse(states.ContainsKey(noTodoMessage.Id), "A message without a Todo must remain convertible.");
}

static async Task TestTodoSourceMessageMigrationAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var messages = new SqliteMessageRepository(databasePath);
    var todos = new SqliteTodoRepository(databasePath);
    var message = await messages.SaveAsync(
        CreateMessage(0, "历史群", "王工", "升级前的待办来源消息") with { SourceMessageKey = $"migration-{Guid.NewGuid():N}" },
        CancellationToken.None);
    var now = DateTimeOffset.Now;
    var todo = await todos.SaveAsync(
        new TodoItem(0, message.Id, null, "升级前的待办", null, TodoStatus.Pending, PriorityLevel.P2, null, now, now, null, true),
        CancellationToken.None);

    // 模拟升级前的数据库：既没有关联表，也没有 v6 迁移记录。
    await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
    {
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS todo_source_messages; DELETE FROM schema_migrations WHERE version = 6;";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    await using var verified = new SqliteConnection($"Data Source={databasePath}");
    await verified.OpenAsync(CancellationToken.None);
    await using var count = verified.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM todo_source_messages WHERE todo_id = $todoId AND message_id = $messageId AND is_primary = 1;";
    count.Parameters.AddWithValue("$todoId", todo.Id);
    count.Parameters.AddWithValue("$messageId", message.Id);
    AssertEqual(1, Convert.ToInt32(await count.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture),
        "Migration v6 should backfill the existing source message as the primary link.");

    // 迁移必须幂等：再次初始化不能重复插入关联行。
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    AssertEqual(1, Convert.ToInt32(await count.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture),
        "Re-running the migration must not duplicate source message links.");
}

static async Task TestTodoMergeAcrossChatsAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var messages = new SqliteMessageRepository(databasePath);
    var todos = new SqliteTodoRepository(databasePath);
    var reminders = new SqliteReminderRepository(databasePath);
    var now = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(8));
    Message NewMessage(string chatName, string sender, string content) => CreateMessage(0, chatName, sender, content);
    var firstMessage = await messages.SaveAsync(
        NewMessage("项目A群", "王工", "请周五前提交验收报告") with
        {
            SourceMessageKey = $"merge-a-{Guid.NewGuid():N}",
            IsMentionMe = true
        },
        CancellationToken.None);
    var secondMessage = await messages.SaveAsync(
        NewMessage("项目B群", "李工", "提醒一下，周五前要交验收报告") with
        {
            SourceMessageKey = $"merge-b-{Guid.NewGuid():N}",
            IsMentionMe = true
        },
        CancellationToken.None);

    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    var created = await service.CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(firstMessage.Id, null, null, null, null, null, now.AddHours(2)),
        CancellationToken.None);
    var todoId = created.Todo!.Id;

    // 第二次提醒时间更晚：应改期到更晚时间，并保留两条来源消息。
    var laterMerge = await service.MergeIntoExistingAsync(
        new MergeTodoRequest(todoId, secondMessage.Id, now.AddHours(5)),
        CancellationToken.None);
    var active = await todos.GetActiveAsync(CancellationToken.None);
    var sources = await todos.GetSourceMessagesForTodosAsync(new[] { todoId }, CancellationToken.None);
    var reminderRows = await reminders.GetForTodoAsync(todoId, CancellationToken.None);
    var messageState = await todos.GetBySourceMessageIdsAsync(
        new[] { firstMessage.Id, secondMessage.Id },
        CancellationToken.None);
    var linkedTodo = await todos.GetByAnySourceMessageIdAsync(secondMessage.Id, CancellationToken.None);

    AssertEqual(1, active.Count, "A repeated reminder from another chat must not create a second Todo.");
    AssertTrue(laterMerge.Merged, "The second reminder should merge into the existing Todo.");
    AssertTrue(laterMerge.ReminderRescheduled, "A later reminder should reschedule the existing reminder.");
    AssertEqual(2, sources[todoId].Count, "Both chats should be kept as source messages.");
    AssertEqual("项目A群", sources[todoId][0].ChatName, "The primary source message should be listed first.");
    AssertEqual("项目B群", sources[todoId][1].ChatName, "The merged source message should follow the primary one.");
    AssertEqual(now.AddHours(5), reminderRows.Single(item => item.Status == ReminderStatus.Scheduled).ScheduledAt,
        "The later reminder time should win.");
    AssertEqual(2, reminderRows.Count, "The replaced reminder should be kept as cancelled history.");
    AssertEqual(2, messageState.Count, "Both messages should report the same existing Todo.");
    AssertEqual(todoId, messageState[secondMessage.Id].Id, "The merged message should point to the original Todo.");
    AssertEqual(todoId, linkedTodo!.Id, "Merged messages must resolve to the original Todo.");

    // 第三次提醒更早：保持原提醒时间，不把提醒提前，但来源消息仍要追加。
    var thirdMessage = await messages.SaveAsync(
        NewMessage("项目C群", "赵工", "验收报告别忘了") with
        {
            SourceMessageKey = $"merge-c-{Guid.NewGuid():N}",
            IsMentionMe = true
        },
        CancellationToken.None);
    var earlierMerge = await service.MergeIntoExistingAsync(
        new MergeTodoRequest(todoId, thirdMessage.Id, now.AddMinutes(30)),
        CancellationToken.None);
    var finalReminders = await reminders.GetForTodoAsync(todoId, CancellationToken.None);
    var finalSources = await todos.GetSourceMessagesForTodosAsync(new[] { todoId }, CancellationToken.None);

    AssertFalse(earlierMerge.ReminderRescheduled, "An earlier reminder must not move the reminder forward.");
    AssertEqual(now.AddHours(5), finalReminders.Single(item => item.Status == ReminderStatus.Scheduled).ScheduledAt,
        "The later reminder time must remain unchanged after an earlier reminder.");
    AssertEqual(3, finalSources[todoId].Count, "The third chat message should still be attached as a source.");

    // 已完成的待办不参与去重，也不能被继续合并。
    await todos.MarkCompletedAsync(todoId, now.AddHours(6), CancellationToken.None);
    var detector = new TodoDuplicateDetector(todos, new FakeMessageActionModel());
    var matchedAfterDone = await detector.DetectAsync(thirdMessage, CancellationToken.None);
    var mergeAfterDone = await service.MergeIntoExistingAsync(
        new MergeTodoRequest(todoId, thirdMessage.Id, null),
        CancellationToken.None);

    AssertEqual<TodoItem?>(null, matchedAfterDone, "A completed Todo must not be offered as a duplicate candidate.");
    AssertFalse(mergeAfterDone.Merged, "A completed Todo must reject further merges so a new Todo can be created.");
}

static async Task TestCapturePipelineReusesTodoAcrossChatsAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var reminderRepository = new SqliteReminderRepository(databasePath);
    var firstReminder = DateTimeOffset.Now.AddHours(2);
    var laterReminder = DateTimeOffset.Now.AddHours(4);
    // 两条 @我 消息来自不同群，模型第二次判定为同一件事。
    var model = new FakeMessageActionModel(
        new MentionMessageAnalysis(PriorityLevel.P1, "请在周五前提交验收报告。", FirstReminderAt: firstReminder),
        new MentionMessageAnalysis(PriorityLevel.P2, "请在周五前提交验收报告。", FirstReminderAt: laterReminder));
    var detector = new TodoDuplicateDetector(todoRepository, model);

    MessageCapturePipeline BuildPipeline() => new(
        adapters: Array.Empty<IMessageCaptureAdapter>(),
        new SqliteMessageProcessingUnitOfWork(databasePath),
        new SqliteProcessingOffsetRepository(databasePath),
        new MentionDetector(new[] { "我" }),
        new ProjectClassifier(Array.Empty<ProjectRule>()),
        new UrgencyRanker(priorityContacts: Array.Empty<string>(), priorityProjectIds: Array.Empty<long>()),
        messageRepository: messageRepository,
        analyzeMessageContent: () => true,
        analyzeMentionAsync: model.AnalyzeMentionAsync,
        detectDuplicateAsync: detector.DetectAsync);

    CapturedMessage Capture(string chatName, string sender, string content) => new(
        "WeChat",
        $"dedup-{Guid.NewGuid():N}",
        chatName,
        chatName,
        sender,
        content,
        DateTimeOffset.Now,
        MessageType.Text);

    var firstRun = await BuildPipeline().ProcessAsync(
        new[] { Capture("项目A群", "王工", "@我 请周五前提交验收报告") },
        CancellationToken.None);
    var created = (await todoRepository.GetPendingAsync(CancellationToken.None)).Single();
    // 第一条消息没有候选待办，检测器不会调用模型；第二条消息才带出命中结果。
    model.DuplicateTodoId = created.Id;

    var secondRun = await BuildPipeline().ProcessAsync(
        new[] { Capture("项目B群", "李工", "@我 记得周五前交验收报告") },
        CancellationToken.None);

    var pending = await todoRepository.GetPendingAsync(CancellationToken.None);
    var sources = await todoRepository.GetSourceMessagesForTodosAsync(new[] { created.Id }, CancellationToken.None);
    var reminders = await reminderRepository.GetForTodoAsync(created.Id, CancellationToken.None);

    AssertEqual(1, firstRun.CreatedTodoCount, "The first mention should create exactly one Todo.");
    AssertEqual(0, secondRun.CreatedTodoCount, "The repeated reminder from another chat must not create a second Todo.");
    AssertEqual(1, pending.Count, "Only one active Todo should remain after the cross-chat reminder.");
    AssertEqual(2, sources[created.Id].Count, "Both chats should be kept in the original message box.");
    AssertEqual("项目A群", sources[created.Id][0].ChatName, "The original chat should stay first.");
    AssertEqual("项目B群", sources[created.Id][1].ChatName, "The second chat should be appended.");
    AssertEqual(laterReminder, reminders.Single(item => item.Status == ReminderStatus.Scheduled).ScheduledAt,
        "A later reminder from the second chat should replace the earlier reminder time.");
    AssertEqual(1, model.DuplicateCandidates.Count, "Only the second mention should query the duplicate matcher.");
}

static async Task TestTodoDetailShowsMultipleSourceMessagesAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var messages = new SqliteMessageRepository(databasePath);
    var todos = new SqliteTodoRepository(databasePath);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var firstMessage = await messages.SaveAsync(
        CreateMessage(0, "项目A群", "王工", "请周五前提交验收报告") with { SourceMessageKey = $"detail-a-{Guid.NewGuid():N}" },
        CancellationToken.None);
    var secondMessage = await messages.SaveAsync(
        CreateMessage(0, "项目B群", "李工", "周五前交验收报告") with { SourceMessageKey = $"detail-b-{Guid.NewGuid():N}" },
        CancellationToken.None);
    var created = await todoService.CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(firstMessage.Id, null, null, null, null, null, null),
        CancellationToken.None);
    await todoService.MergeIntoExistingAsync(
        new MergeTodoRequest(created.Todo!.Id, secondMessage.Id, null),
        CancellationToken.None);

    var sourceMessages = (await todos.GetSourceMessagesForTodosAsync(
        new[] { created.Todo.Id },
        CancellationToken.None))[created.Todo.Id];
    var row = new TodoListItemViewModel(
        created.Todo,
        sourceMessages[0],
        null,
        TodoDueBucket.NoDueDate,
        "未指定项目",
        ProjectColorPolicy.DefaultColor,
        () => { },
        sourceMessages);
    var detail = new TodoDetailViewModel(
        row,
        todoService,
        todos,
        new SqliteFollowedProjectRepository(databasePath),
        new ReminderApplicationService(new SqliteTodoUnitOfWork(databasePath), clock),
        new SqliteReminderRepository(databasePath),
        clock,
        () => Task.CompletedTask,
        _ => Task.CompletedTask,
        new FakeTodoDialogService());

    AssertEqual(2, row.SourceMessageCount, "The list row should expose every source message count.");
    AssertEqual("2 条来源", row.SourceMessageBadge, "Multiple sources should be flagged in the list.");
    AssertTrue(row.HasMultipleSourceMessages, "The list row should report multiple sources.");
    AssertEqual(2, detail.OriginalMessages.Count, "The detail should show both chats in the original message box.");
    AssertTrue(detail.OriginalContent.Contains("项目A群", StringComparison.Ordinal),
        "The joined original content should keep the first chat header.");
    AssertTrue(detail.OriginalContent.Contains("项目B群", StringComparison.Ordinal),
        "The joined original content should include the merged chat header.");
    AssertTrue(detail.BuildCopyText().Contains("项目B群", StringComparison.Ordinal),
        "Copying all info should include the merged source message.");
    AssertEqual(
        System.Windows.Visibility.Collapsed,
        detail.PrimarySourceVisibility,
        "A multi-source Todo hides the single-source header rows.");
}

static async Task TestMessageFeedManualAgentConversionAsync()
{
    // 使用真实消息流、SQLite 工作单元和假模型覆盖完整手动点击链路。
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(8));
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));

    // 每条消息先写入本地 P2，便于区分手动 Agent 覆盖与模型失败回退。
    async Task<Message> SaveMessageAsync(string key, string content)
    {
        var saved = await messageRepository.SaveAsync(
            CreateMessage(0, "项目协作群", "李工", content) with { SourceMessageKey = key },
            CancellationToken.None);
        await InsertUrgencyScoreAsync(databasePath, saved.Id, 50, PriorityLevel.P2, "本地测试评分");
        return saved;
    }

    var actionable = await SaveMessageAsync("manual-agent-success", "请今天确认验收时间");
    var skipped = await SaveMessageAsync("manual-agent-skip", "本周版本已按计划发布");
    var blank = await SaveMessageAsync("manual-agent-blank", "请核对验收附件");
    var failure = await SaveMessageAsync("manual-agent-failure", "请回复接口字段");
    var model = new FakeMessageActionModel(
        new MentionMessageAnalysis(PriorityLevel.P1, "  请今天确认验收时间并回复。  "),
        new MentionMessageAnalysis(PriorityLevel.P3, "本周版本已按计划发布。", ShouldCreate: false),
        new MentionMessageAnalysis(PriorityLevel.P0, " \n\t "));
    var shown = new TaskCompletionSource<CreateTodoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    var feed = new MessageFeedViewModel(
        messageRepository,
        todoRepository,
        todoService,
        model,
        () => Array.Empty<string>(),
        () => Array.Empty<string>(),
        () => FollowedChatFilterMode.Include,
        result =>
        {
            // async void 命令通过现有导航回调通知测试本次创建已完成。
            shown.TrySetResult(result);
            return Task.CompletedTask;
        });

    async Task<CreateTodoResult> ConvertAsync(long messageId)
    {
        shown = new TaskCompletionSource<CreateTodoResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.ConvertToTodoCommand.Execute(messageId);
        return await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    async Task<PriorityLevel> EffectivePriorityAsync(long messageId)
    {
        var priorities = await messageRepository.GetEffectivePrioritiesAsync(new[] { messageId }, CancellationToken.None);
        return priorities[messageId];
    }

    await feed.RefreshAsync();
    var created = await ConvertAsync(actionable.Id);

    // 非 @我 消息同样调用 Agent，摘要和优先级同时进入待办与消息流评分。
    AssertEqual(CreateTodoOutcome.Created, created.Outcome, "A manual click should create a Todo after Agent analysis.");
    AssertEqual("请今天确认验收时间并回复。", created.Todo!.Description, "The manual Todo should use the trimmed Agent summary.");
    AssertEqual(PriorityLevel.P1, created.Todo.Priority, "The manual Todo should use the Agent priority.");
    AssertEqual(PriorityLevel.P1, await EffectivePriorityAsync(actionable.Id),
        "A successful manual analysis should append the Agent message-feed priority.");
    AssertEqual(1, model.Calls.Count, "The first manual conversion should invoke the Agent once.");
    AssertEqual(2, await CountUrgencyScoresAsync(databasePath, actionable.Id),
        "The Agent score should be appended after the original local score.");
    var actionRow = feed.Items.Single(item => item.MessageId == actionable.Id);
    AssertEqual(actionable.Content, actionRow.RawContent, "The message feed must keep the original content instead of the Agent summary.");
    AssertEqual("P1", actionRow.Priority, "The refreshed message row should show the Agent priority.");

    // 已有待办直接幂等打开，不再次分析，也不追加评分。
    var existing = await ConvertAsync(actionable.Id);
    AssertEqual(CreateTodoOutcome.ExistingTodo, existing.Outcome, "A repeated click should open the existing Todo.");
    AssertEqual(created.Todo.Id, existing.Todo!.Id, "A repeated click should preserve the original Todo.");
    AssertEqual(1, model.Calls.Count, "An existing Todo must not trigger another Agent call.");
    AssertEqual(2, await CountUrgencyScoresAsync(databasePath, actionable.Id),
        "An existing Todo must not append another Agent score.");

    // 手动点击是明确创建指令，即使模型返回 shouldCreate=false 也不能跳过。
    var skippedResult = await ConvertAsync(skipped.Id);
    AssertEqual(CreateTodoOutcome.Created, skippedResult.Outcome,
        "A manual click must ignore the model's automatic-creation rejection.");
    AssertEqual("本周版本已按计划发布。", skippedResult.Todo!.Description,
        "A rejected automatic action can still provide a neutral manual-Todo summary.");
    AssertEqual(PriorityLevel.P3, await EffectivePriorityAsync(skipped.Id),
        "The manually requested Todo should still use the returned Agent priority.");

    // 空白说明回退默认正文，但成功返回的 Agent 优先级仍然生效。
    var blankResult = await ConvertAsync(blank.Id);
    AssertEqual(CreateTodoOutcome.Created, blankResult.Outcome, "A blank Agent summary must not block manual creation.");
    AssertEqual($"项目协作群 / 李工: {blank.Content}", blankResult.Todo!.Description,
        "A blank Agent summary should use the standard source-message description.");
    AssertEqual(PriorityLevel.P0, blankResult.Todo.Priority, "A blank summary must not discard the Agent Todo priority.");
    AssertEqual(PriorityLevel.P0, await EffectivePriorityAsync(blank.Id),
        "A blank summary must not discard the Agent message-feed priority.");

    // 模型异常只影响分析结果，待办仍按原文和最后的本地评分创建。
    model.Exception = new InvalidOperationException("模拟模型故障");
    var failureResult = await ConvertAsync(failure.Id);
    AssertEqual(CreateTodoOutcome.Created, failureResult.Outcome, "A model failure must not block manual Todo creation.");
    AssertEqual($"项目协作群 / 李工: {failure.Content}", failureResult.Todo!.Description,
        "A model failure should fall back to the default source-message description.");
    AssertEqual(PriorityLevel.P2, failureResult.Todo.Priority, "A model failure should retain the local Todo priority.");
    AssertEqual(PriorityLevel.P2, await EffectivePriorityAsync(failure.Id),
        "A model failure must not replace the local message-feed priority.");
    AssertEqual(1, await CountUrgencyScoresAsync(databasePath, failure.Id),
        "A model failure must not append an Agent score.");
    AssertTrue(feed.StatusText.Contains("模型分析失败", StringComparison.Ordinal),
        "The status should explain that the Todo was created through the fallback path.");
    AssertEqual(4, model.Calls.Count, "Only four new manual conversions should invoke the Agent.");
}

static async Task TestManualAgentPriorityTransactionAsync()
{
    // 独立验证普通 Priority 不写消息评分，以及 Agent 评分与待办的原子性。
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(8));
    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));

    async Task<Message> SaveMessageAsync(string key)
    {
        var saved = await messageRepository.SaveAsync(
            CreateMessage(0, "事务测试群", "测试员", "请处理事务验证") with { SourceMessageKey = key },
            CancellationToken.None);
        await InsertUrgencyScoreAsync(databasePath, saved.Id, 50, PriorityLevel.P2, "本地测试评分");
        return saved;
    }

    async Task<PriorityLevel> EffectivePriorityAsync(long messageId)
    {
        var priorities = await messageRepository.GetEffectivePrioritiesAsync(new[] { messageId }, CancellationToken.None);
        return priorities[messageId];
    }

    // 普通 Priority 只修改待办，不得被误当成 Agent 评分覆盖消息流。
    var ordinaryMessage = await SaveMessageAsync("manual-ordinary-priority");
    var ordinary = await service.CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(
            ordinaryMessage.Id, null, null, null, PriorityLevel.P0, null, null),
        CancellationToken.None);
    AssertEqual(CreateTodoOutcome.Created, ordinary.Outcome, "The ordinary manual request should create its Todo.");
    AssertEqual(PriorityLevel.P0, ordinary.Todo!.Priority, "The ordinary request should still control Todo priority.");
    AssertEqual(PriorityLevel.P2, await EffectivePriorityAsync(ordinaryMessage.Id),
        "An ordinary Todo priority must not rewrite the message-feed score.");
    AssertEqual(1, await CountUrgencyScoresAsync(databasePath, ordinaryMessage.Id),
        "An ordinary Todo priority must not append an urgency score.");

    // 工作单元复查到已有待办时，应在 Agent 评分写入前返回。
    var existing = await service.CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(
            ordinaryMessage.Id, null, null, null, null, null, null, AgentPriority: PriorityLevel.P1),
        CancellationToken.None);
    AssertEqual(CreateTodoOutcome.ExistingTodo, existing.Outcome, "The transactional recheck should reuse the existing Todo.");
    AssertEqual(1, await CountUrgencyScoresAsync(databasePath, ordinaryMessage.Id),
        "The transactional existing-Todo path must not append an Agent score.");

    // 删除提醒表制造后续写入故障，验证 Agent 评分和新待办一起回滚。
    var rollbackMessage = await SaveMessageAsync("manual-agent-rollback");
    await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
    {
        await connection.OpenAsync(CancellationToken.None);
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE todo_reminders;";
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }

    var failureObserved = false;
    try
    {
        await service.CreateFromMessageAsync(
            new CreateTodoFromMessageRequest(
                rollbackMessage.Id, null, null, null, null, null, now.AddHours(1),
                AgentPriority: PriorityLevel.P0),
            CancellationToken.None);
    }
    catch (SqliteException)
    {
        failureObserved = true;
    }

    AssertTrue(failureObserved, "The test should force a reminder failure after the Agent score insert.");
    AssertEqual(1, await CountUrgencyScoresAsync(databasePath, rollbackMessage.Id),
        "A failed Todo transaction must roll back its appended Agent score.");
    AssertEqual(PriorityLevel.P2, await EffectivePriorityAsync(rollbackMessage.Id),
        "A failed Todo transaction must leave the local message-feed score unchanged.");
    AssertTrue(await todoRepository.GetBySourceMessageIdAsync(rollbackMessage.Id, CancellationToken.None) is null,
        "A failed reminder insert must also roll back the new Todo.");
}

static async Task TestDeepSeekMessageActionModelAsync()
{
    // 保存并恢复原环境，避免测试影响开发者已有的模型配置。
    var variableNames = new[]
    {
        "WECHATDASHBOARD_AGENT_ENDPOINT",
        "WECHATDASHBOARD_AGENT_MODEL",
        "WECHATDASHBOARD_AGENT_API_KEY"
    };
    var originalValues = variableNames.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
    var modelResult = JsonSerializer.Serialize(new
    {
        shouldCreate = false,
        priority = "P3",
        description = "验收时间已同步。"
    });
    var responseBody = JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new { finish_reason = "stop", message = new { content = modelResult } }
        }
    });

    try
    {
        // 即使存在环境变量，本机已保存密钥也必须优先使用固定 DeepSeek 配置。
        Environment.SetEnvironmentVariable(variableNames[0], "https://fallback.example/v1/chat/completions");
        Environment.SetEnvironmentVariable(variableNames[1], "fallback-model");
        Environment.SetEnvironmentVariable(variableNames[2], "fallback-key");
        var message = CreateMessage(9001, "验收群", "王工", "请确认验收时间");
        var savedHandler = new RecordingModelHttpMessageHandler(responseBody);
        using var savedHttp = new HttpClient(savedHandler);
        var savedModel = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment("sk-saved-test", savedHttp);
        var savedAnalysis = await savedModel.AnalyzeMentionAsync(message, CancellationToken.None);

        AssertFalse(savedAnalysis.ShouldCreate, "DeepSeek response should use the retained analysis parser.");
        AssertEqual("验收时间已同步。", savedAnalysis.Description, "The saved-key response should preserve its neutral summary.");
        AssertEqual("https://api.deepseek.com/chat/completions", savedHandler.RequestUri?.ToString(),
            "A saved API key must select the fixed DeepSeek endpoint.");
        AssertEqual("sk-saved-test", savedHandler.AuthorizationParameter, "The saved API key must be used for Bearer auth.");
        using var savedBody = JsonDocument.Parse(savedHandler.RequestBody
            ?? throw new InvalidOperationException("DeepSeek request body was not recorded."));
        AssertEqual("deepseek-flash", savedBody.RootElement.GetProperty("model").GetString(),
            "The built-in DeepSeek model should be used.");
        AssertEqual("json_object", savedBody.RootElement.GetProperty("response_format").GetProperty("type").GetString(),
            "DeepSeek must keep JSON output enabled.");
        AssertEqual("disabled", savedBody.RootElement.GetProperty("thinking").GetProperty("type").GetString(),
            "DeepSeek thinking should be disabled for bounded JSON suggestions.");

        // 没有本机密钥时，原有环境变量配置和通用请求格式保持不变。
        var fallbackHandler = new RecordingModelHttpMessageHandler(responseBody);
        using var fallbackHttp = new HttpClient(fallbackHandler);
        var fallbackModel = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment(null, fallbackHttp);
        await fallbackModel.AnalyzeMentionAsync(message, CancellationToken.None);
        AssertEqual("https://fallback.example/v1/chat/completions", fallbackHandler.RequestUri?.ToString(),
            "Missing saved key should preserve the environment endpoint.");
        AssertEqual("fallback-key", fallbackHandler.AuthorizationParameter, "Environment API key fallback should remain available.");
        using var fallbackBody = JsonDocument.Parse(fallbackHandler.RequestBody
            ?? throw new InvalidOperationException("Fallback request body was not recorded."));
        AssertEqual("fallback-model", fallbackBody.RootElement.GetProperty("model").GetString(),
            "Environment model fallback should remain available.");
        AssertFalse(fallbackBody.RootElement.TryGetProperty("thinking", out _),
            "Generic OpenAI-compatible requests must not receive the DeepSeek thinking option.");
    }
    finally
    {
        foreach (var name in variableNames)
        {
            Environment.SetEnvironmentVariable(name, originalValues[name]);
        }
    }
}

static async Task TestDeepSeekMentionAnalysisAsync()
{
    // 固定响应只经过假 HTTP handler，验证协议而不会访问真实 DeepSeek 服务。
    static string CreateResponse(string content) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new { finish_reason = "stop", message = new { content } }
        }
    });

    var message = CreateMessage(9100, "验收群", "王工", "@我 请今天确认验收时间");
    var handler = new RecordingModelHttpMessageHandler(CreateResponse(
        "{\"shouldCreate\":true,\"priority\":\"P1\",\"description\":\"  请今天确认验收时间。  \",\"dueAt\":\"2026-08-09T18:00:00+08:00\",\"reminderAt\":\"2026-08-09T17:00:00+08:00\"}"));
    using var http = new HttpClient(handler);
    var model = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment("sk-analysis-test", http);

    var analysis = await model.AnalyzeMentionAsync(message, CancellationToken.None);

    AssertTrue(analysis.ShouldCreate, "An actionable mention should be approved for automatic Todo creation.");
    AssertEqual(PriorityLevel.P1, analysis.Priority, "The Agent should return the automatic Todo priority.");
    AssertEqual("请今天确认验收时间。", analysis.Description, "The Agent summary should be trimmed.");
    AssertEqual(
        new DateTimeOffset(2026, 8, 9, 18, 0, 0, TimeSpan.FromHours(8)),
        analysis.DueAt,
        "The Agent should return the parsed completion/deadline time.");
    AssertEqual(
        new DateTimeOffset(2026, 8, 9, 17, 0, 0, TimeSpan.FromHours(8)),
        analysis.FirstReminderAt,
        "The Agent should return the parsed reminder time.");
    AssertEqual("sk-analysis-test", handler.AuthorizationParameter, "Analysis requests must use the saved Bearer key.");
    using var body = JsonDocument.Parse(handler.RequestBody
        ?? throw new InvalidOperationException("Analysis request body was not recorded."));
    AssertEqual(500, body.RootElement.GetProperty("max_tokens").GetInt32(),
        "Priority, summary and time output should use a bounded token limit.");
    AssertEqual("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString(),
        "The combined analysis should require a JSON object.");
    AssertEqual("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString(),
        "The built-in DeepSeek analysis should disable thinking.");

    // 用户输入只能含当前消息，不能混入相邻消息或澄清回答。
    var requestMessages = body.RootElement.GetProperty("messages");
    AssertEqual(2, requestMessages.GetArrayLength(), "An analysis should contain one system and one user message.");
    var userContent = requestMessages[1].GetProperty("content").GetString()
        ?? throw new InvalidOperationException("Analysis user input was missing.");
    using var input = JsonDocument.Parse(userContent);
    AssertEqual(message.Content, input.RootElement.GetProperty("currentMessage").GetProperty("content").GetString(),
        "The analysis input should contain the current message.");
    AssertFalse(input.RootElement.TryGetProperty("adjacentMessages", out _),
        "The analysis must not send adjacent messages.");
    AssertFalse(input.RootElement.TryGetProperty("userAnswer", out _),
        "The analysis must not send a clarification answer.");

    // 空白说明不会丢弃已返回的有效优先级，后续由待办工厂回退默认说明。
    var blankHandler = new RecordingModelHttpMessageHandler(CreateResponse(
        "{\"shouldCreate\":true,\"priority\":\"P3\",\"description\":\" \\n\\t \",\"dueAt\":null,\"reminderAt\":null}"));
    using var blankHttp = new HttpClient(blankHandler);
    var blankModel = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment("sk-analysis-test", blankHttp);
    var blankAnalysis = await blankModel.AnalyzeMentionAsync(message, CancellationToken.None);
    AssertEqual(PriorityLevel.P3, blankAnalysis.Priority, "A blank summary must preserve the parsed priority.");
    AssertEqual<string?>(null, blankAnalysis.Description, "A blank summary should request the default Todo description.");
    AssertEqual<DateTimeOffset?>(null, blankAnalysis.DueAt, "Null time fields should parse as no explicit time.");
    AssertEqual<DateTimeOffset?>(null, blankAnalysis.FirstReminderAt, "Null reminder fields should parse as no explicit reminder.");

    // 模型对无关或低优先级消息可以明确否决创建，解析层必须保留该决定。
    var skipHandler = new RecordingModelHttpMessageHandler(CreateResponse(
        "{\"shouldCreate\":false,\"priority\":\"P3\",\"description\":\"仅同步进展，无需处理\"}"));
    using var skipHttp = new HttpClient(skipHandler);
    var skipModel = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment("sk-analysis-test", skipHttp);
    var skipAnalysis = await skipModel.AnalyzeMentionAsync(message, CancellationToken.None);
    AssertFalse(skipAnalysis.ShouldCreate, "The model's no-Todo decision must survive JSON parsing.");
    AssertEqual(PriorityLevel.P3, skipAnalysis.Priority, "A skipped item should retain its analyzed priority.");
}

static async Task TestReminderSnoozeAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var reminderRepository = new SqliteReminderRepository(databasePath);
    var now = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8));
    var message = await messageRepository.SaveAsync(
        CreateMessage(0, "CRM项目群", "王经理", "下午确认发布窗口") with { SourceMessageKey = $"snooze-{Guid.NewGuid():N}" },
        CancellationToken.None);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var todoService = new TodoApplicationService(unitOfWork, new FixedClock(now));
    var dueAt = now.AddHours(4);
    var created = await todoService.CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(message.Id, null, null, null, null, dueAt, now.AddMinutes(10)),
        CancellationToken.None);
    var originalReminder = (await reminderRepository.GetForTodoAsync(created.Todo!.Id, CancellationToken.None)).Single();
    var snoozeUntil = now.AddHours(1);
    var reminderService = new ReminderApplicationService(unitOfWork, new FixedClock(now));

    var snoozed = await reminderService.SnoozeAsync(originalReminder.Id, snoozeUntil, CancellationToken.None);
    var reminders = await reminderRepository.GetForTodoAsync(created.Todo.Id, CancellationToken.None);
    var reminderBatch = await reminderRepository.GetForTodosAsync(
        new[] { created.Todo.Id, long.MaxValue },
        CancellationToken.None);
    var persistedTodo = await todoRepository.GetByIdAsync(created.Todo.Id, CancellationToken.None);

    AssertTrue(snoozed.Succeeded, "A scheduled reminder should be snoozable.");
    AssertEqual(2, reminders.Count, "Snooze should preserve the old reminder and create a new one.");
    AssertEqual(1, reminderBatch.Count, "Batch reminder lookup should omit Todo ids without reminders.");
    AssertEqual(2, reminderBatch[created.Todo.Id].Count, "Batch reminder lookup should return the full reminder history in one query.");
    AssertEqual(ReminderStatus.Snoozed, reminders.Single(item => item.Id == originalReminder.Id).Status, "The old reminder should retain snooze history.");
    AssertEqual(ReminderStatus.Scheduled, reminders.Single(item => item.Id != originalReminder.Id).Status, "The replacement reminder should be scheduled.");
    AssertEqual(originalReminder.Id, reminders.Single(item => item.Id != originalReminder.Id).ParentReminderId, "The replacement should link to the old reminder.");
    AssertEqual(snoozeUntil, reminders.Single(item => item.Id != originalReminder.Id).ScheduledAt, "The replacement time should match the request.");
    AssertEqual(dueAt, persistedTodo!.DueAt, "Snoozing must not change the Todo due time.");

    var completed = await todoService.UpdateAsync(
        new UpdateTodoRequest(
            persistedTodo.Id,
            persistedTodo.Title,
            persistedTodo.Description,
            persistedTodo.ProjectId,
            persistedTodo.Priority,
            persistedTodo.DueAt,
            TodoStatus.Done,
            persistedTodo.UpdatedAt),
        CancellationToken.None);
    var afterCompletion = await reminderRepository.GetForTodoAsync(persistedTodo.Id, CancellationToken.None);
    AssertEqual(TodoStatus.Done, completed!.Status, "The Todo should complete through the application transaction.");
    AssertEqual(ReminderStatus.Cancelled, afterCompletion.Last().Status, "Completing a Todo should cancel its open replacement reminder.");
}

static async Task TestReminderWorkerAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var now = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8));
    var message = await new SqliteMessageRepository(databasePath).SaveAsync(
        CreateMessage(0, "提醒测试群", "测试员", "提醒测试") with { SourceMessageKey = $"worker-{Guid.NewGuid():N}" },
        CancellationToken.None);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var created = await new TodoApplicationService(unitOfWork, new FixedClock(now)).CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(message.Id, null, null, null, null, null, now.AddMinutes(1)),
        CancellationToken.None);
    var publisher = new RecordingNotificationPublisher();
    var repository = new SqliteReminderRepository(databasePath);
    var worker = new ReminderWorker(repository, publisher, new FixedClock(now.AddMinutes(2)));

    var firstDelivered = await worker.RunOnceAsync(CancellationToken.None);
    var secondDelivered = await worker.RunOnceAsync(CancellationToken.None);
    var reminder = (await repository.GetForTodoAsync(created.Todo!.Id, CancellationToken.None)).Single();

    AssertEqual(1, firstDelivered, "The first worker pass should deliver the due reminder.");
    AssertEqual(0, secondDelivered, "A delivered reminder must not be delivered twice.");
    AssertEqual(1, publisher.PublishCount, "The notification adapter should be called once.");
    AssertEqual(ReminderStatus.Delivered, reminder.Status, "The reminder should persist its delivered state.");
}

static async Task TestReminderWorkerRecoversStaleClaimAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var now = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8));
    var message = await new SqliteMessageRepository(databasePath).SaveAsync(
        CreateMessage(0, "恢复测试群", "测试员", "恢复中断提醒") with { SourceMessageKey = $"recovery-{Guid.NewGuid():N}" },
        CancellationToken.None);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var created = await new TodoApplicationService(unitOfWork, new FixedClock(now)).CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(message.Id, null, null, null, null, null, now.AddMinutes(1)),
        CancellationToken.None);
    var repository = new SqliteReminderRepository(databasePath);
    var reminder = (await repository.GetForTodoAsync(created.Todo!.Id, CancellationToken.None)).Single();
    AssertTrue(
        await repository.TryClaimAsync(reminder.Id, now.AddMinutes(-10), CancellationToken.None),
        "The test reminder should simulate a claim left behind by a crashed process.");
    var publisher = new RecordingNotificationPublisher();
    var worker = new ReminderWorker(repository, publisher, new FixedClock(now.AddMinutes(2)));

    var delivered = await worker.RunOnceAsync(CancellationToken.None);
    var recovered = await repository.GetByIdAsync(reminder.Id, CancellationToken.None);

    AssertEqual(1, delivered, "A stale dispatch claim should be recovered and delivered.");
    AssertEqual(1, publisher.PublishCount, "The recovered reminder should be published once.");
    AssertEqual(ReminderStatus.Delivered, recovered!.Status, "The recovered reminder should finish as delivered.");
    AssertEqual(1, recovered.AttemptCount, "Recovery should leave an auditable retry count.");
}

static async Task TestMessageContextQueryAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var repository = new SqliteMessageRepository(databasePath);
    var sentAt = new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.FromHours(8));
    var saved = new List<Message>();
    for (var index = 0; index < 5; index++)
    {
        saved.Add(await repository.SaveAsync(
            CreateMessage(0, "上下文群", $"成员{index}", $"消息{index}") with
            {
                SourceMessageKey = $"context-{Guid.NewGuid():N}-{index}",
                SentAt = sentAt.AddMinutes(index)
            },
            CancellationToken.None));
    }

    var context = await repository.GetContextAsync(saved[2].Id, before: 1, after: 1, CancellationToken.None);

    AssertTrue(context is not null, "An existing source message should produce a context result.");
    AssertEqual(saved[2].Id, context!.Anchor.Id, "The context anchor must be selected by stable database id.");
    AssertEqual(3, context.Messages.Count, "The context should include one message before and after the anchor.");
    AssertEqual(saved[3].Id, context.Messages[0].Id, "Context should keep the normal newest-first display order.");
    AssertEqual(saved[2].Id, context.Messages[1].Id, "The anchor should remain in chronological position.");
    AssertEqual(saved[1].Id, context.Messages[2].Id, "The older context message should follow the anchor.");
}

static async Task TestMessageFeedContextSurvivesRefreshAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var todoService = new TodoApplicationService(
        new SqliteTodoUnitOfWork(databasePath),
        new FixedClock(new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(8))));
    var sentAt = new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.FromHours(8));
    var saved = new List<Message>();
    for (var index = 0; index < 5; index++)
    {
        saved.Add(await messageRepository.SaveAsync(
            CreateMessage(0, "上下文群", $"成员{index}", $"消息{index}") with
            {
                SourceMessageKey = $"feed-context-{Guid.NewGuid():N}-{index}",
                SentAt = sentAt.AddMinutes(index)
            },
            CancellationToken.None));
    }

    var feed = new MessageFeedViewModel(
        messageRepository,
        todoRepository,
        todoService,
        new FakeMessageActionModel(),
        () => Array.Empty<string>(),
        () => Array.Empty<string>(),
        () => FollowedChatFilterMode.Include,
        _ => Task.CompletedTask);

    // 复现“查看原始消息”：切页触发自动刷新，随后立即定位上下文
    var target = saved[2].Id;
    feed.PrepareContextNavigation(target);
    var refresh = feed.RefreshAsync();
    await feed.ShowContextAsync(target);
    await refresh;

    AssertTrue(feed.IsContextMode, "A tab-triggered refresh must not switch the feed back to the plain list.");
    AssertEqual(target, feed.SelectedMessage?.MessageId ?? 0, "Navigation must keep the source message selected.");
}

static Task TestDataGridScrollSelectionClearedAsync()
{
    Exception? threadFailure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var items = new System.Collections.ObjectModel.ObservableCollection<string> { "第一条消息", "第二条消息" };
            var grid = new System.Windows.Controls.DataGrid { ItemsSource = items };
            WechatDashboard.App.Behaviors.DataGridScrollIntoViewBehavior.SetIsEnabled(grid, true);
            grid.SelectedItem = items[0];
            AssertTrue(grid.SelectedItem is not null, "The DataGrid should accept the programmatic selection.");
            grid.SelectedItem = null; // 刷新清空选中，此时滚动回调尚未执行
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => { }));
        }
        catch (Exception ex)
        {
            threadFailure = ex;
        }
        finally
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();

    AssertTrue(thread.Join(TimeSpan.FromSeconds(10)), "The DataGrid scroll check should not hang.");
    AssertTrue(
        threadFailure is null,
        $"Scrolling must be skipped after the selection is cleared: {threadFailure}");
    return Task.CompletedTask;
}

static async Task TestSqliteRoundTripAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);

    var message = CreateMessage(
        id: 0,
        chatName: "CRM项目群",
        senderName: "王经理",
        content: "@张三 请今天处理线上故障");

    var savedMessage = await messageRepository.SaveAsync(message, CancellationToken.None);
    var classification = new ClassificationResult(savedMessage.Id, 1, "CRM升级", MessageCategory.Incident, 0.95, "chat rule", "Rules");
    var urgency = new UrgencyScore(savedMessage.Id, 90, PriorityLevel.P0, "@我; 线上故障; 今天");
    var todo = TodoService.CreateFromMention(savedMessage, classification, urgency);

    var savedTodo = await todoRepository.SaveAsync(todo, CancellationToken.None);
    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);
    var recentMessages = await messageRepository.GetRecentAsync(10, CancellationToken.None);
    var messagesById = await messageRepository.GetByIdsAsync(new[] { savedMessage.Id, long.MaxValue }, CancellationToken.None);
    var messagesBySourceKey = await messageRepository.GetBySourceKeysAsync(
        new[] { new MessageIdentity(savedMessage.Source, savedMessage.SourceMessageKey) },
        CancellationToken.None);

    AssertTrue(savedMessage.Id > 0, "Saved message should get a database id.");
    AssertTrue(savedTodo.Id > 0, "Saved todo should get a database id.");
    AssertEqual(1, pendingTodos.Count, "One pending todo should round-trip.");
    AssertEqual("@张三 请今天处理线上故障", recentMessages.Single().Content, "Recent message content should round-trip.");
    AssertEqual(1, messagesById.Count, "ID lookup should return only matching messages.");
    AssertEqual(savedMessage.SentAt, messagesById.Single().SentAt, "ID lookup should preserve the original message time.");
    AssertEqual(savedMessage.Id, messagesBySourceKey.Single().Id, "Source-key lookup should resolve the canonical persisted message id.");
}

static async Task TestMessageTodoTransactionRollbackAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var unitOfWork = new SqliteMessageProcessingUnitOfWork(databasePath);
    var message = CreateMessage(
        id: 0,
        chatName: "CRM项目群",
        senderName: "王经理",
        content: "@张三 紧急，今天处理线上故障") with
    {
        SourceMessageKey = $"transaction-{Guid.NewGuid():N}",
        IsMentionMe = true
    };

    MessageProcessingArtifacts BuildArtifacts(Message savedMessage)
    {
        var classification = new ClassificationResult(
            savedMessage.Id,
            1,
            "CRM升级",
            MessageCategory.Incident,
            0.95,
            "测试规则",
            "Rules");
        var urgency = new UrgencyScore(savedMessage.Id, 90, PriorityLevel.P0, "@我; 紧急");
        var todo = TodoService.CreateFromMention(savedMessage, classification, urgency);
        return new MessageProcessingArtifacts(classification, urgency, todo);
    }

    async Task<int> CountRowsAsync(string tableName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    async Task<(long MessageId, string Category, double Confidence, string Reason, string Classifier)> ReadClassificationAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id, category, confidence, reason, classifier FROM message_classifications LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        AssertTrue(await reader.ReadAsync(CancellationToken.None), "A classification row should be readable.");
        return (reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2), reader.GetString(3), reader.GetString(4));
    }

    async Task<(long MessageId, int Score, string Priority, string Reason)> ReadUrgencyAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id, score, priority, reason FROM urgency_scores LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        AssertTrue(await reader.ReadAsync(CancellationToken.None), "An urgency row should be readable.");
        return (reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
    }

    await using (var failureConnection = new SqliteConnection($"Data Source={databasePath}"))
    {
        await failureConnection.OpenAsync(CancellationToken.None);
        await using var failureCommand = failureConnection.CreateCommand();
        failureCommand.CommandText = """
            CREATE TRIGGER fail_todo_insert
            BEFORE INSERT ON todo_items
            BEGIN
                SELECT RAISE(ABORT, 'forced Todo insert failure');
            END;
            """;
        await failureCommand.ExecuteNonQueryAsync(CancellationToken.None);
    }

    var insertFailed = false;
    try
    {
        await unitOfWork.TryProcessAsync(message, BuildArtifacts, CancellationToken.None);
    }
    catch (SqliteException)
    {
        insertFailed = true;
    }

    AssertTrue(insertFailed, "The forced Todo insert should fail inside the SQLite transaction.");
    AssertFalse(
        await messageRepository.ExistsAsync(message.Source, message.SourceMessageKey, CancellationToken.None),
        "The message insert must roll back when Todo insertion fails.");
    AssertEqual(0, (await todoRepository.GetPendingAsync(CancellationToken.None)).Count, "The failed transaction must leave no Todo.");
    AssertEqual(0, await CountRowsAsync("message_classifications"), "The failed transaction must roll back classification results.");
    AssertEqual(0, await CountRowsAsync("urgency_scores"), "The failed transaction must roll back urgency results.");

    await using (var recoveryConnection = new SqliteConnection($"Data Source={databasePath}"))
    {
        await recoveryConnection.OpenAsync(CancellationToken.None);
        await using var recoveryCommand = recoveryConnection.CreateCommand();
        recoveryCommand.CommandText = "DROP TRIGGER fail_todo_insert;";
        await recoveryCommand.ExecuteNonQueryAsync(CancellationToken.None);
    }

    var retry = await unitOfWork.TryProcessAsync(message, BuildArtifacts, CancellationToken.None);
    var persistedMessages = await messageRepository.GetRecentAsync(10, CancellationToken.None);
    var persistedTodos = await todoRepository.GetPendingAsync(CancellationToken.None);

    AssertTrue(retry.Persisted, "A valid retry should persist the message after rollback.");
    AssertTrue(retry.CreatedTodo, "A valid retry should create the Todo atomically.");
    AssertEqual(1, persistedMessages.Count, "Exactly one message should exist after the successful retry.");
    AssertEqual(1, persistedTodos.Count, "Exactly one Todo should exist after the successful retry.");
    AssertEqual(1, await CountRowsAsync("message_classifications"), "The successful retry should persist one classification result.");
    AssertEqual(1, await CountRowsAsync("urgency_scores"), "The successful retry should persist one urgency result.");
    AssertEqual(persistedMessages.Single().Id, persistedTodos.Single().SourceMessageId, "The Todo must reference the committed message.");
    var persistedClassification = await ReadClassificationAsync();
    var persistedUrgency = await ReadUrgencyAsync();
    AssertEqual(persistedMessages.Single().Id, persistedClassification.MessageId, "The classification must reference the committed message.");
    AssertEqual(MessageCategory.Incident.ToString(), persistedClassification.Category, "The classification category should round-trip.");
    AssertEqual(0.95, persistedClassification.Confidence, "The classification confidence should round-trip.");
    AssertEqual("测试规则", persistedClassification.Reason, "The classification reason should round-trip.");
    AssertEqual("Rules", persistedClassification.Classifier, "The classifier name should round-trip.");
    AssertEqual(persistedMessages.Single().Id, persistedUrgency.MessageId, "The urgency score must reference the committed message.");
    AssertEqual(90, persistedUrgency.Score, "The urgency score should round-trip.");
    AssertEqual(PriorityLevel.P0.ToString(), persistedUrgency.Priority, "The urgency priority should round-trip.");
    AssertEqual("@我; 紧急", persistedUrgency.Reason, "The urgency reason should round-trip.");

    var duplicate = await unitOfWork.TryProcessAsync(message, BuildArtifacts, CancellationToken.None);
    AssertFalse(duplicate.Persisted, "The source-message unique key should reject a duplicate atomically.");
    AssertFalse(duplicate.CreatedTodo, "A duplicate message must not create a second Todo.");
    AssertEqual(1, (await messageRepository.GetRecentAsync(10, CancellationToken.None)).Count, "Duplicate processing must keep one message.");
    AssertEqual(1, (await todoRepository.GetPendingAsync(CancellationToken.None)).Count, "Duplicate processing must keep one Todo.");
    AssertEqual(1, await CountRowsAsync("message_classifications"), "Duplicate processing must keep one classification result.");
    AssertEqual(1, await CountRowsAsync("urgency_scores"), "Duplicate processing must keep one urgency result.");
}

static async Task TestTodoCompletionAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var todoRepository = new SqliteTodoRepository(databasePath);
    var now = DateTimeOffset.UtcNow;
    var todo = new TodoItem(
        Id: 0,
        SourceMessageId: null,
        ProjectId: null,
        Title: "确认测试记录",
        Description: null,
        Status: TodoStatus.Pending,
        Priority: PriorityLevel.P2,
        DueAt: null,
        CreatedAt: now.AddMinutes(-5),
        UpdatedAt: now.AddMinutes(-5),
        CompletedAt: null,
        IsAutoCreated: false);

    var savedTodo = await todoRepository.SaveAsync(todo, CancellationToken.None);
    var completionTime = now.AddSeconds(1);
    var updated = await todoRepository.MarkCompletedAsync(savedTodo.Id, completionTime, CancellationToken.None);
    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);
    var completedTodos = await todoRepository.GetCompletedAsync(CancellationToken.None);

    AssertTrue(updated, "Pending todo should be updated exactly once.");
    AssertEqual(0, pendingTodos.Count, "Completed todo should leave the pending list.");
    AssertEqual(1, completedTodos.Count, "Completed todo should appear in the completed list.");
    AssertEqual(TodoStatus.Done, completedTodos.Single().Status, "Completed todo should have Done status.");
    AssertEqual(completionTime, completedTodos.Single().CompletedAt, "Completion time should be persisted.");
    AssertFalse(
        await todoRepository.MarkCompletedAsync(savedTodo.Id, completionTime, CancellationToken.None),
        "Completing the same todo twice should not update it again.");

    await todoRepository.SaveAsync(
        todo with
        {
            Id = 0,
            Title = "批量办理记录一",
            UpdatedAt = now,
            CompletedAt = null
        },
        CancellationToken.None);
    await todoRepository.SaveAsync(
        todo with
        {
            Id = 0,
            Title = "批量办理记录二",
            Status = TodoStatus.InProgress,
            UpdatedAt = now,
            CompletedAt = null
        },
        CancellationToken.None);
    await todoRepository.SaveAsync(
        todo with
        {
            Id = 0,
            Title = "批量办理记录三",
            Status = TodoStatus.Waiting,
            UpdatedAt = now,
            CompletedAt = null
        },
        CancellationToken.None);

    var bulkCompletionTime = now.AddSeconds(2);
    var bulkUpdatedCount = await todoRepository.MarkAllCompletedAsync(bulkCompletionTime, CancellationToken.None);
    var remainingActiveTodos = await todoRepository.GetActiveAsync(CancellationToken.None);
    var allCompletedTodos = await todoRepository.GetCompletedAsync(CancellationToken.None);

    AssertEqual(3, bulkUpdatedCount, "Bulk completion should update every active todo status.");
    AssertEqual(0, remainingActiveTodos.Count, "Bulk completion should leave no active todos.");
    AssertEqual(4, allCompletedTodos.Count, "Individually and bulk completed todos should all be returned.");
    AssertEqual(3, allCompletedTodos.Count(item => item.CompletedAt == bulkCompletionTime), "Bulk completion time should be applied consistently.");
}

static async Task TestTodoPinningAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);
    await initializer.InitializeAsync(CancellationToken.None);

    var repository = new SqliteTodoRepository(databasePath);
    var now = DateTimeOffset.UtcNow;
    var template = new TodoItem(
        Id: 0,
        SourceMessageId: null,
        ProjectId: null,
        Title: "普通待办",
        Description: null,
        Status: TodoStatus.Pending,
        Priority: PriorityLevel.P2,
        DueAt: now.AddDays(1),
        CreatedAt: now,
        UpdatedAt: now,
        CompletedAt: null,
        IsAutoCreated: false);
    var ordinary = await repository.SaveAsync(template, CancellationToken.None);
    var pinned = await repository.SaveAsync(
        template with
        {
            Id = 0,
            Title = "需要置顶的待办",
            DueAt = now.AddDays(2),
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5)
        },
        CancellationToken.None);

    AssertTrue(
        await repository.SetPinnedAsync(pinned.Id, true, now.AddSeconds(1), CancellationToken.None),
        "An active todo should be pinnable.");
    var active = await repository.GetActiveAsync(CancellationToken.None);
    var reloaded = await repository.GetByIdAsync(pinned.Id, CancellationToken.None);

    AssertEqual(pinned.Id, active[0].Id, "Pinned todo should sort before an earlier-due ordinary todo.");
    AssertTrue(reloaded!.IsPinned, "Pinned state should survive a repository reload.");
    AssertFalse(active.Single(item => item.Id == ordinary.Id).IsPinned, "Ordinary todo should remain unpinned.");

    AssertTrue(
        await repository.SetPinnedAsync(pinned.Id, false, now.AddSeconds(2), CancellationToken.None),
        "Pinned state should be removable.");
    AssertFalse(
        (await repository.GetByIdAsync(pinned.Id, CancellationToken.None))!.IsPinned,
        "Unpinned state should persist.");
}

static async Task TestSelectedTodoCompletionAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var repository = new SqliteTodoRepository(databasePath);
    var now = DateTimeOffset.UtcNow;
    var template = new TodoItem(
        Id: 0,
        SourceMessageId: null,
        ProjectId: null,
        Title: "选择办理一",
        Description: null,
        Status: TodoStatus.Pending,
        Priority: PriorityLevel.P2,
        DueAt: null,
        CreatedAt: now,
        UpdatedAt: now,
        CompletedAt: null,
        IsAutoCreated: false);
    var first = await repository.SaveAsync(template, CancellationToken.None);
    var second = await repository.SaveAsync(
        template with { Id = 0, Title = "保留未办理", Status = TodoStatus.InProgress },
        CancellationToken.None);
    var third = await repository.SaveAsync(
        template with { Id = 0, Title = "选择办理二", Status = TodoStatus.Waiting },
        CancellationToken.None);
    await repository.SetPinnedAsync(first.Id, true, now.AddSeconds(1), CancellationToken.None);

    var completionTime = now.AddSeconds(2);
    var updated = await repository.MarkSelectedCompletedAsync(
        new[] { first.Id, third.Id, third.Id, -1 },
        completionTime,
        CancellationToken.None);
    var active = await repository.GetActiveAsync(CancellationToken.None);
    var completed = await repository.GetCompletedAsync(CancellationToken.None);

    AssertEqual(2, updated, "Only two distinct selected active todos should complete.");
    AssertEqual(1, active.Count, "One unselected active todo should remain.");
    AssertEqual(second.Id, active.Single().Id, "The unselected todo must remain active.");
    AssertEqual(2, completed.Count, "Only selected todos should appear in completed history.");
    AssertTrue(completed.All(item => item.CompletedAt == completionTime), "Selected completion should use one timestamp.");
    AssertTrue(completed.All(item => !item.IsPinned), "Completing a selected todo should clear its pinned state.");
    AssertFalse(
        await repository.SetPinnedAsync(first.Id, true, completionTime.AddSeconds(1), CancellationToken.None),
        "Completed todos must not be pinnable.");
    AssertEqual(
        0,
        await repository.MarkSelectedCompletedAsync(Array.Empty<long>(), completionTime, CancellationToken.None),
        "An empty selection should not update any todo.");
}

static async Task TestDeleteCompletedTodosAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var todoRepository = new SqliteTodoRepository(databasePath);
    var now = DateTimeOffset.UtcNow;
    var pendingTodo = new TodoItem(
        Id: 0,
        SourceMessageId: null,
        ProjectId: null,
        Title: "保留的待办理记录",
        Description: null,
        Status: TodoStatus.Pending,
        Priority: PriorityLevel.P2,
        DueAt: null,
        CreatedAt: now,
        UpdatedAt: now,
        CompletedAt: null,
        IsAutoCreated: false);

    await todoRepository.SaveAsync(pendingTodo, CancellationToken.None);
    await todoRepository.SaveAsync(
        pendingTodo with
        {
            Id = 0,
            Title = "删除的已办理记录一",
            Status = TodoStatus.Done,
            CompletedAt = now
        },
        CancellationToken.None);
    await todoRepository.SaveAsync(
        pendingTodo with
        {
            Id = 0,
            Title = "删除的已办理记录二",
            Status = TodoStatus.Done,
            CompletedAt = now
        },
        CancellationToken.None);
    await todoRepository.SaveAsync(
        pendingTodo with
        {
            Id = 0,
            Title = "删除的已忽略记录",
            Status = TodoStatus.Ignored,
            UpdatedAt = now.AddSeconds(1)
        },
        CancellationToken.None);

    var historyBeforeDelete = await todoRepository.GetCompletedAsync(CancellationToken.None);
    var deletedCount = await todoRepository.DeleteCompletedAsync(CancellationToken.None);
    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);
    var completedTodos = await todoRepository.GetCompletedAsync(CancellationToken.None);

    AssertEqual(3, historyBeforeDelete.Count, "Done and Ignored todos should both remain visible in history.");
    AssertTrue(historyBeforeDelete.Any(item => item.Status == TodoStatus.Ignored), "An ignored Todo must not disappear from every list.");
    AssertEqual(3, deletedCount, "Clearing handled todos should delete every Done and Ignored record.");
    AssertEqual(1, pendingTodos.Count, "Clearing completed todos should preserve pending records.");
    AssertEqual("保留的待办理记录", pendingTodos.Single().Title, "The pending record should be unchanged.");
    AssertEqual(0, completedTodos.Count, "No completed records should remain after clearing.");
    AssertEqual(0, await todoRepository.DeleteCompletedAsync(CancellationToken.None), "Clearing an empty completed list should delete nothing.");
}

static async Task TestTodoOrderByMessageTimeAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var now = DateTimeOffset.UtcNow;

    var olderMessage = CreateMessage(
        id: 0,
        chatName: "较早消息群",
        senderName: "发送人A",
        content: "较早发送的消息") with
    {
        SentAt = now.AddHours(-2),
        CapturedAt = now
    };
    var newerMessage = CreateMessage(
        id: 0,
        chatName: "较新消息群",
        senderName: "发送人B",
        content: "较晚发送的消息") with
    {
        SentAt = now.AddHours(-1),
        CapturedAt = now
    };

    var savedOlderMessage = await messageRepository.SaveAsync(olderMessage, CancellationToken.None);
    var savedNewerMessage = await messageRepository.SaveAsync(newerMessage, CancellationToken.None);

    var olderTodo = new TodoItem(
        Id: 0,
        SourceMessageId: savedOlderMessage.Id,
        ProjectId: null,
        Title: olderMessage.Content,
        Description: null,
        Status: TodoStatus.Pending,
        Priority: PriorityLevel.P0,
        DueAt: null,
        CreatedAt: now,
        UpdatedAt: now,
        CompletedAt: null,
        IsAutoCreated: true);
    var newerTodo = olderTodo with
    {
        SourceMessageId = savedNewerMessage.Id,
        Title = newerMessage.Content,
        Priority = PriorityLevel.P3,
        CreatedAt = now.AddDays(-1),
        UpdatedAt = now.AddDays(-1)
    };

    await todoRepository.SaveAsync(olderTodo, CancellationToken.None);
    await todoRepository.SaveAsync(newerTodo, CancellationToken.None);

    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);

    AssertEqual(2, pendingTodos.Count, "Both pending todos should be returned.");
    AssertEqual(savedNewerMessage.Id, pendingTodos[0].SourceMessageId, "Newer source message should sort first.");
    AssertEqual(savedOlderMessage.Id, pendingTodos[1].SourceMessageId, "Older source message should sort second.");
}

static Task TestCaptureAdapterFactoryAsync()
{
    var captureRoot = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    var definitions = CaptureAdapterFactory.CreateDefaultJsonlSources(captureRoot);

    AssertEqual(4, definitions.Count, "Default capture sources should cover four collaboration apps.");
    AssertTrue(definitions.Any(source => source.Source == "WeChat"), "WeChat should be a default source.");
    AssertTrue(definitions.Any(source => source.Source == "Feishu"), "Feishu should be a default source.");
    AssertTrue(definitions.Any(source => source.Source == "Shihuatong"), "Shihuatong should be a default source.");
    AssertTrue(definitions.Any(source => source.Source == "DingTalk"), "DingTalk should be a default source.");
    AssertTrue(definitions.All(source => source.Kind == CaptureSourceKind.JsonlDirectory), "Initial defaults should use JSONL directory capture.");

    var adapters = CaptureAdapterFactory.CreateAdapters(definitions).ToArray();
    AssertEqual(4, adapters.Length, "Enabled sources should create adapters.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "WeChat.JsonlDirectory"), "WeChat adapter should be created.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "Feishu.JsonlDirectory"), "Feishu adapter should be created.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "Shihuatong.JsonlDirectory"), "Shihuatong adapter should be created.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "DingTalk.JsonlDirectory"), "DingTalk adapter should be created.");

    var disabledFeishu = definitions
        .Select(source => source.Source == "Feishu" ? source with { IsEnabled = false } : source)
        .ToArray();
    var enabledAdapters = CaptureAdapterFactory.CreateAdapters(disabledFeishu).ToArray();
    AssertEqual(3, enabledAdapters.Length, "Disabled sources should not create adapters.");
    AssertFalse(enabledAdapters.Any(adapter => adapter.Name == "Feishu.JsonlDirectory"), "Disabled Feishu source should be skipped.");

    var wechatWindowSource = CaptureAdapterFactory.CreateWeChatWindowTextSource();
    AssertEqual(CaptureSourceKind.WindowText, wechatWindowSource.Kind, "WeChat visible-window source should use window text capture.");
    AssertFalse(wechatWindowSource.IsEnabled, "WeChat visible-window source should be opt-in because screen OCR can capture overlapping windows.");
    var enabledWechatWindowSource = wechatWindowSource with { IsEnabled = true };
    var windowAdapters = CaptureAdapterFactory.CreateAdapters(
        new[] { enabledWechatWindowSource },
        new StaticWindowTextSnapshotProvider(Array.Empty<WindowTextSnapshot>()));
    AssertEqual(1, windowAdapters.Count, "Enabled window text source with provider should create one adapter.");
    AssertEqual("WeChat.WindowText", windowAdapters[0].Name, "Enabled WeChat window source should create a window text adapter.");

    return Task.CompletedTask;
}

static Task TestLiveCaptureAdapterFactoryAsync()
{
    var captureRoot = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    var definitions = CaptureAdapterFactory.CreateDefaultLiveSources(captureRoot);

    AssertTrue(definitions.Any(source => source.Source == "WeChat" && source.Kind == CaptureSourceKind.JsonlDirectory), "Live capture should keep WeChat JSONL import.");
    AssertTrue(definitions.Any(source => source.Source == "Feishu" && source.Kind == CaptureSourceKind.JsonlDirectory), "Live capture should keep future Feishu JSONL import.");
    AssertTrue(definitions.Any(source => source.Source == "WeChat" && source.Kind == CaptureSourceKind.WeChatLocalExport && source.IsEnabled), "Live capture should enable WeChat local export source.");
    AssertFalse(definitions.Any(source => source.Source == "WeChat" && source.Kind == CaptureSourceKind.WindowText), "Live capture must not include the visible-window OCR source.");
    AssertTrue(definitions.Any(source => source.Source == "Shihuatong" && source.Kind == CaptureSourceKind.ShihuatongLocalDatabase && source.IsEnabled), "Live capture should enable Shihuatong local database source.");

    var adapters = CaptureAdapterFactory.CreateAdapters(
        definitions,
        new StaticWindowTextSnapshotProvider(Array.Empty<WindowTextSnapshot>()));

    AssertTrue(adapters.Any(adapter => adapter.Name == "WeChat.LocalExport"), "Live adapters should include WeChat local export adapter.");
    AssertFalse(adapters.Any(adapter => adapter.Name == "WeChat.WindowText"), "Live adapters must not include WeChat visible-window capture.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "WeChat.JsonlDirectory"), "Live adapters should still include WeChat JSONL adapter.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "DingTalk.JsonlDirectory"), "Live adapters should preserve extensibility for other sources.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "Shihuatong.LocalDatabase"), "Live adapters should include Shihuatong local database adapter.");

    var nativeReader = new FakeWeChatLocalDatabaseReader();
    nativeReader.InitializeAsync(
        new WeChatInitializationRequest(@"D:\fixture\db_storage", 1234, TimeSpan.FromSeconds(1)),
        CancellationToken.None).GetAwaiter().GetResult();
    var nativeService = new WeChatLocalReaderService(
        nativeReader,
        databaseRootLocator: () => @"D:\fixture\db_storage",
        processIdLocator: () => 1234);
    var nativeSource = CaptureAdapterFactory.CreateWeChatLocalDatabaseSource(nativeService);
    AssertEqual(CaptureSourceKind.WeChatLocalDatabase, nativeSource.Kind, "Native source should use the C# database kind.");
    var nativeAdapters = CaptureAdapterFactory.CreateAdapters(
        new[] { nativeSource },
        windowTextSnapshotProvider: null,
        weChatLocalDatabaseReader: nativeReader);
    AssertEqual(1, nativeAdapters.Count, "An initialized shared reader should create one native adapter.");
    AssertTrue(nativeAdapters.Single() is WeChatLocalDatabaseCaptureAdapter, "Factory must register the native C# database adapter.");

    return Task.CompletedTask;
}

static async Task TestTodoGroupMigrationAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var repository = new SqliteTodoRepository(databasePath);
    var now = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.FromHours(8));
    var saved = await repository.SaveAsync(
        new TodoItem(
            0,
            null,
            null,
            "迁移前待办",
            "必须原样保留",
            TodoStatus.Pending,
            PriorityLevel.P1,
            now.AddDays(1),
            now,
            now,
            null,
            false),
        CancellationToken.None);

    await initializer.InitializeAsync(CancellationToken.None);
    await initializer.InitializeAsync(CancellationToken.None);

    await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    await connection.OpenAsync();
    await using var tableCommand = connection.CreateCommand();
    tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='todo_groups';";
    AssertEqual(1L, (long)(await tableCommand.ExecuteScalarAsync() ?? 0L), "Migration should create todo_groups exactly once.");

    await using var columnCommand = connection.CreateCommand();
    columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('todo_items') WHERE name='group_id';";
    AssertEqual(1L, (long)(await columnCommand.ExecuteScalarAsync() ?? 0L), "Migration should add nullable group_id exactly once.");

    await using var migrationCommand = connection.CreateCommand();
    migrationCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=3 AND name='todo-groups';";
    AssertEqual(1L, (long)(await migrationCommand.ExecuteScalarAsync() ?? 0L), "Migration v3 should be recorded exactly once.");

    await using var colorColumnCommand = connection.CreateCommand();
    colorColumnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('todo_groups') WHERE name='color' AND \"notnull\"=1;";
    AssertEqual(1L, (long)(await colorColumnCommand.ExecuteScalarAsync() ?? 0L), "Migration should add one required color column to todo_groups.");

    await using var colorMigrationCommand = connection.CreateCommand();
    colorMigrationCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=4 AND name='todo-group-colors';";
    AssertEqual(1L, (long)(await colorMigrationCommand.ExecuteScalarAsync() ?? 0L), "Migration v4 should be recorded exactly once.");

    var loaded = await repository.GetByIdAsync(saved.Id, CancellationToken.None);
    AssertNotNull(loaded, "Existing Todo must remain after migration.");
    AssertEqual("迁移前待办", loaded!.Title, "Migration must preserve title.");
    AssertEqual("必须原样保留", loaded.Description, "Migration must preserve description.");
    AssertEqual(PriorityLevel.P1, loaded.Priority, "Migration must preserve priority.");
    AssertEqual(now.AddDays(1), loaded.DueAt, "Migration must preserve due time.");
}

static async Task TestTodoGroupMigrationRollbackAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString()))
    {
        await connection.OpenAsync();
        await using var conflict = connection.CreateCommand();
        conflict.CommandText = "CREATE TABLE todo_groups (id INTEGER PRIMARY KEY);";
        await conflict.ExecuteNonQueryAsync();
    }

    var failed = false;
    try
    {
        await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    }
    catch (SqliteException)
    {
        failed = true;
    }

    AssertTrue(failed, "A conflicting v3 table should stop initialization.");
    await using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString());
    await verification.OpenAsync();

    await using var columnCommand = verification.CreateCommand();
    columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('todo_items') WHERE name='group_id';";
    AssertEqual(0L, (long)(await columnCommand.ExecuteScalarAsync() ?? 0L), "Failed v3 must roll back group_id creation.");

    await using var indexCommand = verification.CreateCommand();
    indexCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_todo_items_group_id';";
    AssertEqual(0L, (long)(await indexCommand.ExecuteScalarAsync() ?? 0L), "Failed v3 must not leave its index.");

    await using var migrationCommand = verification.CreateCommand();
    migrationCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version=3;";
    AssertEqual(0L, (long)(await migrationCommand.ExecuteScalarAsync() ?? 0L), "Failed v3 must not be recorded as applied.");
}

static async Task TestTodoProjectAssignmentMigrationAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var projectRepository = new SqliteFollowedProjectRepository(databasePath);
    var idCollisionProject = await projectRepository.SaveAsync("其他项目", ProjectColorPolicy.Blue, CancellationToken.None);
    var matchedProject = await projectRepository.SaveAsync("工作", ProjectColorPolicy.Red, CancellationToken.None);
    var groupService = new TodoGroupApplicationService(new SqliteTodoGroupRepository(databasePath), clock);
    var matchedGroup = (await groupService.CreateAsync("工作", CancellationToken.None)).Group;
    var unmatchedGroup = (await groupService.CreateAsync("不匹配分组", CancellationToken.None)).Group;
    AssertEqual(idCollisionProject.Id, matchedGroup.Id, "Fixture should force a numeric ID collision between different project/group records.");
    AssertTrue(matchedProject.Id != matchedGroup.Id, "The same-name project/group fixture should use different numeric IDs so mapping cannot pass accidentally by ID.");
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var matchedTodo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("同名分组待办", null, PriorityLevel.P2, null, null, matchedGroup.Id),
        CancellationToken.None)).Todo!;
    var unmatchedTodo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("仅 ID 可能碰撞", null, PriorityLevel.P2, null, null, unmatchedGroup.Id),
        CancellationToken.None)).Todo!;

    await using (var prepare = new SqliteConnection($"Data Source={databasePath}"))
    {
        await prepare.OpenAsync(CancellationToken.None);
        await using var command = prepare.CreateCommand();
        command.CommandText = "DELETE FROM schema_migration_reports WHERE version = 5; DELETE FROM schema_migrations WHERE version = 5;";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    await initializer.InitializeAsync(CancellationToken.None);
    await initializer.InitializeAsync(CancellationToken.None);

    var todoRepository = new SqliteTodoRepository(databasePath);
    var mapped = await todoRepository.GetByIdAsync(matchedTodo.Id, CancellationToken.None);
    var unmatched = await todoRepository.GetByIdAsync(unmatchedTodo.Id, CancellationToken.None);
    AssertEqual(matchedProject.Id, mapped!.ManagedProjectId, "A group maps only to the unique followed project with the same normalized name.");
    AssertEqual<long?>(null, unmatched!.ManagedProjectId, "A different group name must stay unassigned even when numeric IDs can collide.");
    AssertEqual(matchedGroup.Id, mapped.GroupId, "Migration must retain the historical group id as read-only legacy data.");
    AssertEqual(unmatchedGroup.Id, unmatched.GroupId, "Unmatched historical group data must remain intact.");

    await using (var verification = new SqliteConnection($"Data Source={databasePath}"))
    {
        await verification.OpenAsync(CancellationToken.None);
        await using var report = verification.CreateCommand();
        report.CommandText = "SELECT mapped_group_todos, unmatched_group_todos FROM schema_migration_reports WHERE version = 5;";
        await using var reader = await report.ExecuteReaderAsync(CancellationToken.None);
        AssertTrue(await reader.ReadAsync(CancellationToken.None), "Project migration should record an auditable report.");
        AssertEqual(1, reader.GetInt32(0), "Migration report should count the one same-name group mapping.");
        AssertEqual(1, reader.GetInt32(1), "Migration report should count the unmatched historical group Todo.");
    }

    AssertEqual(ProjectColorPolicy.Red, (await projectRepository.GetAllAsync(CancellationToken.None)).Single(item => item.Id == matchedProject.Id).Color, "Project color must survive migration.");
    await using (var injectFailure = new SqliteConnection($"Data Source={databasePath}"))
    {
        await injectFailure.OpenAsync(CancellationToken.None);
        await using var command = injectFailure.CreateCommand();
        command.CommandText = $"""
            DELETE FROM schema_migration_reports WHERE version = 5;
            DELETE FROM schema_migrations WHERE version = 5;
            UPDATE todo_items SET managed_project_id = NULL WHERE id = {matchedTodo.Id};
            CREATE TRIGGER fail_project_migration_report
            BEFORE INSERT ON schema_migration_reports
            WHEN NEW.version = 5
            BEGIN
                SELECT RAISE(ABORT, 'injected v5 failure');
            END;
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    var migrationFailed = false;
    try
    {
        await initializer.InitializeAsync(CancellationToken.None);
    }
    catch (SqliteException)
    {
        migrationFailed = true;
    }
    AssertTrue(migrationFailed, "An injected v5 failure should stop initialization.");
    AssertEqual<long?>(null, (await todoRepository.GetByIdAsync(matchedTodo.Id, CancellationToken.None))!.ManagedProjectId, "A failed v5 transaction must roll back provisional Todo mappings.");

    await using (var recover = new SqliteConnection($"Data Source={databasePath}"))
    {
        await recover.OpenAsync(CancellationToken.None);
        await using var command = recover.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_project_migration_report;";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
    await initializer.InitializeAsync(CancellationToken.None);
    AssertEqual(matchedProject.Id, (await todoRepository.GetByIdAsync(matchedTodo.Id, CancellationToken.None))!.ManagedProjectId, "Migration should succeed after the injected failure is removed.");

    await projectRepository.DeleteAsync(matchedProject.Id, CancellationToken.None);
    AssertEqual<long?>(null, (await todoRepository.GetByIdAsync(matchedTodo.Id, CancellationToken.None))!.ManagedProjectId, "Deleting a project should clear the Todo relation through ON DELETE SET NULL.");
    AssertTrue(idCollisionProject.Id > 0, "The numeric-collision fixture project should be persisted.");
}

static async Task TestTodoGroupsAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 8, 23, 9, 15, 0, TimeSpan.FromHours(8));
    var repository = new SqliteTodoGroupRepository(databasePath);
    var service = new TodoGroupApplicationService(repository, new FixedClock(now));
    var created = await service.CreateAsync("  工作  ", TodoGroupColorPolicy.Red, CancellationToken.None);
    var duplicate = await service.CreateAsync("工作", CancellationToken.None);
    var latin = await service.CreateAsync("Alpha", CancellationToken.None);
    var latinDuplicate = await service.CreateAsync("alpha", CancellationToken.None);
    var unicode = await service.CreateAsync("Cafe\u0301", CancellationToken.None);
    var unicodeDuplicate = await service.CreateAsync("Café", CancellationToken.None);
    var groups = await service.GetAllAsync(CancellationToken.None);

    AssertEqual(CreateTodoGroupOutcome.Created, created.Outcome, "First normalized group should be created.");
    AssertEqual("工作", created.Group.Name, "Display name should be trimmed.");
    AssertEqual(TodoGroupColorPolicy.Red, created.Group.Color, "Selected group color should be persisted.");
    AssertEqual(CreateTodoGroupOutcome.Existing, duplicate.Outcome, "Identical normalized group should return existing.");
    AssertEqual(created.Group.Id, duplicate.Group.Id, "Duplicate should return the persisted group.");
    AssertEqual(TodoGroupColorPolicy.Red, duplicate.Group.Color, "Creating a duplicate must retain the existing group's color.");
    AssertEqual(CreateTodoGroupOutcome.Existing, latinDuplicate.Outcome, "Case-only duplicate should return existing.");
    AssertEqual(latin.Group.Id, latinDuplicate.Group.Id, "Case-only duplicate should keep one row.");
    AssertEqual(CreateTodoGroupOutcome.Existing, unicodeDuplicate.Outcome, "Unicode-equivalent duplicate should return existing.");
    AssertEqual(unicode.Group.Id, unicodeDuplicate.Group.Id, "Unicode-equivalent names should keep one row.");
    AssertEqual(3, groups.Count, "Only three normalized groups should be stored.");
    AssertEqual("Alpha,Café,工作", string.Join(",", groups.Select(item => item.Name)), "Groups should reload in stable name order.");

    var rejectedBlank = false;
    try
    {
        await service.CreateAsync("   ", CancellationToken.None);
    }
    catch (ArgumentException)
    {
        rejectedBlank = true;
    }

    AssertTrue(rejectedBlank, "Blank group names must be rejected before persistence.");

    var rejectedUnsupportedColor = false;
    try
    {
        await service.CreateAsync("不可读颜色", "#FFFFFF", CancellationToken.None);
    }
    catch (ArgumentException)
    {
        rejectedUnsupportedColor = true;
    }

    AssertTrue(rejectedUnsupportedColor, "Colors outside the readable preset palette must be rejected.");

    var concurrentName = $"并发-{Guid.NewGuid():N}";
    var concurrentResults = await Task.WhenAll(
        service.CreateAsync(concurrentName, CancellationToken.None),
        service.CreateAsync(concurrentName, CancellationToken.None));
    AssertEqual(1, concurrentResults.Count(item => item.Outcome == CreateTodoGroupOutcome.Created), "Concurrent normalized group creation should create one row.");
    AssertEqual(1, concurrentResults.Count(item => item.Outcome == CreateTodoGroupOutcome.Existing), "Concurrent duplicate should return an existing business result.");
    AssertEqual(concurrentResults[0].Group.Id, concurrentResults[1].Group.Id, "Concurrent duplicate results should resolve to the same group.");

    var unused = (await service.CreateAsync("临时分组", CancellationToken.None)).Group;
    AssertEqual(DeleteTodoGroupOutcome.Deleted, await service.DeleteAsync(unused.Id, CancellationToken.None), "An unused configured group should be deletable.");
    AssertEqual(DeleteTodoGroupOutcome.Missing, await service.DeleteAsync(unused.Id, CancellationToken.None), "Deleting a missing group should be an explicit business result.");
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    await todoService.CreateManualAsync(
        new CreateManualTodoRequest("引用设置分组", null, PriorityLevel.P2, null, null, created.Group.Id),
        CancellationToken.None);
    AssertEqual(DeleteTodoGroupOutcome.InUse, await service.DeleteAsync(created.Group.Id, CancellationToken.None), "A configured group referenced by a Todo must not be deleted.");
}

static async Task TestTodoDetailProjectOptionsAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var projectRepository = new SqliteFollowedProjectRepository(databasePath);
    var work = await projectRepository.SaveAsync("工作", ProjectColorPolicy.Blue, CancellationToken.None);
    await projectRepository.SaveAsync("个人", ProjectColorPolicy.Green, CancellationToken.None);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var todo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("核对设置项目", null, PriorityLevel.P2, null, null, GroupId: null, ManagedProjectId: work.Id),
        CancellationToken.None)).Todo!;
    var row = new TodoListItemViewModel(
        todo,
        null,
        null,
        TodoDueBucket.NoDueDate,
        work.ProjectName,
        work.Color,
        () => { });
    var detail = new TodoDetailViewModel(
        row,
        todoService,
        new SqliteTodoRepository(databasePath),
        projectRepository,
        new ReminderApplicationService(new SqliteTodoUnitOfWork(databasePath), clock),
        new SqliteReminderRepository(databasePath),
        clock,
        () => Task.CompletedTask,
        _ => Task.CompletedTask,
        new FakeTodoDialogService());

    await detail.LoadProjectsAsync();
    var configured = await projectRepository.GetAllAsync(CancellationToken.None);
    AssertEqual(
        string.Join(",", configured.Select(group => group.Id)),
        string.Join(",", detail.ProjectOptions.Skip(1).Select(project => project.Id)),
        "Detail choices should contain exactly the projects configured in settings, plus the explicit unassigned option.");
    AssertEqual(work.Id, detail.SelectedProject.Id, "Detail should retain the Todo's configured project selection.");

    var added = await projectRepository.SaveAsync("学习", ProjectColorPolicy.Purple, CancellationToken.None);
    await detail.LoadProjectsAsync();
    AssertTrue(detail.ProjectOptions.Any(project => project.Id == added.Id), "A project added in settings should appear after the detail choices reload.");
}

static async Task TestTodoDetailPinToggleAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var todo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("详情置顶按钮", null, PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    var row = new TodoListItemViewModel(
        todo,
        null,
        null,
        TodoDueBucket.NoDueDate,
        "未指定项目",
        ProjectColorPolicy.DefaultColor,
        () => { });
    var refreshCount = 0;
    var dialogs = new FakeTodoDialogService();
    var detail = new TodoDetailViewModel(
        row,
        todoService,
        repository,
        new SqliteFollowedProjectRepository(databasePath),
        new ReminderApplicationService(new SqliteTodoUnitOfWork(databasePath), clock),
        new SqliteReminderRepository(databasePath),
        clock,
        () =>
        {
            refreshCount++;
            return Task.CompletedTask;
        },
        _ => Task.CompletedTask,
        dialogs);

    AssertTrue(!detail.IsPinned, "A new Todo detail should start unpinned.");
    AssertEqual("置顶", detail.PinButtonText, "An unpinned Todo should offer the pin action.");
    AssertTrue(detail.TogglePinCommand.CanExecute(null), "An active Todo should allow pinning from the detail.");

    await detail.TogglePinAsync();

    AssertTrue(detail.IsPinned, "Pinning from the detail should update the detail state.");
    AssertEqual("取消置顶", detail.PinButtonText, "A pinned Todo should offer the unpin action.");
    AssertTrue((await repository.GetByIdAsync(todo.Id, CancellationToken.None))!.IsPinned, "Detail pinning should persist.");
    AssertEqual(1, refreshCount, "Detail pinning should refresh the shared workbench once.");

    await detail.TogglePinAsync();

    AssertTrue(!detail.IsPinned, "Toggling again should clear the pinned state.");
    AssertFalse((await repository.GetByIdAsync(todo.Id, CancellationToken.None))!.IsPinned, "Unpinning from the detail should persist.");
    AssertEqual(2, refreshCount, "Unpinning should refresh the shared workbench once.");

    // 置顶会写 updated_at，详情里的后续操作不能因版本过期被并发检查拒绝。
    await detail.TogglePinAsync();
    await detail.CompleteAsync();

    AssertEqual(0, dialogs.Errors.Count, "Completing right after pinning must not be rejected as a stale edit.");
    AssertEqual(TodoStatus.Done, (await repository.GetByIdAsync(todo.Id, CancellationToken.None))!.Status, "Completing after pinning should persist Done status.");
    AssertTrue(!detail.IsPinned, "Completing a pinned Todo should clear its pinned state.");
    AssertTrue(!detail.TogglePinCommand.CanExecute(null), "A completed Todo must not be pinnable from the detail.");
}

static async Task TestTodoDetailWindowPinButtonRenderingAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var unpinned = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("未置顶详情", null, PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    var pinned = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("已置顶详情", null, PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    await repository.SetPinnedAsync(pinned.Id, true, now, CancellationToken.None);
    pinned = (await repository.GetByIdAsync(pinned.Id, CancellationToken.None))!;
    AssertTrue(pinned.IsPinned, "Test setup should persist the pinned Todo before rendering the detail.");

    static TodoDetailViewModel CreateDetail(TodoItem todo, SqliteTodoRepository todos, IClock detailClock, string database)
    {
        var row = new TodoListItemViewModel(
            todo,
            null,
            null,
            TodoDueBucket.NoDueDate,
            "未指定项目",
            ProjectColorPolicy.DefaultColor,
            () => { });
        return new TodoDetailViewModel(
            row,
            new TodoApplicationService(new SqliteTodoUnitOfWork(database), detailClock),
            todos,
            new SqliteFollowedProjectRepository(database),
            new ReminderApplicationService(new SqliteTodoUnitOfWork(database), detailClock),
            new SqliteReminderRepository(database),
            detailClock,
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            new FakeTodoDialogService());
    }

    Exception? renderFailure = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            var unpinnedWindow = new WechatDashboard.App.Views.Todos.TodoDetailWindow(
                CreateDetail(unpinned, repository, clock, databasePath));
            unpinnedWindow.Show();
            unpinnedWindow.UpdateLayout();
            var pinButton = (System.Windows.Controls.Button)unpinnedWindow.FindName("PinButton");
            var badge = (System.Windows.Controls.Border)unpinnedWindow.FindName("PinnedBadge");
            AssertEqual("置顶", pinButton.Content, "An unpinned Todo detail should render the pin action.");
            AssertTrue(pinButton.IsEnabled, "An active Todo detail should render an enabled pin button.");
            AssertEqual(System.Windows.Visibility.Collapsed, badge.Visibility, "An unpinned Todo should not show the pinned badge.");
            unpinnedWindow.Close();

            var pinnedWindow = new WechatDashboard.App.Views.Todos.TodoDetailWindow(
                CreateDetail(pinned, repository, clock, databasePath));
            pinnedWindow.Show();
            pinnedWindow.UpdateLayout();
            var pinnedButton = (System.Windows.Controls.Button)pinnedWindow.FindName("PinButton");
            var pinnedBadge = (System.Windows.Controls.Border)pinnedWindow.FindName("PinnedBadge");
            AssertEqual("取消置顶", pinnedButton.Content, "A pinned Todo detail should render the unpin action.");
            AssertEqual(System.Windows.Visibility.Visible, pinnedBadge.Visibility, "A pinned Todo should show the pinned badge.");
            pinnedWindow.Close();
        }
        catch (Exception ex)
        {
            renderFailure = ex;
        }
        finally
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();

    AssertTrue(renderThread.Join(TimeSpan.FromSeconds(15)), "Todo detail render thread should finish without hanging.");
    if (renderFailure is not null)
    {
        throw new InvalidOperationException("Todo detail should render its header pin button.", renderFailure);
    }
}

static async Task TestTodoDetailDescriptionAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var todo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("带说明的待办", "初始说明", PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    var row = new TodoListItemViewModel(
        todo,
        null,
        null,
        TodoDueBucket.NoDueDate,
        "未指定项目",
        ProjectColorPolicy.DefaultColor,
        () => { });
    var refreshCount = 0;
    var dialogs = new FakeTodoDialogService();
    var detail = new TodoDetailViewModel(
        row,
        todoService,
        repository,
        new SqliteFollowedProjectRepository(databasePath),
        new ReminderApplicationService(new SqliteTodoUnitOfWork(databasePath), clock),
        new SqliteReminderRepository(databasePath),
        clock,
        () =>
        {
            refreshCount++;
            return Task.CompletedTask;
        },
        _ => Task.CompletedTask,
        dialogs);

    AssertEqual("初始说明", detail.Description, "Opening the detail should load the persisted description for editing.");
    AssertTrue(detail.BuildCopyText().Contains("说明：初始说明", StringComparison.Ordinal), "Copying all info should include the description.");
    AssertTrue(detail.SaveDescriptionCommand.CanExecute(null), "The detail should expose an executable save-description command.");
    AssertTrue(!dialogs.Errors.Any(), "Loading a description should not raise a dialog error.");

    detail.Description = "  补充后的说明  ";
    await detail.SaveDescriptionAsync();

    AssertEqual("待办说明已保存。", detail.StatusText, "Saving only the description should report its own feedback message.");
    AssertFalse(dialogs.Errors.Any(), "Saving a valid description must not raise a dialog error.");
    AssertEqual(1, refreshCount, "Saving the description should refresh the shared workbench once.");
    AssertEqual("补充后的说明", detail.Description, "Saving should write the trimmed description back to the view model.");
    AssertEqual("补充后的说明", (await repository.GetByIdAsync(todo.Id, CancellationToken.None))!.Description, "The description should persist through the repository.");

    detail.Description = "   ";
    await detail.SaveDescriptionAsync();

    AssertEqual<string?>(null, detail.Description, "Clearing the editor should store no description rather than whitespace.");
    AssertEqual<string?>(null, (await repository.GetByIdAsync(todo.Id, CancellationToken.None))!.Description, "A whitespace-only description should persist as no description.");
    AssertTrue(detail.BuildCopyText().Contains("说明：无", StringComparison.Ordinal), "A missing description should be copied as an explicit empty value.");
}

static async Task TestTodoDetailDescriptionSectionRenderingAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var todo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("详情说明渲染", "渲染用的说明文本", PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;

    Exception? renderFailure = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            var row = new TodoListItemViewModel(
                todo,
                null,
                null,
                TodoDueBucket.NoDueDate,
                "未指定项目",
                ProjectColorPolicy.DefaultColor,
                () => { });
            var detail = new TodoDetailViewModel(
                row,
                new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock),
                repository,
                new SqliteFollowedProjectRepository(databasePath),
                new ReminderApplicationService(new SqliteTodoUnitOfWork(databasePath), clock),
                new SqliteReminderRepository(databasePath),
                clock,
                () => Task.CompletedTask,
                _ => Task.CompletedTask,
                new FakeTodoDialogService());
            var window = new WechatDashboard.App.Views.Todos.TodoDetailWindow(detail);
            window.Show();
            window.UpdateLayout();

            var descriptionBox = (System.Windows.Controls.TextBox)window.FindName("DescriptionTextBox");
            var originalBox = (System.Windows.Controls.TextBox)window.FindName("OriginalContentTextBox");
            var saveDescriptionButton = (System.Windows.Controls.Button)window.FindName("SaveDescriptionButton");

            AssertNotNull(descriptionBox, "The detail should render an editable description box.");
            AssertNotNull(originalBox, "The detail should keep rendering the original message box.");
            AssertNotNull(saveDescriptionButton, "The detail should render a save-description button.");
            AssertEqual("渲染用的说明文本", descriptionBox.Text, "The rendered description box should show the persisted description.");
            AssertFalse(descriptionBox.IsReadOnly, "The description box must stay editable.");
            AssertTrue(saveDescriptionButton.IsEnabled, "The save-description button should be enabled for an active Todo.");

            var descriptionTop = descriptionBox.TranslatePoint(new System.Windows.Point(0, 0), window).Y;
            var originalTop = originalBox.TranslatePoint(new System.Windows.Point(0, 0), window).Y;
            AssertTrue(
                descriptionTop < originalTop,
                $"The description editor should render above the original message (description Y={descriptionTop}, original Y={originalTop}).");

            window.Close();
        }
        catch (Exception ex)
        {
            renderFailure = ex;
        }
        finally
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();

    AssertTrue(renderThread.Join(TimeSpan.FromSeconds(15)), "Todo detail description render thread should finish without hanging.");
    if (renderFailure is not null)
    {
        throw new InvalidOperationException("Todo detail should render an editable description above the original message.", renderFailure);
    }
}

static async Task TestManualTodoCreationAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.FromHours(8));
    var groupService = new TodoGroupApplicationService(new SqliteTodoGroupRepository(databasePath), new FixedClock(now));
    var group = (await groupService.CreateAsync("本周工作", CancellationToken.None)).Group;
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    var reminderAt = now.AddHours(1);
    var result = await todoService.CreateManualAsync(
        new CreateManualTodoRequest(
            "  编写周报  ",
            "整理本周交付结果",
            null,
            now.AddDays(1),
            reminderAt,
            group.Id),
        CancellationToken.None);

    AssertEqual(CreateManualTodoOutcome.Created, result.Outcome, "Valid manual Todo should be created.");
    AssertNotNull(result.Todo, "Created result should include the Todo.");
    AssertEqual("编写周报", result.Todo!.Title, "Manual Todo title should be trimmed.");
    AssertEqual(PriorityLevel.P2, result.Todo.Priority, "Manual Todo should default to P2.");
    AssertEqual(TodoStatus.Pending, result.Todo.Status, "Manual Todo should start Pending.");
    AssertEqual<long?>(null, result.Todo.SourceMessageId, "Manual Todo must not fake a source message.");
    AssertFalse(result.Todo.IsAutoCreated, "Manual Todo must not be marked automatic.");
    AssertFalse(result.Todo.IsPinned, "Manual Todo should not be pinned by default.");
    AssertEqual(group.Id, result.Todo.GroupId, "Selected group should persist.");

    var reminders = await new SqliteReminderRepository(databasePath).GetForTodoAsync(result.Todo.Id, CancellationToken.None);
    AssertEqual(1, reminders.Count, "First reminder should be inserted with the Todo.");
    AssertEqual(reminderAt, reminders.Single().ScheduledAt, "Reminder time should round-trip.");

    var defaultResult = await todoService.CreateManualAsync(
        new CreateManualTodoRequest("仅标题", null, null, null, null, null),
        CancellationToken.None);
    AssertEqual(CreateManualTodoOutcome.Created, defaultResult.Outcome, "Title-only manual Todo should be valid.");
    AssertEqual(PriorityLevel.P2, defaultResult.Todo!.Priority, "Title-only manual Todo should default to P2.");
    AssertEqual(TodoStatus.Pending, defaultResult.Todo.Status, "Title-only manual Todo should start pending.");
    AssertEqual<long?>(null, defaultResult.Todo.GroupId, "Ungrouped should persist as a null relation.");
    AssertEqual<long?>(null, defaultResult.Todo.ProjectId, "Manual Todo should not invent a project.");
    AssertEqual<long?>(null, defaultResult.Todo.SourceMessageId, "Manual Todo should not invent a source message.");
    AssertEqual<DateTimeOffset?>(null, defaultResult.Todo.DueAt, "Omitted due time should remain null.");
    AssertFalse(defaultResult.Todo.IsAutoCreated, "Manual Todo should not be marked automatic.");
    AssertFalse(defaultResult.Todo.IsPinned, "Manual Todo should not be pinned by default.");
    AssertEqual(0, (await new SqliteReminderRepository(databasePath).GetForTodoAsync(defaultResult.Todo.Id, CancellationToken.None)).Count, "Omitted reminder should not create a reminder row.");

    // 有明确截止时间的手动消息待办，未显式设置提醒时也应按任务完成时间自动提醒。
    var messageRepository = new SqliteMessageRepository(databasePath);
    var autoReminderMessage = await messageRepository.SaveAsync(
        CreateMessage(0, "普通项目群", "李工", "请在明天提交材料") with
        {
            SourceMessageKey = $"manual-auto-reminder-{Guid.NewGuid():N}",
            IsMentionMe = false
        },
        CancellationToken.None);
    var autoReminderDueAt = now.AddHours(3);
    var autoReminderResult = await todoService.CreateFromMessageAsync(
        new CreateTodoFromMessageRequest(
            autoReminderMessage.Id,
            Title: null,
            Description: null,
            ProjectId: null,
            Priority: null,
            DueAt: autoReminderDueAt,
            FirstReminderAt: null),
        CancellationToken.None);
    var autoReminders = await new SqliteReminderRepository(databasePath)
        .GetForTodoAsync(autoReminderResult.Todo!.Id, CancellationToken.None);
    AssertEqual(1, autoReminders.Count, "A dated manual message Todo should create an automatic reminder.");
    AssertEqual(
        autoReminderDueAt - TimeSpan.FromHours(1),
        autoReminders.Single().ScheduledAt,
        "A manual task reminder should reserve one hour of effort before the deadline.");

    var missingGroup = await todoService.CreateManualAsync(
        new CreateManualTodoRequest("无效分组", null, PriorityLevel.P1, null, null, long.MaxValue),
        CancellationToken.None);
    AssertEqual(CreateManualTodoOutcome.GroupMissing, missingGroup.Outcome, "Missing group should be a recoverable outcome.");
    AssertEqual(3, (await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None)).Count, "Missing group must not leave a Todo.");

    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString()))
    {
        await connection.OpenAsync();
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER fail_manual_reminder
            BEFORE INSERT ON todo_reminders
            BEGIN
                SELECT RAISE(ABORT, 'fixture reminder failure');
            END;
            """;
        await trigger.ExecuteNonQueryAsync();
    }

    var reminderFailureRaised = false;
    try
    {
        await todoService.CreateManualAsync(
            new CreateManualTodoRequest("提醒失败回滚", null, PriorityLevel.P2, null, now.AddHours(2), group.Id),
            CancellationToken.None);
    }
    catch (SqliteException)
    {
        reminderFailureRaised = true;
    }

    AssertTrue(reminderFailureRaised, "Injected reminder failure should be observable to the application layer.");
    AssertEqual(3, (await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None)).Count, "Reminder failure must roll back the Todo insert.");
}

static async Task TestTodoGroupLifecycleAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.FromHours(8));
    var groupService = new TodoGroupApplicationService(new SqliteTodoGroupRepository(databasePath), new FixedClock(now));
    var firstGroup = (await groupService.CreateAsync("工作", CancellationToken.None)).Group;
    var secondGroup = (await groupService.CreateAsync("个人", CancellationToken.None)).Group;
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    var created = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("生命周期", null, PriorityLevel.P1, null, null, firstGroup.Id),
        CancellationToken.None)).Todo!;

    var moved = await todoService.UpdateAsync(
        new UpdateTodoRequest(
            created.Id,
            created.Title,
            created.Description,
            created.ProjectId,
            created.Priority,
            created.DueAt,
            TodoStatus.Pending,
            created.UpdatedAt,
            secondGroup.Id),
        CancellationToken.None);
    AssertNotNull(moved, "Group change should update the existing Todo.");
    AssertEqual(secondGroup.Id, moved!.GroupId, "Changed group should round-trip.");

    var cleared = await todoService.UpdateAsync(
        new UpdateTodoRequest(
            moved.Id,
            moved.Title,
            moved.Description,
            moved.ProjectId,
            moved.Priority,
            moved.DueAt,
            TodoStatus.Pending,
            moved.UpdatedAt,
            null),
        CancellationToken.None);
    AssertNotNull(cleared, "Group should be clearable without replacing the Todo.");
    AssertEqual<long?>(null, cleared!.GroupId, "Cleared group should persist as null.");

    var reassigned = await todoService.UpdateAsync(
        new UpdateTodoRequest(
            cleared.Id,
            cleared.Title,
            cleared.Description,
            cleared.ProjectId,
            cleared.Priority,
            cleared.DueAt,
            TodoStatus.Pending,
            cleared.UpdatedAt,
            secondGroup.Id),
        CancellationToken.None);
    AssertNotNull(reassigned, "Cleared group should be assignable again.");

    var missingGroupUpdate = await todoService.UpdateAsync(
        new UpdateTodoRequest(
            reassigned!.Id,
            reassigned.Title,
            reassigned.Description,
            reassigned.ProjectId,
            reassigned.Priority,
            reassigned.DueAt,
            TodoStatus.Pending,
            reassigned.UpdatedAt,
            long.MaxValue),
        CancellationToken.None);
    AssertEqual<TodoItem?>(null, missingGroupUpdate, "Missing group update should be recoverable rather than leaking a foreign-key exception.");
    var afterRejectedGroup = await new SqliteTodoRepository(databasePath).GetByIdAsync(reassigned.Id, CancellationToken.None);
    AssertEqual(secondGroup.Id, afterRejectedGroup!.GroupId, "Rejected group update must leave the persisted group unchanged.");

    var completed = await todoService.UpdateAsync(
        new UpdateTodoRequest(
            reassigned!.Id,
            reassigned.Title,
            reassigned.Description,
            reassigned.ProjectId,
            reassigned.Priority,
            reassigned.DueAt,
            TodoStatus.Done,
            reassigned.UpdatedAt,
            reassigned.GroupId),
        CancellationToken.None);
    AssertNotNull(completed, "Todo should complete through the existing lifecycle.");
    AssertEqual(secondGroup.Id, completed!.GroupId, "Completion must retain the group.");
    AssertEqual(TodoStatus.Done, completed.Status, "Completion status should persist.");
}

static async Task TestTodoCreateViewModelAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var projectRepository = new SqliteFollowedProjectRepository(databasePath);
    var project = await projectRepository.SaveAsync("工作", ProjectColorPolicy.Red, CancellationToken.None);
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var createdCount = 0;
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var dialogs = new FakeTodoDialogService();
    var viewModel = new TodoCreateViewModel(
        todoService,
        projectRepository,
        clock,
        dialogs,
        () =>
        {
            createdCount++;
            return Task.CompletedTask;
        });
    viewModel.RequestClose += (_, _) => closed.TrySetResult();
    await viewModel.LoadProjectsAsync();
    AssertEqual(ProjectColorPolicy.Red, viewModel.ProjectOptions.Single(item => item.Id == project.Id).Color, "Project options should expose the persisted color.");
    viewModel.Title = "   ";
    viewModel.SaveCommand.Execute(null);
    AssertEqual("标题不能为空。", viewModel.StatusText, "Whitespace-only title should keep the form open with a business error.");
    AssertEqual(0, (await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None)).Count, "Invalid title must not persist a Todo.");

    viewModel.Title = "  手动创建  ";
    viewModel.Description = "不依赖消息";
    viewModel.SelectedProject = viewModel.ProjectOptions.Single(item => item.Id == project.Id);
    viewModel.DueDate = new DateTime(2026, 8, 24);
    viewModel.DueHour = 13;
    viewModel.DueMinute = 0;
    viewModel.HasReminder = true;
    viewModel.ReminderDate = now.LocalDateTime.Date;
    viewModel.ReminderHour = now.Hour;
    viewModel.ReminderMinute = now.Minute;
    viewModel.SaveCommand.Execute(null);
    AssertEqual(1, dialogs.Errors.Count, "Reminder at the current time should be rejected.");

    viewModel.ReminderMinute = now.Minute + 5;

    viewModel.SaveCommand.Execute(null);
    viewModel.SaveCommand.Execute(null);
    await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var active = await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None);
    AssertEqual(1, active.Count, "Rapid duplicate submission should create only one Todo.");
    AssertEqual("手动创建", active.Single().Title, "Create view model should persist a trimmed title.");
    AssertEqual(project.Id, active.Single().ManagedProjectId, "Create view model should persist the selected project.");
    AssertEqual<long?>(null, active.Single().GroupId, "New project-based creation must not write the legacy group relation.");
    var expectedLocalDue = new DateTime(2026, 8, 24, 13, 0, 0, DateTimeKind.Unspecified);
    AssertEqual(new DateTimeOffset(expectedLocalDue, TimeZoneInfo.Local.GetUtcOffset(expectedLocalDue)), active.Single().DueAt, "Selected due date and time should use the local offset.");
    AssertEqual(1, (await new SqliteReminderRepository(databasePath).GetForTodoAsync(active.Single().Id, CancellationToken.None)).Count, "Valid future reminder should persist.");
    AssertEqual(1, createdCount, "Successful submission should notify the shell exactly once.");
}

static Task TestTodoRowPresentationAsync()
{
    var now = new DateTimeOffset(2026, 8, 23, 12, 30, 0, TimeSpan.FromHours(8));
    var manual = new TodoItem(
        1,
        null,
        null,
        "手动标题",
        "手动备注",
        TodoStatus.Pending,
        PriorityLevel.P2,
        null,
        now,
        now,
        null,
        false)
    {
        ManagedProjectId = 7
    };
    var manualRow = new TodoListItemViewModel(
        manual,
        null,
        null,
        TodoDueBucket.NoDueDate,
        "工作",
        ProjectColorPolicy.Red,
        () => { });
    AssertEqual("工作", manualRow.ProjectName, "Todo row should display the resolved project.");
    AssertEqual(ProjectColorPolicy.Red, manualRow.ProjectColor, "Todo row should expose the project's selected color.");
    AssertEqual("手动添加", manualRow.Source, "Source-less Todo should be labelled manual.");
    AssertEqual("无来源消息", manualRow.SourceChatName, "Manual Todo should not fake a chat.");
    AssertEqual("手动备注", manualRow.MessageContent, "Manual Todo should display its description.");

    var missingSourceRow = new TodoListItemViewModel(
        manual with { SourceMessageId = 999 },
        null,
        null,
        TodoDueBucket.NoDueDate,
        "工作",
        ProjectColorPolicy.Red,
        () => { });
    AssertEqual("来源消息丢失", missingSourceRow.Source, "Missing source must not be labelled manual.");
    AssertEqual("原始消息已不存在", missingSourceRow.SourceChatName, "Missing source should be explicit.");
    return Task.CompletedTask;
}

static async Task TestTodoWorkbenchSelectionRefreshAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var now = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var todos = new SqliteTodoRepository(databasePath);
    var messages = new SqliteMessageRepository(databasePath);
    var reminders = new SqliteReminderRepository(databasePath);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var todoService = new TodoApplicationService(unitOfWork, clock);
    var projectRepository = new SqliteFollowedProjectRepository(databasePath);
    var first = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("刷新时保留勾选一", null, PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    var second = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("刷新时保留勾选二", null, PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    var workbench = new TodoWorkbenchViewModel(
        todos,
        messages,
        reminders,
        todoService,
        new ReminderApplicationService(unitOfWork, clock),
        projectRepository,
        new TodoDueBucketPolicy(TimeZoneInfo.Local),
        clock,
        new FakeTodoDialogService(),
        _ => Task.CompletedTask,
        () => Task.CompletedTask);

    await workbench.RefreshAsync();
    workbench.SelectAllCommand.Execute(null);
    AssertEqual(2, workbench.SelectedCount, "Fixture should select both active rows.");

    await workbench.RefreshAsync();
    AssertEqual(2, workbench.SelectedCount, "A data refresh should preserve checked active rows by Todo id.");
    AssertTrue(workbench.NoDueDate.All(row => row.IsSelected), "Rebuilt row view models should restore their checked state.");

    await todos.MarkSelectedCompletedAsync(new[] { first.Id }, now.AddMinutes(1), CancellationToken.None);
    await workbench.RefreshAsync();
    AssertEqual(1, workbench.SelectedCount, "A checked Todo that is no longer active should drop out of the selection.");
    AssertEqual(second.Id, workbench.NoDueDate.Single(row => row.IsSelected).Id, "The remaining active Todo should stay checked.");
}

static async Task TestDesktopCalendarViewModelAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var todayActive = (await service.CreateManualAsync(
        new CreateManualTodoRequest("今天活动待办", null, PriorityLevel.P1, now.AddHours(5), null, null),
        CancellationToken.None)).Todo!;
    var todayCompleted = (await service.CreateManualAsync(
        new CreateManualTodoRequest("今天已办理待办", null, PriorityLevel.P0, now.AddHours(7), null, null),
        CancellationToken.None)).Todo!;
    var todayLateActive = (await service.CreateManualAsync(
        new CreateManualTodoRequest("今天更晚的高优先级待办", null, PriorityLevel.P0, now.AddHours(8), null, null),
        CancellationToken.None)).Todo!;
    var tomorrow = (await service.CreateManualAsync(
        new CreateManualTodoRequest("明天待办", null, PriorityLevel.P2, now.AddDays(1), null, null),
        CancellationToken.None)).Todo!;
    await service.CreateManualAsync(
        new CreateManualTodoRequest("未设置日期", null, PriorityLevel.P3, null, null, null),
        CancellationToken.None);
    await repository.MarkCompletedAsync(todayCompleted.Id, now.AddHours(1), CancellationToken.None);
    long openedTodoId = 0;
    var viewModel = new DesktopCalendarViewModel(
        repository,
        clock,
        timeZone,
        id =>
        {
            openedTodoId = id;
            return Task.CompletedTask;
        });

    await viewModel.RefreshAsync();

    AssertEqual(42, viewModel.Days.Count, "A desktop calendar month should always render six complete weeks.");
    AssertEqual(new DateTime(2026, 7, 27), viewModel.Days.First().Date, "The August 2026 grid should begin on the preceding Monday.");
    AssertEqual(3, viewModel.SelectedTodos.Count, "The selected day should show active and completed scheduled Todos.");
    AssertEqual(todayActive.Id, viewModel.SelectedTodos[0].Id,
        "同一完成状态内应按时间升序排列，先排当天最早的未完成待办。");
    AssertEqual(todayLateActive.Id, viewModel.SelectedTodos[1].Id,
        "优先级不再参与排序：时间更晚的高优先级待办应排在时间更早的低优先级待办之后。");
    AssertEqual(todayCompleted.Id, viewModel.SelectedTodos[2].Id,
        "已办待办应排在所有未完成待办之后，而不是按优先级插到最前面。");
    AssertTrue(viewModel.SelectedTodos[2].IsCompleted, "Completed daily Todo should remain visible with completed state.");
    AssertTrue(viewModel.StatusText.Contains("另有 1 条未设置截止日期"), "Calendar status should report unscheduled Todos honestly.");

    viewModel.SelectDate(new DateTime(2026, 8, 30));
    AssertEqual(1, viewModel.SelectedTodos.Count, "Selecting another date should replace the daily Todo list.");
    AssertEqual(tomorrow.Id, viewModel.SelectedTodos.Single().Id, "The selected date should show its scheduled Todo.");
    await viewModel.OpenTodoAsync(tomorrow.Id);
    AssertEqual(tomorrow.Id, openedTodoId, "Clicking a calendar Todo should delegate to the existing detail flow.");

    viewModel.MoveMonth(1);
    AssertEqual("2026年9月", viewModel.MonthTitle, "Month navigation should update the displayed month.");
}

static async Task TestTodoCompletionEntryPointsAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var reminders = new SqliteReminderRepository(databasePath);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var todoService = new TodoApplicationService(unitOfWork, clock);
    var reminderService = new ReminderApplicationService(unitOfWork, clock);
    var groupService = new TodoGroupApplicationService(new SqliteTodoGroupRepository(databasePath), clock);
    var calendarTodo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("日历勾选完成", null, PriorityLevel.P1, now.AddHours(2), null, null),
        CancellationToken.None)).Todo!;
    var workbenchRefreshCount = 0;
    var calendar = new DesktopCalendarViewModel(
        repository,
        clock,
        timeZone,
        _ => Task.CompletedTask,
        () =>
        {
            workbenchRefreshCount++;
            return Task.CompletedTask;
        });

    await calendar.RefreshAsync();
    AssertTrue(calendar.CompleteTodoCommand.CanExecute(calendar.SelectedTodos.Single()), "An active calendar Todo should expose an enabled completion checkbox command.");
    await calendar.CompleteTodoAsync(calendarTodo.Id);

    AssertEqual(1, workbenchRefreshCount, "Calendar completion should refresh the shared Todo workbench once.");
    AssertEqual(0, (await repository.GetActiveAsync(CancellationToken.None)).Count, "Checking a calendar Todo should remove it from the active list.");
    AssertEqual(TodoStatus.Done, (await repository.GetByIdAsync(calendarTodo.Id, CancellationToken.None))!.Status, "Calendar completion should persist Done status.");
    AssertTrue(calendar.SelectedTodos.Single(item => item.Id == calendarTodo.Id).IsCompleted, "The completed calendar Todo should remain visible as checked history.");
    AssertTrue(!calendar.CompleteTodoCommand.CanExecute(calendar.SelectedTodos.Single()), "A completed calendar Todo checkbox should not be actionable again.");

    var detailTodo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("详情按钮完成", "只完成，不隐式保存编辑", PriorityLevel.P2, null, null, null),
        CancellationToken.None)).Todo!;
    var row = new TodoListItemViewModel(
        detailTodo,
        null,
        null,
        TodoDueBucket.NoDueDate,
        "未指定项目",
        ProjectColorPolicy.DefaultColor,
        () => { });
    var detailRefreshCount = 0;
    var detail = new TodoDetailViewModel(
        row,
        todoService,
        repository,
        new SqliteFollowedProjectRepository(databasePath),
        reminderService,
        reminders,
        clock,
        () =>
        {
            detailRefreshCount++;
            return Task.CompletedTask;
        },
        _ => Task.CompletedTask,
        new FakeTodoDialogService());
    var requestedClose = false;
    detail.RequestClose += (_, _) => requestedClose = true;
    detail.Title = "尚未保存的标题修改";

    await detail.CompleteAsync();

    AssertEqual(1, detailRefreshCount, "Detail completion should refresh the shared Todo workbench once.");
    AssertTrue(requestedClose, "A successfully completed Todo detail should close after moving to completed history.");
    var persistedDetailTodo = (await repository.GetByIdAsync(detailTodo.Id, CancellationToken.None))!;
    AssertEqual(TodoStatus.Done, persistedDetailTodo.Status, "Detail completion should persist Done status.");
    AssertEqual(detailTodo.Title, persistedDetailTodo.Title, "Completing from detail should not silently persist unsaved field edits.");
    AssertTrue(!detail.CanComplete, "The detail completion button should become disabled after success.");
}

/// <summary>
/// 复刻用户操作路径的回归测试：在待办详情里点击"完成"后，
/// 这条待办必须仍然显示在日历组件里，只是复选框变为勾选状态（已办）。
/// </summary>
static async Task TestDetailCompletionKeepsTodoVisibleInCalendarAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(8));
    var clock = new FixedClock(now);
    var repository = new SqliteTodoRepository(databasePath);
    var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
    var todoService = new TodoApplicationService(unitOfWork, clock);
    var reminderService = new ReminderApplicationService(unitOfWork, clock);
    var completedTodo = (await todoService.CreateManualAsync(
        new CreateManualTodoRequest("详情完成必须保留在日历", null, PriorityLevel.P1, now.AddHours(3), null, null),
        CancellationToken.None)).Todo!;

    // 与 MainWindow 相同的接线：日历通过工作台打开详情，详情完成后日历自行刷新。
    var workbenchRefreshCount = 0;
    var calendar = new DesktopCalendarViewModel(
        repository,
        clock,
        timeZone,
        _ => Task.CompletedTask,
        () =>
        {
            workbenchRefreshCount++;
            return Task.CompletedTask;
        });

    await calendar.RefreshAsync();
    var beforeCompletion = calendar.SelectedTodos.Single(item => item.Id == completedTodo.Id);
    AssertTrue(!beforeCompletion.IsCompleted, "刚创建的日历待办不应显示为已完成。");
    var indexBeforeCompletion = calendar.SelectedTodos.IndexOf(beforeCompletion);

    // 走待办详情的完成路径（用户点击"完成"按钮）。
    var row = new TodoListItemViewModel(
        completedTodo,
        null,
        null,
        TodoDueBucket.DueToday,
        "未指定项目",
        ProjectColorPolicy.DefaultColor,
        () => { });
    var detail = new TodoDetailViewModel(
        row,
        todoService,
        repository,
        new SqliteFollowedProjectRepository(databasePath),
        reminderService,
        new SqliteReminderRepository(databasePath),
        clock,
        () =>
        {
            workbenchRefreshCount++;
            return Task.CompletedTask;
        },
        _ => Task.CompletedTask,
        new FakeTodoDialogService());
    await detail.CompleteAsync();

    AssertEqual(TodoStatus.Done, (await repository.GetByIdAsync(completedTodo.Id, CancellationToken.None))!.Status,
        "详情完成应把待办状态写入数据库。");

    // 详情关闭后日历会刷新（同 DesktopCalendarViewModel.OpenTodoAsync 的行为）。
    await calendar.RefreshAsync();
    var afterCompletion = calendar.SelectedTodos.SingleOrDefault(item => item.Id == completedTodo.Id);
    AssertTrue(afterCompletion is not null, "详情完成后的待办必须仍出现在日历当天列表中，不能消失。");
    AssertTrue(afterCompletion!.IsCompleted, "日历中的已完成待办应通过勾选复选框区分，而不是从日历移除。");
    AssertEqual(indexBeforeCompletion, calendar.SelectedTodos.IndexOf(afterCompletion),
        "同一天只有这一条待办时，完成前后位置不变；排序规则是先按完成状态、再按时间。");

    // 数据库中的截止时间必须保留——这是日历分组的依据。
    var persisted = (await repository.GetByIdAsync(completedTodo.Id, CancellationToken.None))!;
    AssertTrue(persisted.DueAt.HasValue, "完成待办不得丢失截止时间，否则日历无法按日期分组显示。");
}

static Task TestDesktopCalendarStateStoreAsync()
{
    var statePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}", "desktop-calendar.json");
    var store = new DesktopCalendarStateStore(statePath);
    var legacyState = new DesktopCalendarWindowState(
        Left: 120,
        Top: 80,
        Width: 1100,
        Height: 700,
        Opacity: 0.78,
        IsEdgeSnapEnabled: false,
        DockEdge: DesktopCalendarDockEdge.Right);

    store.Save(legacyState);
    var actual = store.Load();

    AssertEqual(legacyState, actual, "User-selected calendar dimensions should round-trip.");
    store.Save(legacyState with { IsDarkTheme = true });
    AssertTrue(store.Load().IsDarkTheme, "Dark theme preference must survive a reload.");
    store.Save(legacyState with { Width = -1, Height = 0 });
    AssertEqual(DesktopCalendarStateStore.MinimumWidth, store.Load().Width, "Invalid width must respect the minimum.");
    AssertEqual(DesktopCalendarStateStore.MinimumHeight, store.Load().Height, "Invalid height must respect the minimum.");
    AssertTrue(!actual.IsDesktopEmbedded, "Existing preferences should leave desktop embedding disabled.");
    store.Save(legacyState with { IsDesktopEmbedded = true });
    var embedded = store.Load();
    AssertTrue(embedded.IsDesktopEmbedded, "Desktop embedding should survive a preferences reload.");
    AssertEqual(DesktopCalendarDockEdge.None, embedded.DockEdge, "An embedded calendar must not restore edge retraction.");
    File.WriteAllText(statePath, "{\"Left\":120,\"Top\":80,\"DockEdge\":3}");
    AssertTrue(!store.Load().IsDarkTheme, "Older preference files must retain the light theme.");
    AssertTrue(!store.Load().IsDesktopEmbedded, "JSON saved by an older application must default to a floating calendar.");
    return Task.CompletedTask;
}

static Task TestAutoStartServiceAsync()
{
    var runKey = new InMemoryStartupRunKey();
    var service = new AutoStartService(runKey, () => @"C:\apps\WechatDashboard.App.exe");

    AssertTrue(!service.IsEnabled(), "Auto start should stay disabled before any Run entry exists.");

    var enabled = service.SetEnabled(true);
    AssertTrue(enabled.Success, "Enabling auto start should succeed for a known executable.");
    AssertTrue(enabled.IsEnabled, "A successful enable must report the enabled state.");
    AssertTrue(service.IsEnabled(), "The enabled state should be readable back from the Run entry.");

    var command = runKey.GetCommand(AutoStartService.ValueName);
    AssertNotNull(command, "Enabling auto start must write the Run entry.");
    AssertTrue(command!.Contains("WechatDashboard.App.exe", StringComparison.OrdinalIgnoreCase), "The Run entry must point at the application executable.");
    AssertTrue(command.StartsWith('"'), "The Run entry must quote the executable path so paths with spaces still run.");

    service.SetEnabled(true);
    AssertTrue(service.IsEnabled(), "Re-enabling auto start should stay idempotent.");

    var disabled = service.SetEnabled(false);
    AssertTrue(disabled.Success, "Disabling auto start should succeed.");
    AssertTrue(!disabled.IsEnabled, "A successful disable must report the disabled state.");
    AssertTrue(runKey.GetCommand(AutoStartService.ValueName) is null, "Disabling auto start must remove the Run entry.");
    AssertTrue(!service.IsEnabled(), "Auto start should stay disabled after the Run entry is removed.");

    var withoutPath = new AutoStartService(runKey, () => null).SetEnabled(true);
    AssertTrue(!withoutPath.Success, "Enabling auto start must fail when the executable path is unknown.");
    AssertTrue(!withoutPath.IsEnabled, "A failed enable must not report auto start as enabled.");
    AssertTrue(runKey.GetCommand(AutoStartService.ValueName) is null, "A failed enable must not write a Run entry.");

    var blankPath = new AutoStartService(runKey, () => "   ").SetEnabled(true);
    AssertTrue(!blankPath.Success, "Enabling auto start must fail for a blank executable path.");
    AssertTrue(runKey.GetCommand(AutoStartService.ValueName) is null, "A blank executable path must not write a Run entry.");

    runKey.SetCommand(AutoStartService.ValueName, "   ");
    AssertTrue(!service.IsEnabled(), "A blank Run entry must be treated as disabled.");
    return Task.CompletedTask;
}

static Task TestDesktopCalendarDockLayoutAsync()
{
    var area = new System.Windows.Rect(100, 50, 1920, 1040);
    var size = new System.Windows.Size(DesktopCalendarStateStore.DefaultWidth, DesktopCalendarStateStore.DefaultHeight);
    var current = new System.Windows.Point(400, 200);

    var leftDocked = DesktopCalendarDockLayout.GetDockedPosition(area, size, current, DesktopCalendarDockEdge.Left);
    var leftRetracted = DesktopCalendarDockLayout.GetRetractedPosition(area, size, current, DesktopCalendarDockEdge.Left);
    var topRetracted = DesktopCalendarDockLayout.GetRetractedPosition(area, size, current, DesktopCalendarDockEdge.Top);
    var rightRetracted = DesktopCalendarDockLayout.GetRetractedPosition(area, size, current, DesktopCalendarDockEdge.Right);
    var bottomRetracted = DesktopCalendarDockLayout.GetRetractedPosition(area, size, current, DesktopCalendarDockEdge.Bottom);

    AssertEqual(area.Left, leftDocked.X, "Left docking should align the full calendar with the work area.");
    AssertEqual(area.Left + DesktopCalendarDockLayout.RevealThickness, leftRetracted.X + size.Width, "Left retraction should leave only the reveal thickness on screen.");
    AssertEqual(area.Top + DesktopCalendarDockLayout.RevealThickness, topRetracted.Y + size.Height, "Top retraction should leave only the reveal thickness on screen.");
    AssertEqual(area.Right - DesktopCalendarDockLayout.RevealThickness, rightRetracted.X, "Right retraction should leave only the reveal thickness on screen.");
    AssertEqual(area.Bottom - DesktopCalendarDockLayout.RevealThickness, bottomRetracted.Y, "Bottom retraction should leave only the reveal thickness on screen.");
    AssertEqual(TimeSpan.FromSeconds(1), DesktopCalendarDockLayout.AutoHideDelay, "A docked calendar should wait one second before retracting.");
    return Task.CompletedTask;
}

static async Task TestDesktopCalendarWindowRenderingAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.FromHours(8));
    var repository = new SqliteTodoRepository(databasePath);
    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    await service.CreateManualAsync(
        new CreateManualTodoRequest("窗口布局待办", null, PriorityLevel.P1, now.AddHours(2), null, null),
        CancellationToken.None);
    var viewModel = new DesktopCalendarViewModel(repository, new FixedClock(now), timeZone, _ => Task.CompletedTask);
    await viewModel.RefreshAsync();
    var statePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}", "desktop-calendar.json");
    new DesktopCalendarStateStore(statePath).Save(new DesktopCalendarWindowState(
        Left: 100,
        Top: 100,
        DockEdge: DesktopCalendarDockEdge.Left));
    Exception? renderFailure = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            var window = new WechatDashboard.App.Views.Todos.DesktopCalendarWindow(
                viewModel,
                new DesktopCalendarStateStore(statePath));
            window.Show();
            window.UpdateLayout();
            var themeButton = (System.Windows.Controls.Button)window.FindName("ThemeButton");
            var calendarContent = (WechatDashboard.App.Views.Todos.DesktopCalendarView)window.FindName("CalendarContent");
            themeButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            AssertTrue(new DesktopCalendarStateStore(statePath).Load().IsDarkTheme, "Theme changes must save immediately.");
            AssertEqual("#FF111827", ((System.Windows.Media.SolidColorBrush)window.Background).Color.ToString(), "The native window background must switch to dark.");
            AssertEqual("#FF111827", ((System.Windows.Media.SolidColorBrush)calendarContent.FindResource("CalendarCanvas")).Color.ToString(), "Calendar content must switch with the toolbar.");
            themeButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            AssertEqual("#FFF8FAFC", ((System.Windows.Media.SolidColorBrush)window.Background).Color.ToString(), "Switching back must restore the light background.");
            var standaloneCalendar = new WechatDashboard.App.Views.Todos.DesktopCalendarView();
            AssertEqual("#FFF8FAFC", ((System.Windows.Media.SolidColorBrush)standaloneCalendar.FindResource("CalendarCanvas")).Color.ToString(), "The desktop theme must not leak into the main schedule view.");
            themeButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            DesktopCalendarNativeAssertions.AssertRoundedRegion(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            var initialWidth = window.ActualWidth;
            var initialHeight = window.ActualHeight;
            AssertEqual(DesktopCalendarStateStore.DefaultWidth, initialWidth, "Desktop calendar should use the fixed width.");
            AssertEqual(DesktopCalendarStateStore.DefaultHeight, initialHeight, "Desktop calendar should use the fixed height.");
            AssertEqual(System.Windows.ResizeMode.NoResize, window.ResizeMode, "Custom grip resizing should not enable native window chrome.");
            var resizeGrip = (System.Windows.Controls.Primitives.Thumb)window.FindName("ResizeGrip");
            resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
            resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-60, -40)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            window.UpdateLayout();
            AssertEqual(initialWidth - 60, window.ActualWidth, "Dragging the grip should change width.");
            AssertEqual(initialHeight - 40, window.ActualHeight, "Dragging the grip should change height.");
            resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(-60, -40, false)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
            AssertEqual(window.ActualWidth, new DesktopCalendarStateStore(statePath).Load().Width, "Drag completion must save width.");
            initialWidth = window.ActualWidth;
            initialHeight = window.ActualHeight;
            var retractMethod = typeof(WechatDashboard.App.Views.Todos.DesktopCalendarWindow).GetMethod(
                "RetractToEdge",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var revealMethod = typeof(WechatDashboard.App.Views.Todos.DesktopCalendarWindow).GetMethod(
                "RevealFromEdge",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var dockMethod = typeof(WechatDashboard.App.Views.Todos.DesktopCalendarWindow).GetMethod(
                "DockToNearestEdge",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            AssertTrue(retractMethod is not null && revealMethod is not null && dockMethod is not null, "Desktop calendar should expose its private edge transition methods to the runtime test.");

            // Emulate Windows Aero Snap changing the window to a half-size layout at
            // the edge. Application docking must restore the pre-drag dimensions.
            var preservedSize = new System.Windows.Size(initialWidth, initialHeight);
            window.Width = initialWidth / 2;
            window.Height = initialHeight / 2;
            window.UpdateLayout();
            dockMethod!.Invoke(window, new object?[] { true, preservedSize });
            window.UpdateLayout();
            AssertEqual(initialWidth, window.ActualWidth, "Docking should restore the width that existed before Windows edge snap.");
            AssertEqual(initialHeight, window.ActualHeight, "Docking should restore the height that existed before Windows edge snap.");

            retractMethod!.Invoke(window, null);
            AssertEqual(System.Windows.Visibility.Visible,
                ((System.Windows.Controls.Border)window.FindName("CalendarSurface")).Visibility,
                "The calendar must stay visible while sliding into the edge.");
            var animationFrame = new System.Windows.Threading.DispatcherFrame();
            var animationWait = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            animationWait.Tick += (_, _) => { animationWait.Stop(); animationFrame.Continue = false; };
            animationWait.Start();
            System.Windows.Threading.Dispatcher.PushFrame(animationFrame);
            window.UpdateLayout();
            DesktopCalendarNativeAssertions.AssertRetractedRegion(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            var calendarSurface = window.FindName("CalendarSurface") as System.Windows.Controls.Border;
            var revealHandle = window.FindName("EdgeRevealHandle") as System.Windows.Controls.Border;
            AssertTrue(calendarSurface is not null && revealHandle is not null, "Desktop calendar should render its surface and edge reveal handle.");
            AssertEqual(System.Windows.Visibility.Hidden, calendarSurface!.Visibility, "Retracting should hide the full calendar surface.");
            AssertEqual(System.Windows.Visibility.Visible, revealHandle!.Visibility, "Retracting should show a clickable edge handle.");
            AssertEqual(initialWidth, window.ActualWidth, "Retracting should not change the calendar width.");
            AssertEqual(initialHeight, window.ActualHeight, "Retracting should not change the calendar height.");
            revealMethod!.Invoke(window, null);
            window.UpdateLayout();
            AssertEqual(System.Windows.Visibility.Visible, calendarSurface.Visibility, "Click restoration should show the full calendar surface.");
            AssertEqual(System.Windows.Visibility.Collapsed, revealHandle.Visibility, "Click restoration should hide the edge handle.");
            AssertEqual(initialWidth, window.ActualWidth, "Revealing should preserve the docked calendar width.");
            AssertEqual(initialHeight, window.ActualHeight, "Revealing should preserve the docked calendar height.");
            AssertEqual(System.Windows.ResizeMode.NoResize, window.ResizeMode, "Revealing should keep custom grip resizing.");
            var embedOption = (System.Windows.Controls.CheckBox)window.FindName("DesktopEmbedCheckBox");
            AssertTrue(window.FindName("EdgeSnapCheckBox") is null, "Automatic snapping should not require a checkbox.");
            AssertTrue(window.FindName("DockButton") is null, "Edge docking should not require a toolbar button.");
            var nativeHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            AssertTrue(!window.AllowsTransparency, "Desktop calendars must not use WPF's invisible per-pixel layered child rendering path.");
            DesktopCalendarNativeAssertions.AssertRedirectedOpacity(nativeHandle, 0.9);
            var originalBounds = DesktopCalendarNativeAssertions.GetBounds(nativeHandle);
            embedOption.IsChecked = true;
            window.UpdateLayout();
            AssertTrue(embedOption.IsChecked == true,
                $"Desktop embedding should succeed: {((System.Windows.Controls.TextBlock)window.FindName("DockStatusText")).Text}");
            DesktopCalendarNativeAssertions.AssertEmbeddedAndImmovable(nativeHandle, originalBounds);
            DesktopCalendarNativeAssertions.AssertRoundedRegion(nativeHandle);
            DesktopCalendarNativeAssertions.AssertStaysBelowOtherWindows(nativeHandle);
            resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
            resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(-10000, -10000)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
            window.UpdateLayout();
            AssertEqual(DesktopCalendarStateStore.MinimumWidth, window.ActualWidth, "Embedded resizing must clamp width.");
            AssertEqual(DesktopCalendarStateStore.MinimumHeight, window.ActualHeight, "Embedded resizing must clamp height.");
            DesktopCalendarNativeAssertions.AssertRoundedRegion(nativeHandle);
            var resizedBounds = DesktopCalendarNativeAssertions.GetBounds(nativeHandle);
            AssertEqual(originalBounds.Left, resizedBounds.Left, "Embedded resizing must keep the left position locked.");
            AssertEqual(originalBounds.Top, resizedBounds.Top, "Embedded resizing must keep the top position locked.");
            AssertTrue(resizedBounds.Right < originalBounds.Right && resizedBounds.Bottom < originalBounds.Bottom,
                "Embedded resizing must change native window dimensions.");
            originalBounds = resizedBounds;
            resizeGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)
                { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
            var opacitySlider = (System.Windows.Controls.Slider)window.FindName("OpacitySlider");
            AssertEqual(0.9, opacitySlider.Value, "Entering desktop mode must preserve the chosen opacity.");
            DesktopCalendarNativeAssertions.AssertRedirectedOpacity(nativeHandle, 0.9);
            AssertEqual(0.9, new DesktopCalendarStateStore(statePath).Load().Opacity, "Embedding must save the preserved opacity.");
            opacitySlider.Value = 0.8;
            DesktopCalendarNativeAssertions.AssertRedirectedOpacity(nativeHandle, 0.8);
            dockMethod.Invoke(window, new object?[] { true, preservedSize });
            retractMethod.Invoke(window, null);
            AssertEqual(System.Windows.Visibility.Visible, calendarSurface.Visibility, "Embedded calendars must never retract.");
            AssertTrue(new DesktopCalendarStateStore(statePath).Load().IsDesktopEmbedded, "Checking embedding must save immediately.");
            embedOption.IsChecked = false;
            window.UpdateLayout();
            DesktopCalendarNativeAssertions.AssertDetached(nativeHandle, originalBounds);
            DesktopCalendarNativeAssertions.AssertRedirectedOpacity(nativeHandle, 0.8);
            AssertTrue(!new DesktopCalendarStateStore(statePath).Load().IsDesktopEmbedded, "Unchecking must save immediately.");
            ((System.Windows.Controls.Button)window.FindName("CloseButton")).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            AssertTrue(!window.IsVisible, "The close button must dismiss the calendar.");
            AssertEqual(0.8, new DesktopCalendarStateStore(statePath).Load().Opacity, "Closing must retain the selected opacity.");
            AssertTrue(new DesktopCalendarStateStore(statePath).Load().IsEdgeSnapEnabled, "Snapping must remain enabled when saving.");
            window.Show();
            AssertTrue(window.IsVisible, "The dismissed calendar must be reopenable.");
            embedOption.IsChecked = true;
            AssertEqual(0.8, opacitySlider.Value, "Re-entering desktop mode must preserve the chosen opacity.");
            opacitySlider.Value = 0.7;
            window.Shutdown();
            AssertTrue(new DesktopCalendarStateStore(statePath).Load().IsDesktopEmbedded, "Shutdown must retain embedding preference after detaching.");
            var restoredWindow = new WechatDashboard.App.Views.Todos.DesktopCalendarWindow(viewModel, new DesktopCalendarStateStore(statePath));
            restoredWindow.Show();
            restoredWindow.UpdateLayout();
            AssertEqual("#FF111827", ((System.Windows.Media.SolidColorBrush)restoredWindow.Background).Color.ToString(), "Reopening must restore the dark theme.");
            AssertEqual(DesktopCalendarStateStore.MinimumWidth, restoredWindow.ActualWidth, "Reopening must restore the resized width.");
            AssertEqual(DesktopCalendarStateStore.MinimumHeight, restoredWindow.ActualHeight, "Reopening must restore the resized height.");
            AssertTrue(((System.Windows.Controls.CheckBox)restoredWindow.FindName("DesktopEmbedCheckBox")).IsChecked == true,
                "Reopening must restore actual desktop embedding.");
            DesktopCalendarNativeAssertions.AssertRedirectedOpacity(new System.Windows.Interop.WindowInteropHelper(restoredWindow).Handle, 0.7);
            restoredWindow.Shutdown();
        }
        catch (Exception ex)
        {
            renderFailure = ex;
        }
        finally
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();

    AssertTrue(renderThread.Join(TimeSpan.FromSeconds(15)), "Desktop calendar render thread should finish without hanging.");
    if (renderFailure is not null)
    {
        throw new InvalidOperationException("Desktop calendar should construct and render its populated Todo templates.", renderFailure);
    }
}

static async Task TestDesktopCalendarScrollBarThemeAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.FromHours(8));
    var repository = new SqliteTodoRepository(databasePath);
    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    for (var index = 0; index < 8; index++)
    {
        await service.CreateManualAsync(
            new CreateManualTodoRequest($"当日日程滚动校验 {index}", null, PriorityLevel.P2, now.AddHours(1), null, null),
            CancellationToken.None);
    }

    var viewModel = new DesktopCalendarViewModel(repository, new FixedClock(now), timeZone, _ => Task.CompletedTask);
    await viewModel.RefreshAsync();
    AssertEqual(8, viewModel.SelectedTodos.Count, "The selected day must expose every created Todo before checking the overflow scrollbar.");

    var statePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}", "desktop-calendar.json");
    new DesktopCalendarStateStore(statePath).Save(new DesktopCalendarWindowState(Left: 100, Top: 100, IsDarkTheme: true));
    // 可选：设置 WECHAT_DASHBOARD_CAPTURE_DIR 时导出明暗两套日历效果图，便于人工核对滚动条观感。
    var captureDirectory = Environment.GetEnvironmentVariable("WECHAT_DASHBOARD_CAPTURE_DIR") ?? string.Empty;

    Exception? renderFailure = null;
    var renderThread = new Thread(() =>
    {
        try
        {
            var window = new WechatDashboard.App.Views.Todos.DesktopCalendarWindow(viewModel, new DesktopCalendarStateStore(statePath));
            window.Show();
            window.UpdateLayout();
            var themeButton = (System.Windows.Controls.Button)window.FindName("ThemeButton");
            var calendarContent = (WechatDashboard.App.Views.Todos.DesktopCalendarView)window.FindName("CalendarContent");
            var list = (System.Windows.Controls.ListBox)calendarContent.FindName("SelectedTodosList");
            AssertNotNull(list, "The day detail list must be addressable for scrollbar verification.");
            window.UpdateLayout();

            var verticalScrollBar = FindVisualDescendant<System.Windows.Controls.Primitives.ScrollBar>(
                list,
                candidate => candidate.Orientation == System.Windows.Controls.Orientation.Vertical);
            AssertNotNull(verticalScrollBar, "The overflowing day detail list must create a vertical scrollbar.");
            AssertEqual(System.Windows.Visibility.Visible, verticalScrollBar!.Visibility, "Eight day Todos must overflow the detail list.");
            AssertTrue(Math.Abs(verticalScrollBar.ActualWidth - 8) <= 0.5,
                $"The calendar scrollbar must use the slim themed width instead of the system default, got {verticalScrollBar.ActualWidth}.");
            AssertTrue(verticalScrollBar.ActualHeight > 100,
                $"The vertical scrollbar must keep the full list height, got {verticalScrollBar.ActualHeight}.");
            AssertTrue(verticalScrollBar.Template.FindName("PART_Track", verticalScrollBar) is System.Windows.Controls.Primitives.Track,
                "The calendar scrollbar must use the themed template with a track.");
            AssertEqual(0, CountVisualDescendants<System.Windows.Shapes.Path>(verticalScrollBar),
                "The themed scrollbar must not render the system arrow buttons.");
            AssertEqual(0.0, ((System.Windows.Media.SolidColorBrush)verticalScrollBar.Background).Color.A,
                "The themed scrollbar track must be transparent so the panel background shows through.");

            var darkThumb = (System.Windows.Media.SolidColorBrush)calendarContent.FindResource("CalendarScrollThumb");
            var windowContent = (System.Windows.FrameworkElement)window.Content;
            var darkSample = SampleScrollBarRegion(windowContent, verticalScrollBar, darkThumb.Color);
            AssertTrue(darkSample.OpaquePixels > 0,
                $"The dark scrollbar must render a visible thumb: {DescribeSample(darkSample)}");
            AssertEqual(0, darkSample.SystemChromePixels,
                "The dark theme must not paint the system light scrollbar chrome (RGB 240,240,240).");
            AssertTrue(darkSample.ThemePixels > 0,
                $"The dark scrollbar thumb must use CalendarScrollThumb, found {darkSample.ThemePixels} matching pixels.");
            AssertTrue(darkSample.MaxLuminance < 160,
                $"The dark theme must not draw a bright scrollbar band, measured luminance {darkSample.MaxLuminance:0.0}.");
            CaptureCalendarSnapshot(windowContent, Path.Combine(captureDirectory, "desktop-calendar-dark.png"));

            themeButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.UpdateLayout();
            var lightThumb = (System.Windows.Media.SolidColorBrush)calendarContent.FindResource("CalendarScrollThumb");
            AssertEqual("#FFC2CAD6", lightThumb.Color.ToString(), "Switching to the light theme must restore the light scrollbar thumb color.");
            var lightSample = SampleScrollBarRegion(windowContent, verticalScrollBar, lightThumb.Color);
            AssertTrue(lightSample.ThemePixels > 0,
                $"The light scrollbar thumb must use CalendarScrollThumb, found {lightSample.ThemePixels} matching pixels.");
            AssertEqual(0, lightSample.SystemChromePixels,
                "The light theme must not fall back to the system scrollbar chrome (RGB 240,240,240).");
            // 浅色主题轨道透明，会露出白色面板背景，因此用最暗像素判断滑块是否可辨识。
            AssertTrue(lightSample.MinLuminance <= 215,
                $"The light scrollbar thumb must stay discernible on the white panel, darkest measured luminance {lightSample.MinLuminance:0.0}.");
            CaptureCalendarSnapshot(windowContent, Path.Combine(captureDirectory, "desktop-calendar-light.png"));
            window.Shutdown();
        }
        catch (Exception ex)
        {
            renderFailure = ex;
        }
        finally
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();

    AssertTrue(renderThread.Join(TimeSpan.FromSeconds(15)), "Desktop calendar scrollbar render thread should finish without hanging.");
    if (renderFailure is not null)
    {
        throw new InvalidOperationException("The desktop calendar scrollbar must stay theme-consistent in both themes.", renderFailure);
    }
}

static void CaptureCalendarSnapshot(System.Windows.FrameworkElement element, string path)
{
    var directory = Path.GetDirectoryName(path);
    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
    {
        return;
    }

    const double scale = 1.5;
    var pixelWidth = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale));
    var pixelHeight = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale));
    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
        pixelWidth,
        pixelHeight,
        96 * scale,
        96 * scale,
        System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(element);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
    Directory.CreateDirectory(directory);
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static string DescribeSample(RenderedPixelSample sample) =>
    $"opaque={sample.OpaquePixels} anyAlpha={sample.AlphaPixels} theme={sample.ThemePixels} chrome={sample.SystemChromePixels} minLuma={sample.MinLuminance:0.0} maxLuma={sample.MaxLuminance:0.0} maxAlpha={sample.MaxAlpha}";

static T? FindVisualDescendant<T>(
    System.Windows.DependencyObject root,
    Func<T, bool>? predicate = null)
    where T : System.Windows.DependencyObject
{
    var queue = new Queue<System.Windows.DependencyObject>();
    queue.Enqueue(root);
    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(current, index);
            if (child is T match && (predicate is null || predicate(match)))
            {
                return match;
            }

            queue.Enqueue(child);
        }
    }

    return null;
}

static int CountVisualDescendants<T>(System.Windows.DependencyObject root)
    where T : System.Windows.DependencyObject
{
    var total = 0;
    var queue = new Queue<System.Windows.DependencyObject>();
    queue.Enqueue(root);
    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(current, index);
            if (child is T)
            {
                total++;
            }

            queue.Enqueue(child);
        }
    }

    return total;
}

static RenderedPixelSample SampleScrollBarRegion(
    System.Windows.FrameworkElement root,
    System.Windows.FrameworkElement scrollBar,
    System.Windows.Media.Color expectedColor)
{
    var origin = scrollBar.TransformToAncestor(root).Transform(new System.Windows.Point(0, 0));
    var region = new System.Windows.Rect(origin, new System.Windows.Size(scrollBar.ActualWidth, scrollBar.ActualHeight));
    return SampleRenderedRegion(root, region, expectedColor);
}

static RenderedPixelSample SampleRenderedRegion(
    System.Windows.FrameworkElement root,
    System.Windows.Rect region,
    System.Windows.Media.Color expectedColor)
{
    var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
    var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
        width,
        height,
        96,
        96,
        System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(root);
    var stride = bitmap.PixelWidth * 4;
    var pixels = new byte[stride * bitmap.PixelHeight];
    bitmap.CopyPixels(pixels, stride, 0);

    var opaque = 0;
    var alphaPixels = 0;
    var themePixels = 0;
    var systemChromePixels = 0;
    var maxLuminance = 0.0;
    var minLuminance = double.MaxValue;
    var maxAlpha = 0;
    var left = Math.Max(0, (int)Math.Floor(region.Left));
    var top = Math.Max(0, (int)Math.Floor(region.Top));
    var right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(region.Right));
    var bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(region.Bottom));
    for (var y = top; y < bottom; y++)
    {
        for (var x = left; x < right; x++)
        {
            var offset = (y * stride) + (x * 4);
            var alpha = pixels[offset + 3];
            if (alpha > 0)
            {
                alphaPixels++;
            }

            maxAlpha = Math.Max(maxAlpha, alpha);
            if (alpha < 200)
            {
                continue;
            }

            opaque++;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var luminance = (0.299 * red) + (0.587 * green) + (0.114 * blue);
            maxLuminance = Math.Max(maxLuminance, luminance);
            minLuminance = Math.Min(minLuminance, luminance);
            if (Math.Abs(red - expectedColor.R) <= 6 && Math.Abs(green - expectedColor.G) <= 6 && Math.Abs(blue - expectedColor.B) <= 6)
            {
                themePixels++;
            }

            // 系统默认滚动条滑块/轨道固定为 #F0F0F0，出现即说明自定义模板没有生效。
            if (Math.Abs(red - 240) <= 4 && Math.Abs(green - 240) <= 4 && Math.Abs(blue - 240) <= 4)
            {
                systemChromePixels++;
            }
        }
    }

    return new RenderedPixelSample(
        opaque,
        alphaPixels,
        themePixels,
        systemChromePixels,
        maxLuminance,
        opaque == 0 ? 0 : minLuminance,
        maxAlpha);
}

static async Task TestDesktopCalendarShowDesktopSurvivalAsync()
{
    // 在当前 STA 线程泵消息指定时长：Explorer 投递 SC_MINIMIZE/SC_RESTORE 依赖窗口消息循环，
    // 直接 Thread.Sleep 会让窗口收不到命令，导致「显示桌面」场景验证失效。
    void PumpMessages(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    var now = new DateTimeOffset(2026, 10, 13, 9, 30, 0, TimeSpan.FromHours(8));
    var repository = new SqliteTodoRepository(databasePath);
    var service = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), new FixedClock(now));
    await service.CreateManualAsync(
        new CreateManualTodoRequest("显示桌面校验待办", null, PriorityLevel.P2, now.AddHours(1), null, null),
        CancellationToken.None);
    var viewModel = new DesktopCalendarViewModel(repository, new FixedClock(now), timeZone, _ => Task.CompletedTask);
    await viewModel.RefreshAsync();
    var statePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}", "desktop-calendar.json");
    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
    new DesktopCalendarStateStore(statePath).Save(new DesktopCalendarWindowState(Left: 120, Top: 120));

    Exception? renderFailure = null;
    var renderThread = new Thread(() =>
    {
        var toggledDesktop = false;
        try
        {
            var window = new WechatDashboard.App.Views.Todos.DesktopCalendarWindow(
                viewModel, new DesktopCalendarStateStore(statePath));
            window.Show();
            window.UpdateLayout();
            var nativeHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

            var embedOption = (System.Windows.Controls.CheckBox)window.FindName("DesktopEmbedCheckBox");
            embedOption.IsChecked = true;
            window.UpdateLayout();
            AssertTrue(embedOption.IsChecked == true,
                $"Embedding must succeed for the Show Desktop scenario: {((System.Windows.Controls.TextBlock)window.FindName("DockStatusText")).Text}");
            var embeddedBounds = DesktopCalendarNativeAssertions.GetBounds(nativeHandle);
            AssertTrue(!DesktopCalendarNativeAssertions.HasIconLayerAbove(nativeHandle),
                "A freshly embedded calendar must start above the desktop icon layer.");

            // 端到端：注入真实 Win+D（与任务栏「显示桌面」按钮同一壳层路径）。
            // 守卫定时器在泵消息期间应把日历复位到被抬高的图标层之上。
            DesktopCalendarNativeAssertions.SendWinD();
            toggledDesktop = true;
            PumpMessages(1200);
            AssertTrue(!DesktopCalendarNativeAssertions.HasIconLayerAbove(nativeHandle),
                "An embedded calendar must stay above the raised desktop icon layer after Win+D / Show Desktop.");
            AssertTrue(DesktopCalendarNativeAssertions.IsWindowVisible(nativeHandle) && !DesktopCalendarNativeAssertions.IsIconic(nativeHandle),
                "An embedded calendar must remain visible and unminimized after Win+D / Show Desktop.");
            AssertEqual(embeddedBounds, DesktopCalendarNativeAssertions.GetBounds(nativeHandle),
                "Surviving Show Desktop must not move or resize the embedded calendar.");

            // 切回用户桌面状态并确认嵌入状态不受影响。
            DesktopCalendarNativeAssertions.SendWinD();
            toggledDesktop = false;
            PumpMessages(600);
            AssertTrue(!DesktopCalendarNativeAssertions.HasIconLayerAbove(nativeHandle),
                "Toggling the desktop back must leave the embedded calendar above the icon layer.");

            window.Shutdown();
        }
        catch (Exception ex)
        {
            renderFailure = ex;
        }
        finally
        {
            // 异常退出时也要把用户的桌面切换回原状态。
            if (toggledDesktop)
            {
                try { DesktopCalendarNativeAssertions.SendWinD(); }
                catch { /* 恢复失败不影响测试结论，用户可手动再按一次 Win+D。 */ }
            }
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
    renderThread.SetApartmentState(ApartmentState.STA);
    renderThread.Start();

    AssertTrue(renderThread.Join(TimeSpan.FromSeconds(30)), "Show Desktop survival thread should finish without hanging.");
    if (renderFailure is not null)
    {
        throw new InvalidOperationException("The embedded calendar must survive Show Desktop without minimizing.", renderFailure);
    }
}

static Task TestShihuatongMessageContentDecoderAsync()
{
    var text = "石化通本地消息";
    var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
    var nested = new byte[] { 0x0A, (byte)textBytes.Length }.Concat(textBytes).ToArray();
    var payload = new byte[] { 0x12, (byte)nested.Length }.Concat(nested).ToArray();

    var decoded = ShihuatongMessageContentDecoder.TryDecodeText(payload);

    AssertEqual(text, decoded, "Decoder should read field 2 CoreIMTextMessage field 1 text.");
    AssertEqual<string?>(null, ShihuatongMessageContentDecoder.TryDecodeText(Array.Empty<byte>()), "Empty payload should not produce text.");

    // 引用会议必须保留完整正文和时间，供原始消息、待办说明及截止日解析共同使用。
    const string quotedMeeting = "李工 邀请您参加腾讯会议\n会议时间：2026/10/28 09:00-12:00";
    var combined = ShihuatongLocalDatabaseReader.CombineWithReference(text, quotedMeeting);
    AssertEqual($"{text}\n\n引用消息：\n{quotedMeeting}", combined,
        "A referenced meeting should be appended with an explicit boundary.");
    AssertEqual(combined, ShihuatongLocalDatabaseReader.CombineWithReference(combined, quotedMeeting),
        "An already included reference must not be duplicated.");
    AssertEqual(text, ShihuatongLocalDatabaseReader.CombineWithReference(text, " \n\t"),
        "A blank reference should leave the current content unchanged.");
    return Task.CompletedTask;
}


static async Task TestWindowTextCaptureAdapterAsync()
{
    var capturedAt = new DateTimeOffset(2026, 6, 4, 9, 30, 0, TimeSpan.FromHours(8));
    var provider = new StaticWindowTextSnapshotProvider(new[]
    {
        new WindowTextSnapshot(
            WindowTitle: "CRM项目群 - 微信",
            Text: """
                  CRM项目群
                  09:20 王经理: @张三 今天下班前处理线上故障
                  09:21 李工：同步一下接口变更
                  """,
            CapturedAt: capturedAt),
        new WindowTextSnapshot(
            WindowTitle: "无关窗口",
            Text: "09:25 路人: 不应采集",
            CapturedAt: capturedAt)
    });
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "微信可见窗口",
        WindowTitleContains: "微信",
        ChatId: "visible-window",
        ChatName: "微信可见窗口");
    var adapter = new WindowTextCaptureAdapter(options, provider);

    var firstBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);
    var secondBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>
    {
        [adapter.Name] = firstBatch.NextOffset
    }), CancellationToken.None);
    var replayBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);

    AssertEqual("WeChat.WindowText", adapter.Name, "Adapter name should identify source and capture kind.");
    AssertEqual(2, firstBatch.Messages.Count, "Only chat message lines from matching windows should be captured.");
    AssertEqual(0, secondBatch.Messages.Count, "Identical snapshots should be skipped by offset.");
    AssertEqual(firstBatch.Messages[0].SourceMessageKey, replayBatch.Messages[0].SourceMessageKey, "Source keys should be stable across captures.");
    AssertEqual("WeChat", firstBatch.Messages[0].Source, "Captured source should follow options.");
    AssertEqual("CRM项目群", firstBatch.Messages[0].ChatName, "Chat name should be inferred from the window title.");
    AssertEqual("王经理", firstBatch.Messages[0].SenderName, "Sender should be parsed from visible text.");
    AssertEqual("@张三 今天下班前处理线上故障", firstBatch.Messages[0].Content, "Content should be normalized from visible text.");
    AssertEqual(new DateTimeOffset(2026, 6, 4, 9, 20, 0, TimeSpan.FromHours(8)), firstBatch.Messages[0].SentAt, "Visible HH:mm time should use snapshot date.");
}

static async Task TestWindowTextCaptureAdapterSplitBlocksAsync()
{
    var capturedAt = new DateTimeOffset(2026, 6, 4, 9, 30, 0, TimeSpan.FromHours(8));
    var provider = new StaticWindowTextSnapshotProvider(new[]
    {
        new WindowTextSnapshot(
            WindowTitle: "微信",
            Text: """
                  CRM项目群
                  09:20
                  王经理
                  @张三 今天下班前处理线上故障
                  09:21
                  李工
                  同步一下接口变更
                  """,
            CapturedAt: capturedAt)
    });
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "微信可见窗口",
        WindowTitleContains: "微信",
        ChatId: "visible-window",
        ChatName: "微信可见窗口");
    var adapter = new WindowTextCaptureAdapter(options, provider);

    var batch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);

    AssertEqual(2, batch.Messages.Count, "Split UIA blocks should be parsed as visible messages.");
    AssertEqual("王经理", batch.Messages[0].SenderName, "Sender should come from the split block.");
    AssertEqual("@张三 今天下班前处理线上故障", batch.Messages[0].Content, "Content should come from the line after sender.");
    AssertEqual("CRM项目群", batch.Messages[0].ChatName, "Chat name should be inferred from the first visible title line when the window title is generic.");
    AssertEqual(new DateTimeOffset(2026, 6, 4, 9, 20, 0, TimeSpan.FromHours(8)), batch.Messages[0].SentAt, "Split block time should use snapshot date.");
    AssertEqual("李工", batch.Messages[1].SenderName, "Second sender should be parsed from the next block.");
    AssertEqual("同步一下接口变更", batch.Messages[1].Content, "Second content should be parsed from the next block.");
}


static async Task TestWindowTextCaptureAdapterSkipsStatusChatNameAsync()
{
    var capturedAt = new DateTimeOffset(2026, 8, 4, 9, 30, 0, TimeSpan.FromHours(8));
    var provider = new StaticWindowTextSnapshotProvider(new[]
    {
        new WindowTextSnapshot(
            WindowTitle: "\u5FAE\u4FE1",
            Text: "\u5FAE\u4FE1\u76D1\u542C\u8FD0\u884C\u4E2D\uFF0C\u6700\u8FD1\u91C7\u96C6 9 \u6761\uFF0C\u5165\u5E93 8 \u6761\uFF0C\u5F85\u529E 1 \u6761\nCRM\u9879\u76EE\u7FA4 10\u6761\n09:20 \u738B\u7ECF\u7406: @\u5F20\u4E09 \u4ECA\u5929\u4E0B\u73ED\u524D\u5904\u7406\u7EBF\u4E0A\u6545\u969C",
            CapturedAt: capturedAt)
    });
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "\u5FAE\u4FE1\u53EF\u89C1\u7A97\u53E3",
        WindowTitleContains: "\u5FAE\u4FE1",
        ChatId: "visible-window",
        ChatName: "\u5FAE\u4FE1\u53EF\u89C1\u7A97\u53E3");

    var batch = await new WindowTextCaptureAdapter(options, provider)
        .CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);

    AssertEqual(1, batch.Messages.Count, "The status summary must not be captured as a chat name.");
    AssertEqual("CRM\u9879\u76EE\u7FA4", batch.Messages[0].ChatName, "The actual group name should be used without the unread-count suffix.");
}

static async Task TestWindowTextCaptureAdapterRejectsDashboardSnapshotAsync()
{
    var capturedAt = new DateTimeOffset(2026, 8, 4, 13, 50, 0, TimeSpan.FromHours(8));
    var provider = new StaticWindowTextSnapshotProvider(new[]
    {
        new WindowTextSnapshot(
            WindowTitle: "\u5FAE\u4FE1",
            Text: "\u9879\u76EE\u770B\u677F\n\u5F85\u529E\u7406\n\u5DF2\u529E\u7406\n\u6D88\u606F\u6D41\n\u6BCF\u9875 50\n\u672B\u9875\n13:48 \u8DF3\u8F6C: 1/357 \u9875",
            CapturedAt: capturedAt)
    });
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "\u5FAE\u4FE1\u53EF\u89C1\u7A97\u53E3",
        WindowTitleContains: "\u5FAE\u4FE1",
        ChatId: "visible-window",
        ChatName: "\u5FAE\u4FE1\u53EF\u89C1\u7A97\u53E3");

    var batch = await new WindowTextCaptureAdapter(options, provider)
        .CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);

    AssertEqual(0, batch.Messages.Count, "Dashboard UI text must never be captured as WeChat messages.");
}

static async Task TestWindowsUiAutomationSnapshotProviderAsync()
{
    var capturedAt = new DateTimeOffset(2026, 6, 4, 10, 0, 0, TimeSpan.FromHours(8));
    var reader = new FakeWindowAutomationReader(new[]
    {
        new WindowAutomationElement(
            Name: "CRM项目群 - 微信",
            Text: "",
            Children: new[]
            {
                new WindowAutomationElement("标题", "CRM项目群", Array.Empty<WindowAutomationElement>()),
                new WindowAutomationElement("消息1", "09:20 王经理: @张三 今天处理线上故障", Array.Empty<WindowAutomationElement>()),
                new WindowAutomationElement("消息2", "09:21 李工：同步接口变更", Array.Empty<WindowAutomationElement>())
            }),
        new WindowAutomationElement(
            Name: "无关窗口",
            Text: "",
            Children: new[]
            {
                new WindowAutomationElement("消息", "09:30 路人: 不应采集", Array.Empty<WindowAutomationElement>())
            })
    }, capturedAt);
    var provider = new WindowsUiAutomationSnapshotProvider(reader);
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "微信可见窗口",
        WindowTitleContains: "微信",
        ChatId: "visible-window",
        ChatName: "微信可见窗口");

    var snapshots = await provider.GetSnapshotsAsync(options, CancellationToken.None);

    AssertEqual(1, snapshots.Count, "Only matching WeChat windows should be returned.");
    AssertEqual("CRM项目群 - 微信", snapshots[0].WindowTitle, "Window title should round-trip.");
    AssertTrue(snapshots[0].Text.Contains("CRM项目群"), "Aggregated text should include child text.");
    AssertTrue(snapshots[0].Text.Contains("09:20 王经理: @张三 今天处理线上故障"), "Aggregated text should include message lines.");
    AssertFalse(snapshots[0].Text.Contains("不应采集"), "Unmatched windows should not leak into snapshot text.");
    AssertEqual(capturedAt, snapshots[0].CapturedAt, "Reader timestamp should be preserved.");
}

static Task TestWeChatOcrCropCalculatorAsync()
{
    var crop = WindowOcrCropCalculator.CalculateWeChatChatPanel(1552, 1000);
    var smallWindowCrop = WindowOcrCropCalculator.CalculateWeChatChatPanel(500, 300);

    AssertTrue(crop.X >= 400 && crop.X <= 480, "WeChat crop should skip the left navigation and conversation list.");
    AssertTrue(crop.Y >= 40 && crop.Y <= 80, "WeChat crop should skip the window title bar.");
    AssertTrue(crop.Width < 1200, "WeChat crop width should be smaller than the full window width.");
    AssertTrue(crop.Height > 850, "WeChat crop should keep most of the chat area height.");
    AssertEqual(new WindowOcrCropRectangle(0, 0, 500, 300), smallWindowCrop, "Small windows should fall back to full-window OCR.");

    return Task.CompletedTask;
}

static async Task TestWindowsOcrSnapshotProviderAsync()
{
    var capturedAt = new DateTimeOffset(2026, 6, 4, 21, 29, 0, TimeSpan.FromHours(8));
    var reader = new FakeWindowAutomationReader(new[]
    {
        new WindowAutomationElement(
            Name: "微信",
            Text: "微信 Weixin 微信 最小化 最大化 上下文帮助 关闭",
            Children: Array.Empty<WindowAutomationElement>(),
            NativeWindowHandle: 1001),
        new WindowAutomationElement(
            Name: "微信项目消息看板",
            Text: "微信项目消息看板",
            Children: Array.Empty<WindowAutomationElement>(),
            NativeWindowHandle: 1003),
        new WindowAutomationElement(
            Name: "无关窗口",
            Text: "不应 OCR",
            Children: Array.Empty<WindowAutomationElement>(),
            NativeWindowHandle: 1002)
    }, capturedAt);
    var ocrReader = new FakeScreenOcrReader(new Dictionary<int, string>
    {
        [1001] = """
                 数字石化（二期） 盈科国科沟通(34)
                 18:38
                 国科 王建辉
                 @白驹过隙
                 """,
        [1003] = "不应采集本应用窗口"
    });
    var provider = new WindowsOcrWindowTextSnapshotProvider(reader, ocrReader);
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "微信可见窗口",
        WindowTitleContains: "微信",
        ChatId: "visible-window",
        ChatName: "微信可见窗口")
    {
        IgnoreWindowTitleContains = new[] { "微信项目消息看板" }
    };

    var snapshots = await provider.GetSnapshotsAsync(options, CancellationToken.None);

    AssertEqual(1, snapshots.Count, "OCR provider should return only matching WeChat windows.");
    AssertEqual("微信", snapshots[0].WindowTitle, "OCR snapshot should keep the source window title.");
    AssertTrue(snapshots[0].Text.StartsWith("数字石化（二期）", StringComparison.Ordinal), "OCR chat text should be prioritized before UIA window chrome.");
    AssertTrue(snapshots[0].Text.Contains("数字石化（二期）"), "OCR snapshot should contain visible chat title.");
    AssertTrue(snapshots[0].Text.Contains("@白驹过隙"), "OCR snapshot should contain visible @ mention text.");
    AssertFalse(snapshots[0].Text.Contains("不应采集本应用窗口"), "OCR snapshot should exclude this dashboard window.");
    AssertEqual(capturedAt, snapshots[0].CapturedAt, "OCR snapshot should preserve automation read timestamp.");
}

static async Task TestWindowCaptureDiagnosticsServiceAsync()
{
    var capturedAt = new DateTimeOffset(2026, 6, 4, 10, 30, 0, TimeSpan.FromHours(8));
    var longLine = new string('测', 160);
    var provider = new StaticWindowTextSnapshotProvider(new[]
    {
        new WindowTextSnapshot("CRM项目群 - 微信", $"CRM项目群\n09:20 王经理: {longLine}", capturedAt),
        new WindowTextSnapshot("无关窗口", "09:21 路人: 不应显示", capturedAt)
    });
    var service = new WindowCaptureDiagnosticsService(provider);
    var options = new WindowTextCaptureOptions(
        Source: "WeChat",
        DisplayName: "微信可见窗口",
        WindowTitleContains: "微信",
        ChatId: "visible-window",
        ChatName: "微信可见窗口");

    var rows = await service.ScanAsync(options, CancellationToken.None);

    AssertEqual(1, rows.Count, "Diagnostics should include only matching snapshots.");
    AssertEqual("CRM项目群 - 微信", rows[0].WindowTitle, "Diagnostics should keep the title.");
    AssertEqual(capturedAt, rows[0].CapturedAt, "Diagnostics should keep capture time.");
    AssertTrue(rows[0].Preview.Contains("CRM项目群"), "Preview should include visible text.");
    AssertTrue(rows[0].Preview.Length <= 123, "Preview should be truncated for UI display.");
    AssertFalse(rows[0].Preview.Contains("不应显示"), "Preview should not include unmatched windows.");
}

static async Task TestJsonlDirectoryCaptureAdapterAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var filePath = Path.Combine(root, "wechat.jsonl");
    await File.WriteAllLinesAsync(filePath, new[]
    {
        """{"id":"wx-1","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}""",
        """{"id":"wx-2","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"李工","content":"同步一下接口变更","sentAt":"2026-06-03T10:05:00+08:00","messageType":"Text"}"""
    }, CancellationToken.None);

    var adapter = new JsonlDirectoryCaptureAdapter("WeChat", root);
    var firstBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);
    var secondBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>
    {
        [adapter.Name] = firstBatch.NextOffset
    }), CancellationToken.None);
    await File.AppendAllLinesAsync(filePath, new[]
    {
        """{"id":"wx-3","platform":"WeChat","chatId":"pay","chatName":"支付项目群","senderName":"赵经理","content":"@张三 支付联调今天反馈","sentAt":"2026-06-03T10:10:00+08:00","messageType":"Text"}"""
    }, CancellationToken.None);
    var appendBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>
    {
        [adapter.Name] = firstBatch.NextOffset
    }), CancellationToken.None);

    AssertEqual("WeChat.JsonlDirectory", adapter.Name, "Adapter name should include the source platform.");
    AssertEqual(2, firstBatch.Messages.Count, "First capture should read both messages.");
    AssertEqual(0, secondBatch.Messages.Count, "Second capture with offset should not reread messages.");
    AssertEqual(1, appendBatch.Messages.Count, "Appended capture should read only the new line.");
    AssertEqual("WeChat:wx-3", appendBatch.Messages[0].SourceMessageKey, "Appended message should preserve identity.");
    AssertEqual("WeChat", firstBatch.Messages[0].Source, "Message source should come from platform.");
    AssertEqual("WeChat:wx-1", firstBatch.Messages[0].SourceMessageKey, "Source key should be globally namespaced.");
    AssertEqual("CRM项目群", firstBatch.Messages[0].ChatName, "Chat name should round-trip.");
}

static async Task TestWeChatLocalExportCaptureAdapterAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var filePath = Path.Combine(root, "wechat-local-export.jsonl");
    await File.WriteAllLinesAsync(filePath, new[]
    {
        """{"msgId":"10001","chatId":"room-digital","chatName":"数字石化（二期）","senderName":"国科 王建辉","content":"@戴少峰 请确认域名配置","createTime":"2026-06-05T09:10:00+08:00","msgType":"Text"}""",
        """{"msgId":"10002","talker":"room-digital","roomName":"数字石化（二期）","sender":"梁大鹏","message":"接口域名还有点问题","timestamp":1780614900,"type":"Text"}"""
    }, CancellationToken.None);

    var adapter = new WeChatLocalExportCaptureAdapter(new WeChatLocalExportOptions(root));
    var firstBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);
    var secondBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>
    {
        [adapter.Name] = firstBatch.NextOffset
    }), CancellationToken.None);
    await File.AppendAllLinesAsync(filePath, new[]
    {
        """{"msgId":"10003","chatId":"room-digital","chatName":"数字石化（二期）","senderName":"刘荐辉","content":"@白驹过隙 今天下班前反馈","createTime":"2026-06-05T09:20:00+08:00","msgType":"Text"}"""
    }, CancellationToken.None);
    var appendBatch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>
    {
        [adapter.Name] = firstBatch.NextOffset
    }), CancellationToken.None);

    AssertEqual("WeChat.LocalExport", adapter.Name, "Adapter name should reflect the local export source.");
    AssertEqual(2, firstBatch.Messages.Count, "First capture should read exported local messages.");
    AssertEqual(0, secondBatch.Messages.Count, "Offset should prevent rereading exported local messages.");
    AssertEqual(1, appendBatch.Messages.Count, "Appended capture should read only the new exported row.");
    AssertEqual("WeChat", firstBatch.Messages[0].Source, "Local export should normalize source to WeChat.");
    AssertEqual("WeChat:local:10001", firstBatch.Messages[0].SourceMessageKey, "Source key should use local message identity.");
    AssertEqual("room-digital", firstBatch.Messages[0].ChatId, "Chat id should come from local export fields.");
    AssertEqual("数字石化（二期）", firstBatch.Messages[0].ChatName, "Chat name should come from local export fields.");
    AssertEqual("国科 王建辉", firstBatch.Messages[0].SenderName, "Sender should come from local export fields.");
    AssertEqual("@戴少峰 请确认域名配置", firstBatch.Messages[0].Content, "Content should come from local export fields.");
    AssertEqual("WeChat:local:10003", appendBatch.Messages[0].SourceMessageKey, "Appended message should preserve local message identity.");
}

static Task TestKeyProbeOptionsAsync()
{
    var dllPath = Path.Combine(Path.GetTempPath(), "wx key", "wx_key.dll");
    var parsed = KeyProbeOptions.TryParse(
        new[]
        {
            "--pid", "1234",
            "--pipe-name", "WechatDashboard.KeyProbe.test-1",
            "--dll-path", dllPath,
            "--timeout-seconds", "45"
        },
        out var options,
        out var error);

    AssertTrue(parsed, $"Valid KeyProbe arguments should parse: {error}");
    AssertNotNull(options, "Parsed KeyProbe options should be returned.");
    AssertEqual(1234, options!.ProcessId, "PID should round-trip.");
    AssertEqual("WechatDashboard.KeyProbe.test-1", options.PipeName, "Pipe name should round-trip.");
    AssertEqual(Path.GetFullPath(dllPath), options.DllPath, "DLL path should normalize without exposing a key.");
    AssertEqual(TimeSpan.FromSeconds(45), options.Timeout, "Timeout should round-trip.");

    var rejected = KeyProbeOptions.TryParse(
        new[]
        {
            "--pid", "1234",
            "--pipe-name", "invalid\\pipe",
            "--dll-path", dllPath,
            "--timeout-seconds", "45"
        },
        out _,
        out _);
    AssertFalse(rejected, "Pipe names containing separators must be rejected.");

    var unknownRejected = KeyProbeOptions.TryParse(
        new[]
        {
            "--pid", "1234",
            "--pipe-name", "WechatDashboard.KeyProbe.test-1",
            "--dll-path", dllPath,
            "--timeout-seconds", "45",
            "--db-key", new string('a', 64)
        },
        out _,
        out _);
    AssertFalse(unknownRejected, "Unknown arguments must be rejected so secrets cannot be added to the command line.");
    return Task.CompletedTask;
}

static async Task TestWeChatLocalReaderServiceExtractsNativeDatabaseKeyAsync()
{
    var reader = new FakeWeChatLocalDatabaseReader();
    var service = new WeChatLocalReaderService(
        reader,
        databaseRootLocator: () => @"D:\fixture\db_storage",
        processIdLocator: () => 9876);

    var result = await service.ExtractDatabaseKeyAsync(
        new WeChatDatabaseKeyExtractionOptions(Seconds: 12, TargetProcessId: 4321),
        CancellationToken.None);

    AssertNotNull(result, "Native reader should return an initialization result.");
    AssertEqual("native-csharp", result!.Provider, "Result should identify the in-process C# reader.");
    AssertEqual(4321, result.TargetProcessId, "Configured target PID should reach the native reader.");
    AssertNotNull(reader.LastInitializationRequest, "Native reader should receive an initialization request.");
    AssertEqual(4321, reader.LastInitializationRequest!.ProcessId, "Initialization should carry the target PID.");
    AssertEqual(TimeSpan.FromSeconds(12), reader.LastInitializationRequest.KeyTimeout, "Initialization should carry the bounded timeout.");
    AssertEqual(@"D:\fixture\db_storage", reader.LastInitializationRequest.DatabaseRoot, "Initialization should use the discovered database root.");
    AssertTrue(service.IsInitialized, "Successful extraction and validation should leave the native session ready.");
    await service.DisposeAsync();
}

static Task TestShihuatongSourceStampAsync()
{
    const string durableStamp = "4096:638919000000000000|8192:638919000000000001";
    const string legacyStampWithShm = "4096:638919000000000000|8192:638919000000000001|32768:638919000000000099";
    const string differentWal = "4096:638919000000000000|8193:638919000000000002";

    AssertTrue(
        ShihuatongDatabaseSnapshot.HasSameDurableContent(legacyStampWithShm, durableStamp),
        "A legacy stamp that differs only by its SHM component should not trigger database decryption.");
    AssertTrue(
        !ShihuatongDatabaseSnapshot.HasSameDurableContent(legacyStampWithShm, differentWal),
        "A WAL change must still trigger an incremental read.");
    return Task.CompletedTask;
}

static async Task TestNativeHookKeyProviderRejectsMissingBinariesAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    var provider = new NativeHookKeyProvider(
        Path.Combine(root, "WechatDashboard.KeyProbe.exe"),
        Path.Combine(root, "wx_key.dll"));
    var rejected = false;
    try
    {
        using var _ = await provider.AcquireAsync(
            new WeChatDatabaseKeyRequest(1234, TimeSpan.FromSeconds(1)),
            CancellationToken.None);
    }
    catch (FileNotFoundException exception)
    {
        rejected = true;
        AssertFalse(
            System.Text.RegularExpressions.Regex.IsMatch(
                exception.Message,
                "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])"),
            "Missing-binary diagnostics must not contain a key-shaped value.");
    }

    AssertTrue(rejected, "Native provider must fail before process access when KeyProbe is missing.");
}

static async Task TestWeChatLocalReaderServiceReadsPagedMessagesAsync()
{
    var sentAt = new DateTimeOffset(2026, 6, 29, 10, 0, 0, TimeSpan.FromHours(8));
    var message = new CapturedMessage(
        "WeChat",
        "WeChat:local:room@chatroom:message_0.db:1051:1782702000",
        "room@chatroom",
        "项目群",
        "Alice",
        "第51条消息",
        sentAt,
        MessageType.Text);
    var reader = new FakeWeChatLocalDatabaseReader(new[] { message }, totalCount: 75);
    var service = new WeChatLocalReaderService(
        reader,
        databaseRootLocator: () => @"D:\fixture\db_storage",
        processIdLocator: () => 1234);
    AssertTrue(await service.InitializeAsync(CancellationToken.None), "Fixture reader should initialize.");
    var date = new DateTime(2026, 6, 29);

    var page = await service.ReadMessagesAsync(
        new WeChatLocalMessageReadOptions(date, PageNumber: 2, PageSize: 50),
        CancellationToken.None);

    AssertNotNull(page, "Message page should be returned.");
    AssertEqual(75, page!.TotalCount, "Total message count should come from reader output.");
    AssertEqual(2, page.PageNumber, "Requested page number should round-trip.");
    AssertEqual(50, page.PageSize, "Requested page size should round-trip.");
    AssertEqual(1, page.Messages.Count, "Second page should contain parsed messages.");
    AssertEqual("第51条消息", page.Messages[0].Content, "Message content should be parsed for the table.");
    AssertEqual("项目群", page.Messages[0].ChatName, "Chat name should be parsed for the table.");
    AssertEqual("Alice", page.Messages[0].SenderName, "Sender should be parsed for the table.");

    AssertNotNull(reader.LastReadRequest, "Native reader should receive a read request.");
    var localDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
    var expectedStart = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    AssertEqual(expectedStart, reader.LastReadRequest!.Start!.Value, "Reader should receive the local day start.");
    AssertEqual(50, reader.LastReadRequest.Offset, "Second page should use offset 50.");
    AssertEqual(50, reader.LastReadRequest.Limit, "Reader should receive the selected page size.");
    await service.DisposeAsync();
}

static async Task TestProtectedWeChatDatabaseKeyStoreAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "protected-key", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(root, "protected-wechat-key.bin");
    var key = Enumerable.Range(1, WeChatDatabaseKeyLease.KeyLength).Select(value => (byte)value).ToArray();
    try
    {
        var store = new ProtectedWeChatDatabaseKeyStore(path);
        using (var lease = new WeChatDatabaseKeyLease(key))
        {
            await store.SaveAsync(lease, CancellationToken.None);
        }

        AssertTrue(File.Exists(path), "Saving a validated key should create the protected cache.");
        var persisted = await File.ReadAllBytesAsync(path);
        AssertTrue(persisted.AsSpan().IndexOf(key) < 0, "Protected cache must not contain the plaintext key bytes.");
        AssertFalse(
            Encoding.UTF8.GetString(persisted).Contains(Convert.ToHexString(key), StringComparison.OrdinalIgnoreCase),
            "Protected cache must not contain the hexadecimal key.");

        using var restored = await store.LoadAsync(CancellationToken.None);
        AssertNotNull(restored, "The same Windows user should restore the protected key.");
        var restoredBytes = new byte[WeChatDatabaseKeyLease.KeyLength];
        restored!.CopyTo(restoredBytes);
        try
        {
            AssertTrue(restoredBytes.AsSpan().SequenceEqual(key), "Restored key must match the original bytes.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(restoredBytes);
        }

        await File.WriteAllBytesAsync(path, "broken-cache"u8.ToArray());
        var rejected = false;
        try
        {
            using var _ = await store.LoadAsync(CancellationToken.None);
        }
        catch (WeChatReaderException exception)
        {
            rejected = exception.Stage == WeChatReaderStage.ProtectedKey &&
                       exception.ErrorCode == WeChatReaderErrorCode.ProtectedKeyInvalid;
        }

        AssertTrue(rejected, "A malformed protected cache must return a stable protected-key error.");
        await store.ClearAsync(CancellationToken.None);
        AssertFalse(File.Exists(path), "Explicit cache reset should remove only the protected key file.");

        var protectionFailed = false;
        var failingStore = new ProtectedWeChatDatabaseKeyStore(path, new ThrowingWeChatKeyProtector());
        try
        {
            using var lease = new WeChatDatabaseKeyLease(key);
            await failingStore.SaveAsync(lease, CancellationToken.None);
        }
        catch (WeChatReaderException exception)
        {
            protectionFailed = exception.Stage == WeChatReaderStage.ProtectedKey &&
                               exception.ErrorCode == WeChatReaderErrorCode.ProtectedKeyUnavailable;
        }
        AssertTrue(protectionFailed, "Data-protection failures must use a stable protected-key error.");
        AssertFalse(File.Exists(path), "A failed protection operation must not create the formal cache file.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static Task TestProtectedDeepSeekApiKeyStoreAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "deepseek-key", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(root, "protected-deepseek-api-key.bin");
    var store = new ProtectedDeepSeekApiKeyStore(path);
    try
    {
        // 首次运行没有密文文件时应保持未配置状态。
        AssertTrue(store.Load() is null, "A missing DeepSeek key file should return no saved key.");
        const string apiKey = "sk-deepseek-test-only";
        store.Save($"  {apiKey}  ");
        AssertTrue(File.Exists(path), "Saving should create the DeepSeek protected key file.");

        // 密文既不能包含明文，也必须能由当前 Windows 用户还原。
        var persisted = File.ReadAllBytes(path);
        AssertFalse(Encoding.UTF8.GetString(persisted).Contains(apiKey, StringComparison.Ordinal),
            "The protected file must not contain the plaintext DeepSeek API key.");
        AssertEqual(apiKey, store.Load(), "The saved DeepSeek API key should round-trip after trimming.");

        // 损坏文件必须返回可读错误，不能把异常内容当作有效密钥。
        File.WriteAllText(path, "broken-cache");
        var rejected = false;
        try
        {
            store.Load();
        }
        catch (InvalidOperationException exception)
        {
            rejected = exception.Message.Contains("重新保存", StringComparison.Ordinal);
        }

        AssertTrue(rejected, "A corrupt DeepSeek key file should request configuration again.");
        store.Clear();
        AssertFalse(File.Exists(path), "Clear should remove only the DeepSeek protected key file.");
        AssertTrue(store.Load() is null, "A cleared DeepSeek key should return to the unconfigured state.");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    return Task.CompletedTask;
}

static async Task TestNativeWeChatReaderRestoresProtectedKeyAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "protected-key-reader", Guid.NewGuid().ToString("N"));
    var databaseRoot = Path.Combine(root, "db_storage");
    var snapshotRoot = Path.Combine(root, "snapshots");
    var cachePath = Path.Combine(root, "protected-wechat-key.bin");
    var key = Enumerable.Range(41, WeChatDatabaseKeyLease.KeyLength).Select(value => (byte)value).ToArray();
    var wrongKey = Enumerable.Range(91, WeChatDatabaseKeyLease.KeyLength).Select(value => (byte)value).ToArray();
    try
    {
        await CreateEncryptedWeChatFixtureAsync(databaseRoot, key);
        var store = new ProtectedWeChatDatabaseKeyStore(cachePath);
        await using (var initialReader = new NativeWeChatLocalDatabaseReader(
                         new FakeWeChatDatabaseKeyProvider(key),
                         snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
                         stateStore: new WeChatReaderStateStore(Path.Combine(root, "state-1.json")),
                         keyStore: store))
        {
            var initialized = await initialReader.InitializeAsync(
                new WeChatInitializationRequest(databaseRoot, 1234, TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            AssertTrue(initialized.Succeeded, "A validated KeyProbe key should initialize the reader.");
        }

        AssertTrue(File.Exists(cachePath), "Successful validation should persist the protected key cache.");
        var validCache = await File.ReadAllBytesAsync(cachePath);

        await using (var restoredReader = new NativeWeChatLocalDatabaseReader(
                          new ThrowingWeChatDatabaseKeyProvider(new InvalidOperationException("KeyProbe must not run")),
                          snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
                          stateStore: new WeChatReaderStateStore(Path.Combine(root, "state-1.json")),
                          keyStore: store))
        {
            var restored = await restoredReader.RestoreProtectedKeyAsync(
                new WeChatProtectedKeyRestoreRequest(databaseRoot),
                CancellationToken.None);
            AssertTrue(restored.Succeeded, "The same user should restore and validate the cached key without KeyProbe.");
            AssertTrue(restoredReader.IsInitialized, "Protected-key restoration should leave the reader initialized.");
            AssertFalse(
                restored.Stages.Any(stage => stage.Stage == WeChatReaderStage.KeyValidation),
                "An unchanged protected key and database proof should skip repeated PBKDF2 validation.");
        }

        await using (var invalidProbeReader = new NativeWeChatLocalDatabaseReader(
                         new FakeWeChatDatabaseKeyProvider(wrongKey),
                         snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
                         stateStore: new WeChatReaderStateStore(Path.Combine(root, "state-3.json")),
                         keyStore: store))
        {
            var rejected = await invalidProbeReader.InitializeAsync(
                new WeChatInitializationRequest(databaseRoot, 1234, TimeSpan.FromSeconds(1)),
                CancellationToken.None);
            AssertFalse(rejected.Succeeded, "An invalid replacement key must fail database validation.");
        }
        AssertTrue((await File.ReadAllBytesAsync(cachePath)).AsSpan().SequenceEqual(validCache),
            "A failed extraction must not overwrite the last valid protected cache.");

        using (var invalidLease = new WeChatDatabaseKeyLease(wrongKey))
        {
            await store.SaveAsync(invalidLease, CancellationToken.None);
        }
        await using (var invalidCacheReader = new NativeWeChatLocalDatabaseReader(
                         new ThrowingWeChatDatabaseKeyProvider(new InvalidOperationException("KeyProbe must not run")),
                         snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
                         stateStore: new WeChatReaderStateStore(Path.Combine(root, "state-4.json")),
                         keyStore: store))
        {
            var rejected = await invalidCacheReader.RestoreProtectedKeyAsync(
                new WeChatProtectedKeyRestoreRequest(databaseRoot),
                CancellationToken.None);
            AssertFalse(rejected.Succeeded, "A stale cached key must fail validation without falling through to KeyProbe.");
        }
        AssertFalse(File.Exists(cachePath), "A cache that fails database validation should be removed before explicit re-extraction.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(wrongKey);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task TestCapturePipelineAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var inputRoot = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(inputRoot);
    await File.WriteAllLinesAsync(Path.Combine(inputRoot, "wechat.jsonl"), new[]
    {
        """{"id":"wx-mention","platform":"WeChat","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 紧急，今天处理线上故障","sentAt":"2026-06-03T10:00:00+08:00","messageType":"Text"}""",
        """{"id":"wx-normal","platform":"WeChat","chatId":"pay","chatName":"支付项目群","senderName":"赵经理","content":"支付联调今天继续推进","sentAt":"2026-06-03T10:10:00+08:00","messageType":"Text"}""",
        // 三人群 @所有人 必须沿用真实 SQLite 工作单元创建自动待办，正文包含微信 U+2005 空白。
        """{"id":"wx-mention-all","platform":"WeChat","chatId":"three-person-room","chatName":"三人群","senderName":"白驹过隙","content":"@所有人\u2005 ai agent 开发测试","sentAt":"2026-06-03T10:20:00+08:00","messageType":"Text"}"""
    }, CancellationToken.None);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var followedProjectRepository = new SqliteFollowedProjectRepository(databasePath);
    await followedProjectRepository.SaveAsync("占位项目", ProjectColorPolicy.DefaultColor, CancellationToken.None);
    var managedCrmProject = await followedProjectRepository.SaveAsync("CRM升级", ProjectColorPolicy.Red, CancellationToken.None);
    var followedKeywordRepository = new SqliteFollowedProjectKeywordRepository(databasePath);
    await followedKeywordRepository.SaveAsync(managedCrmProject.Id, "CRM项目群", CancellationToken.None);
    var managedKeywords = await followedKeywordRepository.GetAllAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var offsetRepository = new SqliteProcessingOffsetRepository(databasePath);
    var pipeline = new MessageCapturePipeline(
        adapters: new IMessageCaptureAdapter[] { new JsonlDirectoryCaptureAdapter("WeChat", inputRoot) },
        new SqliteMessageProcessingUnitOfWork(databasePath),
        offsetRepository,
        new MentionDetector(new[] { "张三", "zhangsan" }),
        new ProjectClassifier(new[]
        {
            new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100),
            new ProjectRule(2, "支付平台", ProjectRuleType.Keyword, "支付", 80)
        }),
        new UrgencyRanker(priorityContacts: new[] { "王经理" }, priorityProjectIds: new[] { 1L }),
        message => FollowedProjectMatcher.Match(message, managedKeywords)?.ProjectId);

    var firstRun = await pipeline.RunOnceAsync(CancellationToken.None);
    var secondRun = await pipeline.RunOnceAsync(CancellationToken.None);
    var recentMessages = await messageRepository.GetRecentAsync(10, CancellationToken.None);
    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);

    AssertEqual(3, firstRun.CapturedCount, "First pipeline run should capture all three messages.");
    AssertEqual(2, firstRun.CreatedTodoCount, "Both a direct mention and a group mention-all should create todos.");
    AssertEqual(0, secondRun.CapturedCount, "Offset should prevent duplicate capture.");
    AssertEqual(CaptureSourceRunStatus.Empty, secondRun.Sources.Single().Status, "A successful zero-message source must be distinct from failure.");
    AssertEqual(3, recentMessages.Count, "All three messages should be persisted.");
    AssertEqual(2, pendingTodos.Count, "Both mention messages should persist pending todos.");
    var directMentionTodo = pendingTodos.Single(todo => todo.Title.Contains("@张三"));
    AssertEqual(1L, directMentionTodo.ProjectId, "Legacy classifier output should remain unchanged.");
    AssertEqual(managedCrmProject.Id, directMentionTodo.ManagedProjectId, "Automatic Todo should use the existing followed-project keyword matcher for its managed project.");
    var mentionAllTodo = pendingTodos.Single(todo => todo.Title.Contains("@所有人"));
    // 日历要求待办具有截止日期；此处直接验证三人群 @所有人 的产物满足显示条件。
    AssertTrue(mentionAllTodo.DueAt.HasValue, "A group mention-all Todo should have a due date for calendar display.");

    await using var resultConnection = new SqliteConnection($"Data Source={databasePath}");
    await resultConnection.OpenAsync(CancellationToken.None);
    await using var resultCommand = resultConnection.CreateCommand();
    resultCommand.CommandText = "SELECT COUNT(*) FROM message_classifications;";
    AssertEqual(3, Convert.ToInt32(await resultCommand.ExecuteScalarAsync(CancellationToken.None)), "Every persisted message should have a classification result.");
    resultCommand.CommandText = "SELECT COUNT(*) FROM urgency_scores;";
    AssertEqual(3, Convert.ToInt32(await resultCommand.ExecuteScalarAsync(CancellationToken.None)), "Every persisted message should have an urgency result.");
}

static async Task TestCapturePipelineMentionAnalysisAsync()
{
    // 使用真实 SQLite 工作单元，确保测试覆盖消息与自动待办的实际持久化路径。
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var analyzeMessageContent = false;
    var analysisCalls = new List<string>();
    using var cancellationSource = new CancellationTokenSource();
    const string mentionContent = "@我 请今天确认验收时间";
    // 用接近真实当前时间的发送时间，保证本地回退截止时间（消息发送日末）仍在未来，
    // 从而能断言规则提醒被创建；Agent 显式时间仍覆盖本地规则。
    var sentAt = new DateTimeOffset(
        DateTimeOffset.Now.Year,
        DateTimeOffset.Now.Month,
        DateTimeOffset.Now.Day,
        13, 30, 0,
        TimeSpan.FromHours(8));

    // 只改变来源消息键，便于用同一正文比较 Agent 分析前后的待办字段。
    CapturedMessage Capture(string key, string content) => new(
        "WeChat",
        key,
        "analysis-room",
        "分析群",
        "王工",
        content,
        sentAt,
        MessageType.Text);

    // 可控 Agent 委托模拟成功、故障、空白和外部取消，不产生真实模型费用。
    Task<MentionMessageAnalysis> Analyze(Message message, CancellationToken cancellationToken)
    {
        analysisCalls.Add(message.SourceMessageKey);
        if (message.SourceMessageKey == "summary-on")
        {
            return Task.FromResult(new MentionMessageAnalysis(
                PriorityLevel.P1,
                "  请今天确认验收时间。  ",
                DueAt: sentAt.Date.AddHours(18),
                FirstReminderAt: sentAt.Date.AddHours(17).AddMinutes(30)));
        }

        if (message.SourceMessageKey == "summary-skip")
        {
            // 明确返回 false，模拟无关或低优先级消息无需形成待办。
            return Task.FromResult(new MentionMessageAnalysis(
                PriorityLevel.P3,
                "仅同步进展，无需处理",
                ShouldCreate: false));
        }

        if (message.SourceMessageKey == "summary-failure")
        {
            throw new InvalidOperationException("模拟模型故障");
        }

        if (message.SourceMessageKey == "summary-blank")
        {
            return Task.FromResult(new MentionMessageAnalysis(PriorityLevel.P0, " \n "));
        }

        if (message.SourceMessageKey == "summary-cancel")
        {
            // 在 Agent 执行期间取消，验证取消不会被失败降级逻辑吞掉。
            cancellationSource.Cancel();
            return Task.FromCanceled<MentionMessageAnalysis>(cancellationToken);
        }

        throw new InvalidOperationException("未配置分析行为的消息不应调用模型。");
    }

    var pipeline = new MessageCapturePipeline(
        adapters: Array.Empty<IMessageCaptureAdapter>(),
        messageProcessingUnitOfWork: new SqliteMessageProcessingUnitOfWork(databasePath),
        offsetRepository: new SqliteProcessingOffsetRepository(databasePath),
        mentionDetector: new MentionDetector(new[] { "张三" }),
        projectClassifier: new ProjectClassifier(new[]
        {
            new ProjectRule(7, "验收项目", ProjectRuleType.ChatName, "分析群", 100)
        }),
        urgencyRanker: new UrgencyRanker(),
        messageRepository: messageRepository,
        analyzeMessageContent: () => analyzeMessageContent,
        analyzeMentionAsync: Analyze);

    // 默认关闭时必须保持零模型调用，同时仍创建原自动待办。
    var offResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-off", mentionContent) },
        CancellationToken.None);
    AssertEqual(1, offResult.CreatedTodoCount, "A disabled summary must not disable automatic Todo creation.");
    AssertEqual(0, analysisCalls.Count, "A disabled summary must not call the model.");

    // 不重建同一管线，直接打开内存开关验证立即生效。
    analyzeMessageContent = true;
    var onResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-on", mentionContent) },
        CancellationToken.None);
    AssertEqual(1, onResult.CreatedTodoCount, "An enabled summary should create exactly one Todo.");
    AssertEqual(1, analysisCalls.Count, "A new mention should invoke the summary once.");

    // 普通消息即使开启开关，也不能调用模型或创建待办。
    var normalResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-normal", "普通项目进展") },
        CancellationToken.None);
    AssertEqual(0, normalResult.CreatedTodoCount, "A non-mention must not create a Todo.");
    AssertEqual(1, analysisCalls.Count, "A non-mention must not invoke the model.");

    // 模型异常和空白结果分别验证两种默认说明回退路径。
    var failureResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-failure", mentionContent) },
        CancellationToken.None);
    var blankResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-blank", mentionContent) },
        CancellationToken.None);
    AssertEqual(1, failureResult.CreatedTodoCount, "A model failure must still create an automatic Todo.");
    AssertEqual(1, blankResult.CreatedTodoCount, "A blank summary must still create an automatic Todo.");

    // 只有模型成功且明确否决时才跳过待办，原始消息及分类评分仍正常入库。
    var skipResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-skip", "@所有人 本周版本已按计划发布") },
        CancellationToken.None);
    AssertEqual(1, skipResult.PersistedCount, "A skipped Todo must not prevent its source message from persisting.");
    AssertEqual(0, skipResult.CreatedTodoCount, "A model-approved skip must not create an automatic Todo.");

    // 重复消息仍由工作单元去重，但摘要前预检应阻止第二次模型调用。
    var callsBeforeDuplicate = analysisCalls.Count;
    var duplicateResult = await pipeline.ProcessAsync(
        new[] { Capture("summary-on", mentionContent) },
        CancellationToken.None);
    AssertEqual(1, duplicateResult.DuplicateCount, "The repeated source key should remain a duplicate.");
    AssertEqual(callsBeforeDuplicate, analysisCalls.Count, "A duplicate message must not invoke the model again.");

    var cancellationPropagated = false;
    try
    {
        await pipeline.ProcessAsync(
            new[] { Capture("summary-cancel", mentionContent) },
            cancellationSource.Token);
    }
    catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
    {
        cancellationPropagated = true;
    }

    AssertTrue(cancellationPropagated, "External cancellation during summarization must propagate.");
    var messages = await messageRepository.GetRecentAsync(20, CancellationToken.None);
    var todos = await todoRepository.GetPendingAsync(CancellationToken.None);
    AssertEqual(6, messages.Count, "All six non-duplicate, non-cancelled messages should persist, including the skipped Todo source.");
    AssertEqual(4, todos.Count, "Only the four model-approved or fallback mention messages should create Todos.");

    // 通过来源消息键定位待办，避免依赖仓储的排序规则。
    TodoItem TodoFor(string key)
    {
        var sourceMessage = messages.Single(message => message.SourceMessageKey == key);
        return todos.Single(todo => todo.SourceMessageId == sourceMessage.Id);
    }

    var offTodo = TodoFor("summary-off");
    var onTodo = TodoFor("summary-on");
    var failureTodo = TodoFor("summary-failure");
    var blankTodo = TodoFor("summary-blank");
    var defaultDescription = $"分析群 / 王工: {mentionContent}";
    AssertEqual(defaultDescription, offTodo.Description, "The disabled path should keep the default description.");
    AssertEqual("请今天确认验收时间。", onTodo.Description, "The enabled path should persist the trimmed summary.");
    AssertEqual(defaultDescription, failureTodo.Description, "A model exception should fall back to the default description.");
    AssertEqual(defaultDescription, blankTodo.Description, "A blank summary should fall back to the default description.");
    AssertEqual(PriorityLevel.P2, offTodo.Priority, "The disabled path should keep local priority calculation.");
    AssertEqual(PriorityLevel.P1, onTodo.Priority, "The Agent priority should override only the automatic Todo.");
    AssertEqual(PriorityLevel.P2, failureTodo.Priority, "A model exception should fall back to the local priority.");
    AssertEqual(PriorityLevel.P0, blankTodo.Priority, "A blank summary must not discard the Agent priority.");

    // Agent 只能覆盖优先级、说明和时间提醒，标题及项目仍由原规则生成。
    AssertEqual(offTodo.Title, onTodo.Title, "Agent analysis must not change the Todo title.");
    AssertEqual(offTodo.ProjectId, onTodo.ProjectId, "Agent analysis must not change the Todo project.");
    AssertEqual(offTodo.ManagedProjectId, onTodo.ManagedProjectId, "Agent analysis must not change the managed project.");
    AssertEqual(
        sentAt.Date.AddHours(18),
        onTodo.DueAt,
        "An analyzed completion time should override the local due-date rule.");
    AssertTrue(onTodo.IsAutoCreated, "The analyzed Todo should remain automatic.");

    var reminderRepository = new SqliteReminderRepository(databasePath);
    var offReminders = await reminderRepository.GetForTodoAsync(offTodo.Id, CancellationToken.None);
    var onReminders = await reminderRepository.GetForTodoAsync(onTodo.Id, CancellationToken.None);
    AssertEqual(1, offReminders.Count, "An unanalyzed automatic Todo should still create its rule-based reminder.");
    AssertEqual(offTodo.DueAt - TimeSpan.FromHours(1), offReminders.Single().ScheduledAt,
        "The fallback reminder should reserve one hour before the send-day deadline.");
    AssertEqual(1, onReminders.Count, "An analyzed automatic Todo should create exactly one reminder.");
    AssertEqual(
        sentAt.Date.AddHours(17).AddMinutes(30),
        onReminders.Single().ScheduledAt,
        "An explicit Agent reminder time should override the default lead.");

    // 消息流继续读取本地 urgency_scores，不能使用自动待办的 Agent 优先级。
    var analyzedMessageIds = messages
        .Where(message => message.SourceMessageKey is "summary-on" or "summary-blank" or "summary-skip")
        .Select(message => message.Id)
        .ToArray();
    var messagePriorities = await messageRepository
        .GetEffectivePrioritiesAsync(analyzedMessageIds, CancellationToken.None);
    AssertEqual(PriorityLevel.P2,
        messagePriorities[messages.Single(message => message.SourceMessageKey == "summary-on").Id],
        "The Agent P1 must not replace the local P2 message priority.");
    AssertEqual(PriorityLevel.P2,
        messagePriorities[messages.Single(message => message.SourceMessageKey == "summary-blank").Id],
        "The Agent P0 must not replace the local P2 message priority.");
    var skippedMessage = messages.Single(message => message.SourceMessageKey == "summary-skip");
    // 跳过消息没有“今天”等本地加分词，因此应保留本地 P3，而非统一按其他消息断言。
    AssertEqual(PriorityLevel.P3, messagePriorities[skippedMessage.Id],
        "A skipped Todo must keep its independent local message priority.");
    var normalMessage = messages.Single(message => message.SourceMessageKey == "summary-normal");
    AssertFalse(todos.Any(todo => todo.SourceMessageId == normalMessage.Id),
        "The non-mention message should not have a Todo.");
    AssertFalse(todos.Any(todo => todo.SourceMessageId == skippedMessage.Id),
        "The rejected message should persist without any automatic Todo.");
    AssertFalse(messages.Any(message => message.SourceMessageKey == "summary-cancel"),
        "A cancelled message should not enter the database transaction.");
}

static async Task TestCapturePipelineReportsIndividualSourceFailuresAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var healthyMessage = new CapturedMessage(
        "Shihuatong",
        "healthy-1",
        "chat-1",
        "测试群",
        "Alice",
        "正常消息",
        DateTimeOffset.Now,
        MessageType.Text);
    var failing = new StubCaptureAdapter(
        "WeChat.LocalDatabase",
        (_, _) => Task.FromException<CaptureBatch>(new WeChatReaderException(
            WeChatReaderStage.KeyValidation,
            WeChatReaderErrorCode.KeyInvalid,
            "微信未初始化 " + new string('a', 64))));
    var healthy = new StubCaptureAdapter(
        "Shihuatong.LocalDatabase",
        (_, _) => Task.FromResult(new CaptureBatch(
            "Shihuatong.LocalDatabase",
            new[] { healthyMessage },
            "healthy-offset")));
    var offsetRepository = new SqliteProcessingOffsetRepository(databasePath);
    var pipeline = new MessageCapturePipeline(
        new IMessageCaptureAdapter[] { failing, healthy },
        new SqliteMessageProcessingUnitOfWork(databasePath),
        offsetRepository,
        new MentionDetector(Array.Empty<string>()),
        new ProjectClassifier(Array.Empty<ProjectRule>()),
        new UrgencyRanker());

    var result = await pipeline.RunOnceAsync(CancellationToken.None);
    var offsets = await offsetRepository.GetAllAsync(CancellationToken.None);

    AssertEqual(1, result.CapturedCount, "Healthy sources must continue after a WeChat failure.");
    AssertEqual(1, result.PersistedCount, "Healthy source messages must still be persisted.");
    AssertEqual(2, result.Sources.Count, "Every enabled source must have an independent result.");
    var failed = result.Sources.Single(source => source.AdapterName == "WeChat.LocalDatabase");
    AssertEqual(CaptureSourceRunStatus.Failed, failed.Status, "Uninitialized WeChat must be reported as a failed source.");
    AssertEqual("KeyValidation", failed.Stage, "WeChat failure must retain its stable stage.");
    AssertEqual("KeyInvalid", failed.ErrorCode, "WeChat failure must retain its stable error code.");
    AssertFalse(failed.SanitizedError!.Contains(new string('a', 64), StringComparison.Ordinal), "Source diagnostics must redact key-shaped text.");
    AssertFalse(offsets.ContainsKey("WeChat.LocalDatabase"), "A failed source must not advance its offset.");
    AssertEqual("healthy-offset", offsets["Shihuatong.LocalDatabase"], "A successful source must advance only its own offset.");

    var allFailedPipeline = new MessageCapturePipeline(
        new IMessageCaptureAdapter[]
        {
            failing,
            new StubCaptureAdapter("Shihuatong.LocalDatabase", (_, _) =>
                Task.FromException<CaptureBatch>(new InvalidOperationException("source unavailable")))
        },
        new SqliteMessageProcessingUnitOfWork(databasePath),
        offsetRepository,
        new MentionDetector(Array.Empty<string>()),
        new ProjectClassifier(Array.Empty<ProjectRule>()),
        new UrgencyRanker());
    var allFailed = await allFailedPipeline.RunOnceAsync(CancellationToken.None);
    AssertTrue(allFailed.AllSourcesFailed, "The aggregate result must distinguish all-source failure from partial failure.");
}

static async Task TestLiveWeChatLocalExportCapturePipelineAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var captureRoot = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    var localExportRoot = Path.Combine(captureRoot, "WeChatLocalExport");
    Directory.CreateDirectory(localExportRoot);
    await File.WriteAllLinesAsync(Path.Combine(localExportRoot, "wechat-local-export.jsonl"), new[]
    {
        """{"msgId":"local-mention","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 紧急，今天处理线上故障","createTime":"2026-06-05T11:00:00+08:00","msgType":"Text"}"""
    }, CancellationToken.None);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var offsetRepository = new SqliteProcessingOffsetRepository(databasePath);
    var sources = CaptureAdapterFactory.CreateDefaultLiveSources(captureRoot)
        .Where(source => source.Kind != CaptureSourceKind.WeChatLocalDatabase &&
                         source.Kind != CaptureSourceKind.ShihuatongLocalDatabase)
        .ToArray();
    var pipeline = new MessageCapturePipeline(
        adapters: CaptureAdapterFactory.CreateAdapters(sources),
        new SqliteMessageProcessingUnitOfWork(databasePath),
        offsetRepository,
        new MentionDetector(new[] { "张三", "zhangsan" }),
        new ProjectClassifier(new[]
        {
            new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100)
        }),
        new UrgencyRanker(priorityContacts: new[] { "王经理" }, priorityProjectIds: new[] { 1L }));

    var firstRun = await pipeline.RunOnceAsync(CancellationToken.None);
    var secondRun = await pipeline.RunOnceAsync(CancellationToken.None);
    var recentMessages = await messageRepository.GetRecentAsync(10, CancellationToken.None);
    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);

    AssertEqual(1, firstRun.CapturedCount, "Live WeChat local export source should capture the local mention.");
    AssertEqual(1, firstRun.PersistedCount, "Local mention should be persisted.");
    AssertEqual(1, firstRun.CreatedTodoCount, "Local mention should create a todo.");
    AssertEqual(0, secondRun.CapturedCount, "Live WeChat local export offset should prevent duplicates.");
    AssertEqual("WeChat", recentMessages.Single().Source, "Persisted local message should keep WeChat source.");
    AssertEqual("CRM项目群", recentMessages.Single().ChatName, "Persisted local message should use local chat name.");
    AssertEqual(1, pendingTodos.Count, "One pending todo should be created from @我.");
}

static async Task TestModelSettingsRepositoryAsync()
{
    // 每个用例使用独立数据库，避免开发者本机设置影响测试结果。
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
    var repository = new SqliteModelSettingsRepository(databasePath);

    // 空库没有配置记录，必须默认关闭以避免意外模型费用。
    AssertFalse(await repository.GetAnalyzeMessageContentAsync(CancellationToken.None),
        "A fresh database should disable message analysis.");
    await repository.SetAnalyzeMessageContentAsync(true, CancellationToken.None);

    // 使用新仓储实例回读，确认状态来自 SQLite 而不是内存缓存。
    var reloadedRepository = new SqliteModelSettingsRepository(databasePath);
    AssertTrue(await reloadedRepository.GetAnalyzeMessageContentAsync(CancellationToken.None),
        "An enabled analysis switch should round-trip through SQLite.");
    await reloadedRepository.SetAnalyzeMessageContentAsync(false, CancellationToken.None);
    AssertFalse(await repository.GetAnalyzeMessageContentAsync(CancellationToken.None),
        "A disabled analysis switch should overwrite the previously enabled value.");

    // 直接写入非法值，验证读取端采用安全关闭而不是抛出或意外开启。
    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync(CancellationToken.None);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO app_settings (key, value) VALUES ('model_analyze_message_content', 'invalid')
        ON CONFLICT(key) DO UPDATE SET value = 'invalid';
        """;
    await command.ExecuteNonQueryAsync(CancellationToken.None);
    AssertFalse(await repository.GetAnalyzeMessageContentAsync(CancellationToken.None),
        "An invalid persisted value should safely disable message analysis.");
}

static async Task TestCaptureSourceSettingsRepositoryAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var repository = new SqliteCaptureSourceSettingsRepository(databasePath);

    var initialSettings = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(0, initialSettings.Count, "Fresh database should have no saved source settings.");

    var now = DateTimeOffset.Now;
    var settings = new[]
    {
        new CaptureSourceSettings(0, "WeChat", "微信", "JsonlDirectory", "/capture/WeChat", true, now, now),
        new CaptureSourceSettings(0, "WeChat", "微信本地导出", "WeChatLocalExport", "/capture/WeChatLocalExport", true, now, now),
        new CaptureSourceSettings(0, "WeChat", "微信可见窗口", "WindowText", "/capture/WeChatWindow", false, now, now)
    };

    await repository.SaveAllAsync(settings, CancellationToken.None);

    var loaded = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(3, loaded.Count, "All saved source settings should round-trip.");
    AssertTrue(loaded.Any(s => s.Source == "WeChat" && s.Kind == "WindowText" && !s.IsEnabled), "WindowText should be saved as disabled.");
    AssertTrue(loaded.Any(s => s.Source == "WeChat" && s.Kind == "JsonlDirectory" && s.IsEnabled), "JsonlDirectory should be saved as enabled.");

    var windowText = loaded.First(s => s.Kind == "WindowText");
    await repository.SaveAsync(windowText with { IsEnabled = true }, CancellationToken.None);

    var reloaded = await repository.GetAllAsync(CancellationToken.None);
    AssertTrue(reloaded.First(s => s.Kind == "WindowText").IsEnabled, "Updated setting should reflect enable change.");

    await repository.SaveAsync(
        new CaptureSourceSettings(0, "WeChat", "旧微信命令", "WeChatLocalCommand", "legacy-reader", false, now, now),
        CancellationToken.None);
    await repository.MigrateKindAsync(
        "WeChat",
        "WeChatLocalCommand",
        "WeChatLocalDatabase",
        "微信本地数据库",
        "原生 C#（只读本地数据库）",
        CancellationToken.None);
    await repository.MigrateKindAsync(
        "WeChat",
        "WeChatLocalCommand",
        "WeChatLocalDatabase",
        "微信本地数据库",
        "原生 C#（只读本地数据库）",
        CancellationToken.None);
    var migrated = await repository.GetAllAsync(CancellationToken.None);
    AssertFalse(migrated.Any(s => s.Kind == "WeChatLocalCommand"), "Legacy command settings should be removed after migration.");
    AssertFalse(migrated.Single(s => s.Kind == "WeChatLocalDatabase").IsEnabled, "Migration should preserve the legacy enabled flag.");

    await repository.DeleteAllAsync(CancellationToken.None);
    var afterDelete = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(0, afterDelete.Count, "All settings should be removable.");
}

static async Task TestCapturePipelineWithSavedSettingsAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var captureRoot = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    var localExportRoot = Path.Combine(captureRoot, "WeChatLocalExport");
    Directory.CreateDirectory(localExportRoot);
    await File.WriteAllLinesAsync(Path.Combine(localExportRoot, "export.jsonl"), new[]
    {
        """{"msgId":"m1","chatId":"crm","chatName":"CRM项目群","senderName":"王经理","content":"@张三 紧急处理","createTime":"2026-06-05T11:00:00+08:00","msgType":"Text"}"""
    }, CancellationToken.None);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var messageRepository = new SqliteMessageRepository(databasePath);
    var todoRepository = new SqliteTodoRepository(databasePath);
    var offsetRepository = new SqliteProcessingOffsetRepository(databasePath);
    var settingsRepository = new SqliteCaptureSourceSettingsRepository(databasePath);

    var now = DateTimeOffset.Now;
    var savedSettings = new[]
    {
        new CaptureSourceSettings(0, "WeChat", "微信", "JsonlDirectory", captureRoot, true, now, now),
        new CaptureSourceSettings(0, "WeChat", "微信本地导出", "WeChatLocalExport", localExportRoot, true, now, now)
    };
    await settingsRepository.SaveAllAsync(savedSettings, CancellationToken.None);

    var defaultSources = CaptureAdapterFactory.CreateDefaultLiveSources(captureRoot)
        .Where(source => source.Kind != CaptureSourceKind.WeChatLocalDatabase &&
                         source.Kind != CaptureSourceKind.ShihuatongLocalDatabase)
        .ToArray();
    var effectiveSources = defaultSources.Select(source =>
    {
        var saved = savedSettings.FirstOrDefault(s =>
            s.Source == source.Source && s.Kind == source.Kind.ToString());
        return saved is null ? source : source with { IsEnabled = saved.IsEnabled };
    }).ToArray();

    var pipeline = new MessageCapturePipeline(
        adapters: CaptureAdapterFactory.CreateAdapters(effectiveSources),
        new SqliteMessageProcessingUnitOfWork(databasePath),
        offsetRepository,
        new MentionDetector(new[] { "张三" }),
        new ProjectClassifier(new[]
        {
            new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100)
        }),
        new UrgencyRanker(priorityContacts: new[] { "王经理" }, priorityProjectIds: new[] { 1L }));

    var firstRun = await pipeline.RunOnceAsync(CancellationToken.None);

    AssertEqual(1, firstRun.CapturedCount, "Pipeline should use saved settings to capture from local export.");
    AssertEqual(1, firstRun.PersistedCount, "Message should be persisted through saved settings.");
    AssertEqual(1, firstRun.CreatedTodoCount, "Mention should create todo through saved settings.");
}

static async Task TestUserAliasRepositoryAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

    var repository = new SqliteUserAliasRepository(databasePath);

    var initial = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(0, initial.Count, "Fresh database should have no user aliases.");

    var saved = await repository.SaveAsync("白驹过隙", CancellationToken.None);
    AssertTrue(saved.Id > 0, "Saved alias should get a database id.");
    await repository.SaveAsync("戴少峰", CancellationToken.None);

    var loaded = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(2, loaded.Count, "Both saved aliases should round-trip.");
    AssertTrue(loaded.Any(a => a.Alias == "白驹过隙"), "First alias should be present.");
    AssertTrue(loaded.Any(a => a.Alias == "戴少峰"), "Second alias should be present.");
    AssertTrue(loaded.All(a => a.IsActive), "Loaded aliases should be active.");

    var detector = new MentionDetector(loaded.Select(a => a.Alias));
    AssertTrue(detector.IsMentioned("@白驹过隙 今天处理线上故障"), "Configured alias should be detected as mention.");
    AssertFalse(detector.IsMentioned("@张三 帮忙看一下"), "Unknown alias should not be detected.");

    await repository.SaveAsync("白驹过隙", CancellationToken.None);
    var stillTwo = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(2, stillTwo.Count, "Saving a duplicate alias should not create a new row.");

    await repository.DeleteAsync(loaded.First(a => a.Alias == "戴少峰").Id, CancellationToken.None);
    var afterDelete = await repository.GetAllAsync(CancellationToken.None);
    AssertEqual(1, afterDelete.Count, "Deleted alias should no longer be returned.");
    AssertEqual("白驹过隙", afterDelete.Single().Alias, "Remaining alias should be the one not deleted.");
}

static async Task TestFollowedProjectKeywordRepositoryAsync()
{
    var databasePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);

    var projectRepository = new SqliteFollowedProjectRepository(databasePath);
    var keywordRepository = new SqliteFollowedProjectKeywordRepository(databasePath);
    var project = await projectRepository.SaveAsync("智能管线", ProjectColorPolicy.Purple, CancellationToken.None);
    await keywordRepository.SaveAsync(project.Id, "管线提升", CancellationToken.None);
    await keywordRepository.SaveAsync(project.Id, "WMS联调", CancellationToken.None);
    await keywordRepository.SaveAsync(project.Id, "wms联调", CancellationToken.None);

    var keywords = await keywordRepository.GetAllAsync(CancellationToken.None);
    AssertEqual(2, keywords.Count, "Duplicate keywords for the same project should not create another row.");
    AssertTrue(keywords.All(keyword => keyword.ProjectName == "智能管线"), "All keywords should retain their owning project name.");
    AssertEqual(ProjectColorPolicy.Purple, project.Color, "A new project should retain its selected fixed-palette color.");

    var duplicate = await projectRepository.SaveAsync("智能管线", ProjectColorPolicy.Red, CancellationToken.None);
    AssertEqual(ProjectColorPolicy.Purple, duplicate.Color, "Re-saving an existing project must not silently recolor it.");
    var rejectedUnsupportedColor = false;
    try
    {
        await projectRepository.SaveAsync("不可读颜色项目", "#FFFFFF", CancellationToken.None);
    }
    catch (ArgumentException)
    {
        rejectedUnsupportedColor = true;
    }
    AssertTrue(rejectedUnsupportedColor, "Project colors outside the fixed readable palette must be rejected.");

    AssertEqual(
        UpdateFollowedProjectColorOutcome.Updated,
        await projectRepository.UpdateColorAsync(project.Id, ProjectColorPolicy.Green, CancellationToken.None),
        "An existing project's fixed-palette color should be updated by stable project id.");
    AssertEqual(
        ProjectColorPolicy.Green,
        (await projectRepository.GetAllAsync(CancellationToken.None)).Single(item => item.Id == project.Id).Color,
        "Updated project color should round-trip through a fresh repository read.");
    AssertEqual(
        UpdateFollowedProjectColorOutcome.Unchanged,
        await projectRepository.UpdateColorAsync(project.Id, ProjectColorPolicy.Green, CancellationToken.None),
        "Selecting the persisted color again should be an explicit no-op.");
    AssertEqual(
        UpdateFollowedProjectColorOutcome.Missing,
        await projectRepository.UpdateColorAsync(long.MaxValue, ProjectColorPolicy.Blue, CancellationToken.None),
        "Updating a deleted or missing project should return a recoverable business result.");
    var rejectedInvalidUpdate = false;
    try
    {
        await projectRepository.UpdateColorAsync(project.Id, "#FFFFFF", CancellationToken.None);
    }
    catch (ArgumentException)
    {
        rejectedInvalidUpdate = true;
    }
    AssertTrue(rejectedInvalidUpdate, "Project color updates must reject values outside the fixed palette.");
    AssertEqual(
        ProjectColorPolicy.Green,
        (await projectRepository.GetAllAsync(CancellationToken.None)).Single(item => item.Id == project.Id).Color,
        "A rejected color update must preserve the previously persisted color.");

    var classifier = new ProjectClassifier(keywords.Select(keyword =>
        new ProjectRule(keyword.ProjectId, keyword.ProjectName, ProjectRuleType.Keyword, keyword.Keyword, 170)));
    var result = classifier.Classify(CreateMessage(100, "实施群", "王工", "请确认 WMS联调 计划"));
    AssertEqual(project.Id, result.ProjectId, "Any keyword of one project should classify the message into that project.");
    AssertEqual("智能管线", result.ProjectName, "Keyword classification should return the shared project name.");

    var shorterProject = await projectRepository.SaveAsync("短关键字", ProjectColorPolicy.Blue, CancellationToken.None);
    await keywordRepository.SaveAsync(shorterProject.Id, "WMS", CancellationToken.None);
    keywords = await keywordRepository.GetAllAsync(CancellationToken.None);
    var matched = FollowedProjectMatcher.Match(
        CreateMessage(101, "实施群", "王工", "请确认 WMS联调 计划"),
        keywords);
    AssertEqual(project.Id, matched!.ProjectId, "Followed-project matching should preserve the existing longest-keyword-first rule.");

    await keywordRepository.DeleteByProjectIdAsync(project.Id, CancellationToken.None);
    var remainingKeywords = await keywordRepository.GetAllAsync(CancellationToken.None);
    AssertFalse(remainingKeywords.Any(item => item.ProjectId == project.Id), "Deleting a project keyword set should remove all keywords owned by that project.");
    AssertTrue(remainingKeywords.Any(item => item.ProjectId == shorterProject.Id), "Deleting one project's keywords must preserve other projects' keyword rules.");
}

static async Task TestProjectColorSettingsXamlAsync()
{
    var xamlPath = Path.Combine(ProjectToolPaths.ProjectRoot, "src", "WechatDashboard.App", "MainWindow.xaml");
    var xaml = await File.ReadAllTextAsync(xamlPath, CancellationToken.None);
    AssertTrue(xaml.Contains("x:Name=\"NewFollowedProjectColorComboBox\"", StringComparison.Ordinal), "Project creation should retain its fixed-palette color selector.");
    AssertTrue(xaml.Contains("ItemsSource=\"{Binding DataContext.ProjectColors, RelativeSource={RelativeSource AncestorType=Window}}\"", StringComparison.Ordinal), "The project list color editor should use the shared fixed palette.");
    AssertTrue(xaml.Contains("SelectedValue=\"{Binding Color, Mode=OneWay}\"", StringComparison.Ordinal), "The color dropdown should display the persisted row color without mutating the row before persistence succeeds.");
    AssertTrue(xaml.Contains("SelectionChanged=\"FollowedProjectColorComboBox_SelectionChanged\"", StringComparison.Ordinal), "The project color dropdown should save selection changes immediately.");
    AssertFalse(xaml.Contains("<TextBlock Text=\"{Binding Color}\" Foreground=\"{Binding Color}\"", StringComparison.Ordinal), "The project color column should no longer render a non-editable color code.");

    var pendingXamlPath = Path.Combine(ProjectToolPaths.ProjectRoot, "src", "WechatDashboard.App", "Views", "Todos", "TodoPendingView.xaml");
    var pendingXaml = await File.ReadAllTextAsync(pendingXamlPath, CancellationToken.None);
    AssertTrue(
        pendingXaml.Contains("Text=\"{Binding ProjectName, Mode=OneWay}\" Foreground=\"{Binding ProjectColor, Mode=OneWay}\"", StringComparison.Ordinal),
        "The pending Todo project name should bind its own text foreground directly to the latest project color.");
}

static async Task TestNativeWeChatKeyDerivationAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "native-reader", Guid.NewGuid().ToString("N"));
    var databaseRoot = Path.Combine(root, "db_storage");
    var snapshotRoot = Path.Combine(root, "snapshots");
    var masterKey = Enumerable.Range(1, WeChatDatabaseKeyLease.KeyLength).Select(value => (byte)value).ToArray();
    try
    {
        await CreateEncryptedWeChatFixtureAsync(databaseRoot, masterKey);
        var contactPath = Path.Combine(databaseRoot, "contact", "contact.db");
        await using var walConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = contactPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await walConnection.OpenAsync();
        AssertEqual(
            SQLitePCL.raw.SQLITE_OK,
            SQLitePCL.raw.sqlite3_key(walConnection.Handle, masterKey),
            "SQLCipher should accept the key before creating WAL content.");
        ExecuteSql(walConnection, "PRAGMA journal_mode=WAL;");
        ExecuteSql(walConnection, "PRAGMA wal_autocheckpoint=0;");
        ExecuteSql(walConnection, "INSERT INTO contact VALUES ('wxid_wal', 'WAL联系人', '');");
        AssertTrue(File.Exists(contactPath + "-wal"), "Fixture should retain an uncheckpointed encrypted WAL file.");

        var discovery = new WeChatDatabaseDiscovery();
        var databases = discovery.Discover(databaseRoot);
        AssertTrue(databases.Any(item => item.RelativePath == "session/session.db"), "Session database should be discovered.");
        AssertTrue(databases.Any(item => item.RelativePath == "contact/contact.db"), "Contact database should be discovered.");
        AssertTrue(databases.Any(item => item.RelativePath == "message/message_0.db"), "Message shard should be discovered.");

        var sourceMetadata = databases.ToDictionary(
            item => item.FullPath,
            item => (item.Length, File.GetLastWriteTimeUtc(item.FullPath)));
        var snapshot = await new WeChatDatabaseSnapshotService(snapshotRoot)
            .CreateAsync(databases, CancellationToken.None);
        AssertEqual(3, snapshot.Databases.Count, "Snapshot should contain the three required encrypted databases.");
        AssertTrue(
            File.Exists(Path.Combine(snapshot.RootPath, "contact", "contact.db-wal")),
            "Snapshot should copy the encrypted WAL with its database.");
        foreach (var source in sourceMetadata)
        {
            AssertEqual(source.Value.Length, new FileInfo(source.Key).Length, "Snapshot must not change source length.");
            AssertEqual(source.Value.Item2, File.GetLastWriteTimeUtc(source.Key), "Snapshot must not change source timestamp.");
        }

        using var lease = new WeChatDatabaseKeyLease(masterKey);
        using (var snapshotConnection = new SqlCipherDatabaseAccessor().OpenReadOnly(
                   Path.Combine(snapshot.RootPath, "contact", "contact.db"),
                   lease))
        using (var walCommand = snapshotConnection.CreateCommand())
        {
            walCommand.CommandText = "SELECT COUNT(*) FROM contact WHERE username = 'wxid_wal';";
            AssertEqual(1L, (long)walCommand.ExecuteScalar()!, "Read-only SQLCipher access should see rows copied from WAL.");
        }
        using var keys = await new WeChatDatabaseKeyDeriver().DeriveAsync(
            lease,
            snapshot.Databases,
            CancellationToken.None);
        AssertEqual(3, keys.RelativePaths.Count, "All required database keys should validate.");
        var derivedFixtureKey = Enumerable.Range(65, WeChatDatabaseKeyDeriver.KeySize)
            .Select(value => (byte)value)
            .ToArray();
        var reserve64Page = CreateV4PageFixture(derivedFixtureKey, reserveSize: 64);
        var reserve80Page = CreateV4PageFixture(derivedFixtureKey, reserveSize: 80);
        AssertTrue(WeChatDatabaseKeyDeriver.VerifyPage(derivedFixtureKey, reserve64Page), "Reserve-64 V4 pages should validate.");
        AssertTrue(WeChatDatabaseKeyDeriver.VerifyPage(derivedFixtureKey, reserve80Page), "Reserve-80 V4 pages should validate.");
        reserve80Page[128] ^= 0x5A;
        AssertFalse(WeChatDatabaseKeyDeriver.VerifyPage(derivedFixtureKey, reserve80Page), "Modified encrypted pages must fail HMAC validation.");
        CryptographicOperations.ZeroMemory(derivedFixtureKey);
        CryptographicOperations.ZeroMemory(reserve64Page);
        CryptographicOperations.ZeroMemory(reserve80Page);

        var invalidKey = masterKey.Select(value => (byte)(value ^ 0x5A)).ToArray();
        using var invalidLease = new WeChatDatabaseKeyLease(invalidKey);
        var rejected = false;
        try
        {
            using var ignored = await new WeChatDatabaseKeyDeriver().DeriveAsync(
                invalidLease,
                snapshot.Databases,
                CancellationToken.None);
        }
        catch (WeChatReaderException exception)
        {
            rejected = exception.Stage == WeChatReaderStage.KeyValidation &&
                       exception.ErrorCode == WeChatReaderErrorCode.KeyInvalid;
        }

        AssertTrue(rejected, "An invalid master key should fail at key validation.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(masterKey);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task TestNativeWeChatReaderAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "native-reader", Guid.NewGuid().ToString("N"));
    var databaseRoot = Path.Combine(root, "db_storage");
    var snapshotRoot = Path.Combine(root, "snapshots");
    var statePath = Path.Combine(root, "state.json");
    var masterKey = Enumerable.Range(33, WeChatDatabaseKeyLease.KeyLength).Select(value => (byte)value).ToArray();
    try
    {
        await CreateEncryptedWeChatFixtureAsync(databaseRoot, masterKey);
        var provider = new FakeWeChatDatabaseKeyProvider(masterKey);
        await using var nativeReader = new NativeWeChatLocalDatabaseReader(
            provider,
            snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
            stateStore: new WeChatReaderStateStore(statePath));
        var initialized = await nativeReader.InitializeAsync(
            new WeChatInitializationRequest(databaseRoot, 1234, TimeSpan.FromSeconds(5)),
            CancellationToken.None);
        AssertTrue(initialized.Succeeded, "Native reader should initialize against encrypted fixtures.");
        AssertTrue(nativeReader.IsInitialized, "Native reader should retain an in-memory key lease after initialization.");
        AssertFalse(Directory.EnumerateDirectories(snapshotRoot).Any(), "Initialization snapshots should be deleted after validation.");
        AssertEqual(1234, provider.LastRequest?.ProcessId ?? 0, "Native reader should pass the requested process id to KeyProbe provider.");

        var start = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);
        var end = DateTimeOffset.FromUnixTimeSeconds(1_780_001_000);
        var page = await nativeReader.ReadAsync(
            new WeChatReadRequest(start, end, 0, 2),
            CancellationToken.None);
        AssertEqual(3, page.TotalCount, "Native reader should count all matching shard messages before paging.");
        AssertEqual(2, page.Messages.Count, "Native reader should apply page size after stable sorting.");
        AssertTrue(page.Messages[0].SentAt >= page.Messages[1].SentAt, "Native messages should be newest first.");

        var all = await nativeReader.ReadAsync(
            new WeChatReadRequest(start, end, 0, 200),
            CancellationToken.None);
        AssertTrue(all.Messages.Any(message => message.Content == "[图片]"), "Image XML should use the established summary.");
        AssertTrue(all.Messages.Any(message => message.Content == "压缩消息"), "Zstd message content should be decoded in C#.");
        AssertFalse(Directory.EnumerateDirectories(snapshotRoot).Any(), "Read snapshots should be deleted after the query.");
        var contentDecoder = new WeChatMessageContentDecoder();
        var dtdPayload = "<!DOCTYPE msg [<!ENTITY xxe SYSTEM 'file:///not-allowed'>]><msg>&xxe;</msg>";
        AssertEqual("[图片]", contentDecoder.Summarize(dtdPayload, 3), "DTD content must be rejected without returning the raw payload.");
        AssertEqual("[图片]", contentDecoder.Summarize("<msg><img>", 3), "Malformed XML should degrade to a safe type summary.");
        const string referencedContent = "项目周会\n时间：2026/10/28 09:00-12:00\n参会人：张三 & 李工";
        const string referencedReply =
            "<msg><appmsg><title>本周三请参加会议</title><type>57</type>" +
            "<refermsg><type>1</type><content>项目周会\n时间：2026/10/28 09:00-12:00\n参会人：张三 &amp; 李工</content></refermsg>" +
            "</appmsg></msg>";
        // 引用回复应保留当前标题，并由 XML 解码器还原文本实体后追加完整引用。
        AssertEqual("[链接] 本周三请参加会议\n\n引用消息：\n" + referencedContent, contentDecoder.Summarize(referencedReply, 49), "WeChat refermsg content should follow the current reply.");
        // 没有 refermsg 时只返回当前回复，不添加空引用标记。
        AssertEqual("[链接] 无引用回复", contentDecoder.Summarize("<msg><appmsg><title>无引用回复</title><type>57</type></appmsg></msg>", 49), "A missing refermsg should leave the reply unchanged.");
        // 空白引用正文不应产生多余分隔符或引用标题。
        AssertEqual("[链接] 空引用回复", contentDecoder.Summarize("<msg><appmsg><title>空引用回复</title><type>57</type><refermsg><content>  </content></refermsg></appmsg></msg>", 49), "A blank refermsg should be ignored.");
        // 当前摘要已含相同正文时不重复拼接，避免模型收到重复内容。
        AssertEqual("[链接] 项目周会", contentDecoder.Summarize("<msg><appmsg><title>项目周会</title><type>57</type><refermsg><content>项目周会</content></refermsg></appmsg></msg>", 49), "An already included reference should not be duplicated.");
        // 非引用类型即使带有 refermsg 节点，也必须保持既有摘要规则。
        AssertEqual("[链接] 普通链接 - 说明 https://example.test", contentDecoder.Summarize("<msg><appmsg><title>普通链接</title><des>说明</des><type>5</type><url>https://example.test</url><refermsg><content>不应合并</content></refermsg></appmsg></msg>", 49), "Ordinary links must not merge refermsg content.");
        AssertEqual("[文件]", contentDecoder.Summarize("<msg><appmsg><title>文件</title><type>6</type><refermsg><content>不应合并</content></refermsg></appmsg></msg>", 49), "File messages must retain their existing summary.");
        AssertEqual("普通文本", contentDecoder.Summarize("  普通文本  ", 1), "Plain text should only be trimmed.");
        AssertEqual("", contentDecoder.DecodeContent(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00 }, 1), "Corrupt zstd should fail closed.");
        var mention = all.Messages.Single(message => message.Content.Contains("@张三", StringComparison.Ordinal));
        AssertEqual("Alice", mention.SenderName, "Name2Id and contact display names should resolve the group sender.");
        AssertTrue(mention.SourceMessageKey.StartsWith("WeChat:local:room@chatroom:message_0.db:", StringComparison.Ordinal), "Source key should remain stable and compatible.");

        var adapter = new WeChatLocalDatabaseCaptureAdapter(nativeReader);
        var batch = await adapter.CaptureAsync(
            new CaptureContext(new Dictionary<string, string> { [adapter.Name] = "0" }),
            CancellationToken.None);
        AssertEqual(3, batch.Messages.Count, "Native adapter should capture all fixture messages from offset zero.");
        var captureOffset = WeChatCaptureOffset.Parse(batch.NextOffset);
        AssertTrue(long.Parse(captureOffset.HighWatermark, CultureInfo.InvariantCulture) >= 1_780_000_300, "Native adapter should return a high-water timestamp.");
        AssertTrue(!string.IsNullOrWhiteSpace(captureOffset.SourceFingerprint), "Native adapter should persist a source fingerprint.");
        var idleBatch = await adapter.CaptureAsync(
            new CaptureContext(new Dictionary<string, string> { [adapter.Name] = batch.NextOffset }),
            CancellationToken.None);
        AssertEqual(0, idleBatch.Messages.Count, "An unchanged source fingerprint should take the zero-message fast path.");
        AssertEqual(
            0L,
            nativeReader.Diagnostics.Last().Counts?["shards_scanned"] ?? -1,
            "The idle fast path must not reopen encrypted message shards.");

        var stateText = await File.ReadAllTextAsync(statePath);
        AssertFalse(stateText.Contains(Convert.ToHexString(masterKey), StringComparison.OrdinalIgnoreCase), "Reader state must not contain the database key.");
        AssertFalse(stateText.Contains("all_keys", StringComparison.OrdinalIgnoreCase), "Reader state must not reference legacy plaintext key storage.");

        await using var failingReader = new NativeWeChatLocalDatabaseReader(
            new ThrowingWeChatDatabaseKeyProvider(
                new TimeoutException("probe timed out " + new string('a', 64))),
            snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
            stateStore: new WeChatReaderStateStore(Path.Combine(root, "failed-state.json")));
        var failedInitialization = await failingReader.InitializeAsync(
            new WeChatInitializationRequest(databaseRoot, 1234, TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        var failedStage = failedInitialization.Stages.Last();
        AssertFalse(failedInitialization.Succeeded, "A KeyProbe timeout must fail initialization.");
        AssertEqual(WeChatReaderStage.KeyProbe, failedStage.Stage, "Timeout should retain the KeyProbe stage.");
        AssertEqual(WeChatReaderErrorCode.KeyTimeout, failedStage.ErrorCode, "Timeout should use the stable KeyTimeout code.");
        AssertFalse(failedStage.SanitizedMessage.Contains(new string('a', 64), StringComparison.Ordinal), "Diagnostics must redact key-shaped text.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(masterKey);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task TestNativeWeChatReaderMatchesPythonOracleAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "native-oracle", Guid.NewGuid().ToString("N"));
    var encryptedRoot = Path.Combine(root, "encrypted", "db_storage");
    var plainRoot = Path.Combine(root, "plain");
    var snapshotRoot = Path.Combine(root, "snapshots");
    var masterKey = Enumerable.Range(97, WeChatDatabaseKeyLease.KeyLength).Select(value => (byte)value).ToArray();
    try
    {
        await CreateEncryptedWeChatFixtureAsync(encryptedRoot, masterKey);
        await CreatePlainWeChatFixtureAsync(plainRoot);
        await using var nativeReader = new NativeWeChatLocalDatabaseReader(
            new FakeWeChatDatabaseKeyProvider(masterKey),
            snapshotService: new WeChatDatabaseSnapshotService(snapshotRoot),
            stateStore: new WeChatReaderStateStore(Path.Combine(root, "state.json")));
        var initialized = await nativeReader.InitializeAsync(
            new WeChatInitializationRequest(encryptedRoot, 1234, TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        AssertTrue(initialized.Succeeded, "Native oracle reader should initialize.");

        const long startTimestamp = 1_780_000_000;
        const long endTimestamp = 1_780_001_000;
        var native = await nativeReader.ReadAsync(
            new WeChatReadRequest(
                DateTimeOffset.FromUnixTimeSeconds(startTimestamp),
                DateTimeOffset.FromUnixTimeSeconds(endTimestamp),
                0,
                200),
            CancellationToken.None);

        var toolDirectory = Path.Combine(ProjectToolPaths.ProjectRoot, "tools", "wechat-local-reader");
        var startInfo = new ProcessStartInfo
        {
            FileName = "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "import json,sys;sys.path.insert(0,sys.argv[1]);import wechat_local_reader as r;" +
            "m,o,d=r.read_messages(sys.argv[2],int(sys.argv[3]),int(sys.argv[4]));" +
            "print(json.dumps({'messages':m,'offset':o,'diagnostics':d},ensure_ascii=False))");
        startInfo.ArgumentList.Add(toolDirectory);
        startInfo.ArgumentList.Add(plainRoot);
        startInfo.ArgumentList.Add(startTimestamp.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(endTimestamp.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the migration Python oracle.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        AssertEqual(0, process.ExitCode, "Python oracle should complete: " + standardError.Trim());

        using var oracleDocument = JsonDocument.Parse(standardOutput);
        var oracleRoot = oracleDocument.RootElement;
        var oracleRows = oracleRoot.GetProperty("messages").EnumerateArray().ToArray();
        AssertEqual(oracleRows.Length, native.Messages.Count, "C# and Python should return the same message count.");
        AssertEqual(
            oracleRoot.GetProperty("offset").GetInt64().ToString(CultureInfo.InvariantCulture),
            native.ProposedNextOffset,
            "C# and Python should propose the same next offset.");
        for (var index = 0; index < oracleRows.Length; index++)
        {
            var oracle = oracleRows[index];
            var actual = native.Messages[index];
            AssertEqual(oracle.GetProperty("id").GetString(), actual.SourceMessageKey["WeChat:local:".Length..], "Source message id should match the oracle.");
            AssertEqual(oracle.GetProperty("chatId").GetString(), actual.ChatId, "Chat id should match the oracle.");
            AssertEqual(oracle.GetProperty("chatName").GetString(), actual.ChatName, "Chat name should match the oracle.");
            AssertEqual(oracle.GetProperty("senderName").GetString(), actual.SenderName, "Sender should match the oracle.");
            AssertEqual(oracle.GetProperty("content").GetString(), actual.Content, "Content summary should match the oracle.");
            AssertEqual(oracle.GetProperty("sentAt").GetInt64(), actual.SentAt.ToUnixTimeSeconds(), "Timestamp should match the oracle.");
            AssertEqual(oracle.GetProperty("messageType").GetString(), actual.MessageType.ToString(), "Message type should match the oracle.");
        }

        var nativeCounts = native.Stages.Single(stage => stage.Stage == WeChatReaderStage.Query).Counts!;
        var oracleDiagnostics = oracleRoot.GetProperty("diagnostics");
        foreach (var name in new[] { "sessions", "matched_tables", "shards_scanned", "rows_read", "query_errors" })
        {
            AssertEqual(
                oracleDiagnostics.GetProperty(name).GetInt64(),
                nativeCounts[name],
                $"Diagnostic count '{name}' should match the oracle.");
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(masterKey);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static Task CreateEncryptedWeChatFixtureAsync(string databaseRoot, byte[] masterKey) =>
    CreateWeChatFixtureAsync(databaseRoot, masterKey);

static Task CreatePlainWeChatFixtureAsync(string databaseRoot) =>
    CreateWeChatFixtureAsync(databaseRoot, masterKey: null);

static async Task CreateWeChatFixtureAsync(string databaseRoot, byte[]? masterKey)
{
    Directory.CreateDirectory(databaseRoot);
    await CreateFixtureDatabaseAsync(
        Path.Combine(databaseRoot, "contact", "contact.db"),
        masterKey,
        connection =>
        {
            ExecuteSql(connection, "CREATE TABLE contact(username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT);");
            ExecuteSql(connection, "INSERT INTO contact VALUES ('room@chatroom', '项目群', '');");
            ExecuteSql(connection, "INSERT INTO contact VALUES ('wxid_alice', 'Alice', '');");
        });
    await CreateFixtureDatabaseAsync(
        Path.Combine(databaseRoot, "session", "session.db"),
        masterKey,
        connection =>
        {
            ExecuteSql(connection, "CREATE TABLE SessionTable(username TEXT, last_timestamp INTEGER, display_name TEXT);");
            ExecuteSql(connection, "INSERT INTO SessionTable VALUES ('room@chatroom', 1780000300, '项目群');");
        });
    await CreateFixtureDatabaseAsync(
        Path.Combine(databaseRoot, "message", "message_0.db"),
        masterKey,
        connection =>
        {
            ExecuteSql(connection, "CREATE TABLE Name2Id(id INTEGER, user_name TEXT);");
            ExecuteSql(connection, "INSERT INTO Name2Id VALUES (1, 'wxid_alice');");
            var tableName = "Msg_" + Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("room@chatroom")));
            ExecuteSql(connection, $"CREATE TABLE \"{tableName}\"(local_id INTEGER, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, compress_content BLOB);");
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"INSERT INTO \"{tableName}\" VALUES (1, 101, 1, 1, 1780000100, $content, NULL)";
                command.Parameters.AddWithValue("$content", "wxid_alice:\n@张三 请处理");
                command.ExecuteNonQuery();
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"INSERT INTO \"{tableName}\" VALUES (2, 102, 3, 1, 1780000200, $content, NULL)";
                command.Parameters.AddWithValue("$content", "<msg><img /></msg>");
                command.ExecuteNonQuery();
            }
            using (var compressor = new Compressor())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"INSERT INTO \"{tableName}\" VALUES (3, 103, 1, 1, 1780000300, '', $compressed)";
                command.Parameters.AddWithValue("$compressed", compressor.Wrap(Encoding.UTF8.GetBytes("压缩消息")).ToArray());
                command.ExecuteNonQuery();
            }
        });
}

static async Task CreateFixtureDatabaseAsync(
    string path,
    byte[]? masterKey,
    Action<SqliteConnection> seed)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    SQLitePCL.Batteries_V2.Init();
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    if (masterKey is not null)
    {
        var result = SQLitePCL.raw.sqlite3_key(connection.Handle, masterKey);
        AssertEqual(SQLitePCL.raw.SQLITE_OK, result, "SQLCipher should accept the fixture master key.");
    }
    seed(connection);
}

static void ExecuteSql(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static byte[] CreateV4PageFixture(byte[] databaseKey, int reserveSize)
{
    var page = new byte[WeChatDatabaseKeyDeriver.PageSize];
    RandomNumberGenerator.Fill(page);
    var salt = page.AsSpan(0, WeChatDatabaseKeyDeriver.SaltSize).ToArray();
    var macSalt = salt.Select(value => (byte)(value ^ 0x3A)).ToArray();
    var macKey = Rfc2898DeriveBytes.Pbkdf2(
        databaseKey,
        macSalt,
        2,
        HashAlgorithmName.SHA512,
        WeChatDatabaseKeyDeriver.KeySize);
    try
    {
        using var hmac = new HMACSHA512(macKey);
        var pageNumber = new byte[] { 1, 0, 0, 0 };
        hmac.TransformBlock(salt, 0, salt.Length, null, 0);
        hmac.TransformBlock(pageNumber, 0, pageNumber.Length, null, 0);
        var contentLength = WeChatDatabaseKeyDeriver.PageSize - reserveSize - WeChatDatabaseKeyDeriver.SaltSize;
        var content = page.AsSpan(WeChatDatabaseKeyDeriver.SaltSize, contentLength).ToArray();
        try
        {
            hmac.TransformFinalBlock(content, 0, content.Length);
            hmac.Hash!.CopyTo(page, page.Length - hmac.Hash.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }

        return page;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(macSalt);
        CryptographicOperations.ZeroMemory(macKey);
    }
}

/// <summary>为手动转待办测试写入一条本地评分基线。</summary>
static async Task InsertUrgencyScoreAsync(
    string databasePath,
    long messageId,
    int score,
    PriorityLevel priority,
    string reason)
{
    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync(CancellationToken.None);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO urgency_scores (message_id, score, priority, reason, calculated_at)
        VALUES ($messageId, $score, $priority, $reason, $calculatedAt);
        """;
    command.Parameters.AddWithValue("$messageId", messageId);
    command.Parameters.AddWithValue("$score", score);
    command.Parameters.AddWithValue("$priority", priority.ToString());
    command.Parameters.AddWithValue("$reason", reason);
    command.Parameters.AddWithValue("$calculatedAt", DateTimeOffset.Now.ToString("O"));
    await command.ExecuteNonQueryAsync(CancellationToken.None);
}

/// <summary>读取评分行数，验证追加、幂等和事务回滚行为。</summary>
static async Task<int> CountUrgencyScoresAsync(string databasePath, long messageId)
{
    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync(CancellationToken.None);
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM urgency_scores WHERE message_id = $messageId;";
    command.Parameters.AddWithValue("$messageId", messageId);
    return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
}

static Message CreateMessage(long id, string chatName, string senderName, string content)
{
    return new Message(
        Id: id,
        Source: "ManualImport",
        SourceMessageKey: $"manual-{id}-{Guid.NewGuid():N}",
        ChatSessionId: id + 100,
        ChatName: chatName,
        SenderName: senderName,
        Content: content,
        SentAt: DateTimeOffset.Now,
        CapturedAt: DateTimeOffset.Now,
        MessageType: MessageType.Text,
        IsMentionMe: false);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool condition, string message)
{
    if (condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}

static void AssertNotNull<T>(T? value, string message) where T : class
{
    if (value is null)
    {
        throw new InvalidOperationException(message);
    }
}

/// <summary>离屏渲染一块区域后的像素统计，用于断言滚动条是否与主题一致。</summary>
public sealed record RenderedPixelSample(
    int OpaquePixels,
    int AlphaPixels,
    int ThemePixels,
    int SystemChromePixels,
    double MaxLuminance,
    double MinLuminance,
    int MaxAlpha);

public sealed class StaticWindowTextSnapshotProvider : IWindowTextSnapshotProvider
{
    private readonly IReadOnlyList<WindowTextSnapshot> _snapshots;

    public StaticWindowTextSnapshotProvider(IReadOnlyList<WindowTextSnapshot> snapshots)
    {
        _snapshots = snapshots;
    }

    public Task<IReadOnlyList<WindowTextSnapshot>> GetSnapshotsAsync(WindowTextCaptureOptions options, CancellationToken cancellationToken)
    {
        return Task.FromResult(_snapshots);
    }
}

public sealed class FakeWindowAutomationReader : IWindowAutomationReader
{
    private readonly IReadOnlyList<WindowAutomationElement> _windows;
    private readonly DateTimeOffset _capturedAt;

    public FakeWindowAutomationReader(IReadOnlyList<WindowAutomationElement> windows, DateTimeOffset capturedAt)
    {
        _windows = windows;
        _capturedAt = capturedAt;
    }

    public Task<WindowAutomationReadResult> ReadTopLevelWindowsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new WindowAutomationReadResult(_windows, _capturedAt));
    }
}

public sealed class FakeScreenOcrReader : IScreenOcrReader
{
    private readonly IReadOnlyDictionary<int, string> _textByWindowHandle;

    public FakeScreenOcrReader(IReadOnlyDictionary<int, string> textByWindowHandle)
    {
        _textByWindowHandle = textByWindowHandle;
    }

    public Task<string> ReadWindowTextAsync(int nativeWindowHandle, CancellationToken cancellationToken)
    {
        return Task.FromResult(_textByWindowHandle.TryGetValue(nativeWindowHandle, out var text) ? text : "");
    }
}

public sealed class FakeWeChatDatabaseKeyProvider : IWeChatDatabaseKeyProvider
{
    private readonly byte[] _keyBytes;

    public FakeWeChatDatabaseKeyProvider(ReadOnlySpan<byte> keyBytes)
    {
        _keyBytes = keyBytes.ToArray();
    }

    public string Name => "native-hook";

    public WeChatDatabaseKeyRequest? LastRequest { get; private set; }

    public Task<WeChatDatabaseKeyLease> AcquireAsync(
        WeChatDatabaseKeyRequest request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new WeChatDatabaseKeyLease(_keyBytes));
    }
}

public sealed class ThrowingWeChatDatabaseKeyProvider : IWeChatDatabaseKeyProvider
{
    private readonly Exception _exception;

    public ThrowingWeChatDatabaseKeyProvider(Exception exception)
    {
        _exception = exception;
    }

    public string Name => "throwing-fixture";

    public Task<WeChatDatabaseKeyLease> AcquireAsync(
        WeChatDatabaseKeyRequest request,
        CancellationToken cancellationToken) => Task.FromException<WeChatDatabaseKeyLease>(_exception);
}

public sealed class StubCaptureAdapter : IMessageCaptureAdapter
{
    private readonly Func<CaptureContext, CancellationToken, Task<CaptureBatch>> _capture;

    public StubCaptureAdapter(
        string name,
        Func<CaptureContext, CancellationToken, Task<CaptureBatch>> capture)
    {
        Name = name;
        _capture = capture;
    }

    public string Name { get; }

    public Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken) =>
        _capture(context, cancellationToken);
}

public sealed class ThrowingWeChatKeyProtector : IWeChatKeyProtector
{
    public byte[] Protect(byte[] plaintext) => throw new CryptographicException("fixture protection failed");

    public byte[] Unprotect(byte[] protectedBytes) => throw new CryptographicException("fixture unprotect failed");
}

public sealed class FakeWeChatLocalDatabaseReader : IWeChatLocalDatabaseReader
{
    private readonly IReadOnlyList<CapturedMessage> _messages;
    private readonly int _totalCount;
    private bool _disposed;

    public FakeWeChatLocalDatabaseReader(
        IReadOnlyList<CapturedMessage>? messages = null,
        int? totalCount = null)
    {
        _messages = messages ?? Array.Empty<CapturedMessage>();
        _totalCount = totalCount ?? _messages.Count;
    }

    public bool IsAvailable => !_disposed;

    public bool IsInitialized { get; private set; }

    public IReadOnlyList<WeChatReaderStageResult> Diagnostics { get; private set; } =
        Array.Empty<WeChatReaderStageResult>();

    public WeChatInitializationRequest? LastInitializationRequest { get; private set; }

    public WeChatReadRequest? LastReadRequest { get; private set; }

    public Task<WeChatInitializationResult> InitializeAsync(
        WeChatInitializationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastInitializationRequest = request;
        IsInitialized = true;
        Diagnostics = new[]
        {
            new WeChatReaderStageResult(
                WeChatReaderStage.KeyValidation,
                true,
                WeChatReaderErrorCode.None,
                "ok",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero)
        };
        return Task.FromResult(new WeChatInitializationResult(true, Diagnostics));
    }

    public Task<WeChatInitializationResult> RestoreProtectedKeyAsync(
        WeChatProtectedKeyRestoreRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsInitialized = true;
        Diagnostics = new[]
        {
            new WeChatReaderStageResult(
                WeChatReaderStage.ProtectedKey,
                true,
                WeChatReaderErrorCode.None,
                "ok",
                DateTimeOffset.UtcNow,
                TimeSpan.Zero)
        };
        return Task.FromResult(new WeChatInitializationResult(true, Diagnostics));
    }

    public Task ClearProtectedKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsInitialized = false;
        Diagnostics = Array.Empty<WeChatReaderStageResult>();
        return Task.CompletedTask;
    }

    public Task<WeChatReadResult> ReadAsync(
        WeChatReadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastReadRequest = request;
        return Task.FromResult(new WeChatReadResult(
            _messages,
            _totalCount,
            "0",
            Diagnostics));
    }

    public ValueTask DisposeAsync()
    {
        IsInitialized = false;
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeTodoDialogService : ITodoDialogService
{
    public List<(string Message, string Title)> Errors { get; } = new();

    public Task ShowAsync(TodoDetailViewModel viewModel) => Task.CompletedTask;

    public Task ShowCreateAsync(TodoCreateViewModel viewModel) => Task.CompletedTask;

    public bool Confirm(string message, string title) => true;

    public void ShowError(string message, string title) => Errors.Add((message, title));
}

public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; } = now;
}

/// <summary>记录模型 HTTP 请求并返回固定响应，避免测试访问真实服务。</summary>
public sealed class RecordingModelHttpMessageHandler(string responseBody) : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }

    public string? AuthorizationScheme { get; private set; }

    public string? AuthorizationParameter { get; private set; }

    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // 在返回响应前完整记录地址、鉴权和请求体供断言检查。
        RequestUri = request.RequestUri;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        RequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>按队列返回手动或自动待办分析结果，并记录实际发送的单条消息。</summary>
public sealed class FakeMessageActionModel(params MentionMessageAnalysis[] responses) : IMessageActionModel
{
    private readonly Queue<MentionMessageAnalysis> _responses = new(responses);

    public List<Message> Calls { get; } = new();

    /// <summary>设置后模拟模型故障，用于验证手动创建仍安全回退。</summary>
    public Exception? Exception { get; set; }

    /// <summary>去重判定命中的待办 Id；为空表示永远判定不是同一件事。</summary>
    public long? DuplicateTodoId { get; set; }

    /// <summary>记录每次去重判定收到的候选待办，便于断言只把活动待办送进模型。</summary>
    public List<IReadOnlyList<TodoItem>> DuplicateCandidates { get; } = new();

    public Task<MentionMessageAnalysis> AnalyzeMentionAsync(Message message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(message);
        if (Exception is not null)
        {
            return Task.FromException<MentionMessageAnalysis>(Exception);
        }

        return Task.FromResult(_responses.Dequeue());
    }

    public Task<long?> MatchDuplicateTodoAsync(
        Message message,
        IReadOnlyList<TodoItem> candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DuplicateCandidates.Add(candidates);
        return Task.FromResult(DuplicateTodoId);
    }
}

public sealed class RecordingNotificationPublisher : IUserNotificationPublisher
{
    public int PublishCount { get; private set; }

    public Task PublishAsync(ReminderDispatchItem item, CancellationToken cancellationToken)
    {
        PublishCount++;
        return Task.CompletedTask;
    }
}
