using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WechatDashboard.App.Services;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using Microsoft.Data.Sqlite;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
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
    ("Any persisted message converts to one Todo with an optional reminder", TestManualMessageToTodoAsync),
    ("Todo repository reports existing Todo state for message feed rows", TestMessageTodoStateLookupAsync),
    ("Reminder snooze preserves history without changing the Todo due time", TestReminderSnoozeAsync),
    ("Reminder worker claims and delivers a due reminder only once", TestReminderWorkerAsync),
    ("Reminder worker recovers a stale dispatch claim after restart", TestReminderWorkerRecoversStaleClaimAsync),
    ("Message context query locates the source message by stable id", TestMessageContextQueryAsync),
    ("SQLite repositories initialize schema and round-trip message and todo", TestSqliteRoundTripAsync),
    ("Todo group migration preserves existing todos and is idempotent", TestTodoGroupMigrationAsync),
    ("Todo group migration rolls back when schema creation fails", TestTodoGroupMigrationRollbackAsync),
    ("Todo groups normalize names and reject duplicates", TestTodoGroupsAsync),
    ("Manual Todo creation persists a group and optional reminder atomically", TestManualTodoCreationAsync),
    ("Todo group changes persist through completion", TestTodoGroupLifecycleAsync),
    ("Manual Todo create view model prevents duplicate submission", TestTodoCreateViewModelAsync),
    ("Todo rows show groups and truthful manual source", TestTodoRowPresentationAsync),
    ("Todo workbench preserves checked rows across background refresh", TestTodoWorkbenchSelectionRefreshAsync),
    ("Desktop calendar groups scheduled Todos by local due date", TestDesktopCalendarViewModelAsync),
    ("Desktop calendar window preferences round-trip", TestDesktopCalendarStateStoreAsync),
    ("Desktop calendar edge retraction preserves its size", TestDesktopCalendarDockLayoutAsync),
    ("Desktop calendar window renders readonly Todo bindings", TestDesktopCalendarWindowRenderingAsync),
    ("SQLite message processing transaction rolls back message when Todo insert fails", TestMessageTodoTransactionRollbackAsync),
    ("Todo repository moves a checked todo to completed", TestTodoCompletionAsync),
    ("Todo repository persists pinning and sorts pinned active todos first", TestTodoPinningAsync),
    ("Todo repository completes only explicitly selected active todos", TestSelectedTodoCompletionAsync),
    ("Todo repository retains and clears Done and Ignored history", TestDeleteCompletedTodosAsync),
    ("Todo repository sorts pending todos by source message time descending", TestTodoOrderByMessageTimeAsync),
    ("Default mention aliases include current user's WeChat display names", TestDefaultMentionAliasesAsync),
    ("Capture adapter factory creates enabled adapters for collaboration sources", TestCaptureAdapterFactoryAsync),
    ("Capture adapter factory creates live WeChat visible-window adapters", TestLiveCaptureAdapterFactoryAsync),
    ("Shihuatong protobuf decoder reads text content", TestShihuatongMessageContentDecoderAsync),
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
    ("Native WeChat reader restores a validated key without KeyProbe", TestNativeWeChatReaderRestoresProtectedKeyAsync),
    ("Native WeChat key derivation validates SQLCipher V4 databases", TestNativeWeChatKeyDerivationAsync),
    ("Native WeChat reader queries V4 shards and adapter offsets", TestNativeWeChatReaderAsync),
    ("Native WeChat reader matches the frozen Python oracle field by field", TestNativeWeChatReaderMatchesPythonOracleAsync),
    ("Capture pipeline persists messages and creates todos for mentions", TestCapturePipelineAsync),
    ("Capture pipeline reports one source failure and continues healthy sources", TestCapturePipelineReportsIndividualSourceFailuresAsync),
    ("Capture pipeline persists live WeChat local export messages", TestLiveWeChatLocalExportCapturePipelineAsync),
    ("Capture source settings repository saves and loads enabled sources", TestCaptureSourceSettingsRepositoryAsync),
    ("Capture pipeline uses saved source settings for adapter construction", TestCapturePipelineWithSavedSettingsAsync),
    ("User alias repository saves, loads and deletes configurable mention aliases", TestUserAliasRepositoryAsync),
    ("Project keyword repository supports multiple keywords per project", TestFollowedProjectKeywordRepositoryAsync),
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
        content: "@zhangsan 请今天给出支付联调问题反馈");
    var classification = new ClassificationResult(30, 2, "支付平台", MessageCategory.Question, 0.85, "keyword rule", "Rules");
    var urgency = new UrgencyScore(30, 72, PriorityLevel.P1, "@我; 今天");

    var todo = TodoService.CreateFromMention(message, classification, urgency, now, timeZone);

    AssertEqual(TodoStatus.Pending, todo.Status, "Mention-created todo should start as pending.");
    AssertEqual(PriorityLevel.P1, todo.Priority, "Todo priority should follow urgency.");
    AssertEqual(30L, todo.SourceMessageId, "Todo should point to source message.");
    AssertEqual(2L, todo.ProjectId, "Todo should carry project classification.");
    AssertTrue(todo.IsAutoCreated, "Mention-created todo should be marked as automatic.");
    AssertTrue(todo.Title.Length <= 80, "Generated title should fit list display.");
    AssertEqual(
        new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.FromHours(8)).AddTicks(-1),
        todo.DueAt,
        "Automatically recognized Todo should expire at the end of the current local day.");
    AssertEqual(
        TodoDueBucket.DueToday,
        new TodoDueBucketPolicy(timeZone).Classify(todo.DueAt, now),
        "Automatically recognized Todo should appear in the due-today bucket.");

    return Task.CompletedTask;
}

static Task TestTodoDueBucketPolicyAsync()
{
    var now = new DateTimeOffset(2026, 8, 9, 14, 0, 0, TimeSpan.FromHours(8));
    var policy = new TodoDueBucketPolicy(TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));

    AssertEqual(TodoDueBucket.Overdue, policy.Classify(now.AddMinutes(-1), now), "A past due time should be overdue.");
    AssertEqual(TodoDueBucket.DueToday, policy.Classify(now.AddHours(2), now), "A later due time today should be due today.");
    AssertEqual(TodoDueBucket.Upcoming, policy.Classify(now.AddDays(1), now), "A due time tomorrow should be upcoming.");
    AssertEqual(TodoDueBucket.NoDueDate, policy.Classify(null, now), "A missing due time should be undated.");
    AssertEqual(TodoDueBucket.Overdue, policy.Classify(now, now), "A Todo due exactly now should be overdue, not duplicated across groups.");

    return Task.CompletedTask;
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
    var persistedTodo = await todoRepository.GetByIdAsync(created.Todo.Id, CancellationToken.None);

    AssertTrue(snoozed.Succeeded, "A scheduled reminder should be snoozable.");
    AssertEqual(2, reminders.Count, "Snooze should preserve the old reminder and create a new one.");
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

    var missingGroup = await todoService.CreateManualAsync(
        new CreateManualTodoRequest("无效分组", null, PriorityLevel.P1, null, null, long.MaxValue),
        CancellationToken.None);
    AssertEqual(CreateManualTodoOutcome.GroupMissing, missingGroup.Outcome, "Missing group should be a recoverable outcome.");
    AssertEqual(2, (await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None)).Count, "Missing group must not leave a Todo.");

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
    AssertEqual(2, (await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None)).Count, "Reminder failure must roll back the Todo insert.");
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
    var groupService = new TodoGroupApplicationService(new SqliteTodoGroupRepository(databasePath), clock);
    var group = (await groupService.CreateAsync("工作", CancellationToken.None)).Group;
    var todoService = new TodoApplicationService(new SqliteTodoUnitOfWork(databasePath), clock);
    var createdCount = 0;
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var dialogs = new FakeTodoDialogService();
    var viewModel = new TodoCreateViewModel(
        todoService,
        groupService,
        clock,
        dialogs,
        () =>
        {
            createdCount++;
            return Task.CompletedTask;
        });
    viewModel.RequestClose += (_, _) => closed.TrySetResult();
    await viewModel.LoadGroupsAsync();
    AssertTrue(viewModel.NewGroupColors.Any(item => item.Name == "红色" && item.Color == TodoGroupColorPolicy.Red), "Create view model should offer red for new groups.");
    AssertEqual(TodoGroupColorPolicy.DefaultColor, viewModel.GroupOptions.Single(item => item.Id == group.Id).Color, "Group options should expose the persisted color.");
    viewModel.Title = "   ";
    viewModel.SaveCommand.Execute(null);
    AssertEqual("标题不能为空。", viewModel.StatusText, "Whitespace-only title should keep the form open with a business error.");
    AssertEqual(0, (await new SqliteTodoRepository(databasePath).GetActiveAsync(CancellationToken.None)).Count, "Invalid title must not persist a Todo.");

    viewModel.Title = "  手动创建  ";
    viewModel.Description = "不依赖消息";
    viewModel.SelectedGroup = viewModel.GroupOptions.Single(item => item.Id == group.Id);
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
    AssertEqual(group.Id, active.Single().GroupId, "Create view model should persist the selected group.");
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
        GroupId = 7
    };
    var manualRow = new TodoListItemViewModel(
        manual,
        null,
        null,
        TodoDueBucket.NoDueDate,
        "工作",
        TodoGroupColorPolicy.Red,
        () => { });
    AssertEqual("工作", manualRow.GroupName, "Todo row should display the resolved group.");
    AssertEqual(TodoGroupColorPolicy.Red, manualRow.GroupColor, "Todo row should expose the group's selected color.");
    AssertEqual("手动添加", manualRow.Source, "Source-less Todo should be labelled manual.");
    AssertEqual("无来源消息", manualRow.SourceChatName, "Manual Todo should not fake a chat.");
    AssertEqual("手动备注", manualRow.MessageContent, "Manual Todo should display its description.");

    var missingSourceRow = new TodoListItemViewModel(
        manual with { SourceMessageId = 999 },
        null,
        null,
        TodoDueBucket.NoDueDate,
        "工作",
        TodoGroupColorPolicy.Red,
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
    var groupService = new TodoGroupApplicationService(new SqliteTodoGroupRepository(databasePath), clock);
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
        groupService,
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
        new CreateManualTodoRequest("今天已办理待办", null, PriorityLevel.P2, now.AddHours(7), null, null),
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
    AssertEqual(2, viewModel.SelectedTodos.Count, "The selected day should show active and completed scheduled Todos.");
    AssertEqual(todayActive.Id, viewModel.SelectedTodos.First().Id, "Active Todo should sort before a completed Todo on the same day.");
    AssertTrue(viewModel.SelectedTodos.Last().IsCompleted, "Completed daily Todo should remain visible with completed state.");
    AssertTrue(viewModel.StatusText.Contains("另有 1 条未设置截止日期"), "Calendar status should report unscheduled Todos honestly.");

    viewModel.SelectDate(new DateTime(2026, 8, 30));
    AssertEqual(1, viewModel.SelectedTodos.Count, "Selecting another date should replace the daily Todo list.");
    AssertEqual(tomorrow.Id, viewModel.SelectedTodos.Single().Id, "The selected date should show its scheduled Todo.");
    await viewModel.OpenTodoAsync(tomorrow.Id);
    AssertEqual(tomorrow.Id, openedTodoId, "Clicking a calendar Todo should delegate to the existing detail flow.");

    viewModel.MoveMonth(1);
    AssertEqual("2026年9月", viewModel.MonthTitle, "Month navigation should update the displayed month.");
}

static Task TestDesktopCalendarStateStoreAsync()
{
    var statePath = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", $"{Guid.NewGuid():N}", "desktop-calendar.json");
    var store = new DesktopCalendarStateStore(statePath);
    var expected = new DesktopCalendarWindowState(
        Left: 120,
        Top: 80,
        Width: 1100,
        Height: 700,
        Opacity: 0.78,
        IsEdgeSnapEnabled: false,
        DockEdge: DesktopCalendarDockEdge.Right);

    store.Save(expected);
    var actual = store.Load();

    AssertEqual(expected, actual, "Desktop calendar position, opacity and dock preference should round-trip.");
    return Task.CompletedTask;
}

static Task TestDesktopCalendarDockLayoutAsync()
{
    var area = new System.Windows.Rect(100, 50, 1920, 1040);
    var size = new System.Windows.Size(980, 620);
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
    AssertEqual(TimeSpan.FromSeconds(2), DesktopCalendarDockLayout.AutoHideDelay, "A docked calendar should wait two seconds before retracting.");
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
            var initialWidth = window.ActualWidth;
            var initialHeight = window.ActualHeight;
            var retractMethod = typeof(WechatDashboard.App.Views.Todos.DesktopCalendarWindow).GetMethod(
                "RetractToEdge",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var revealMethod = typeof(WechatDashboard.App.Views.Todos.DesktopCalendarWindow).GetMethod(
                "RevealFromEdge",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            AssertTrue(retractMethod is not null && revealMethod is not null, "Desktop calendar should expose its private edge transition methods to the runtime test.");
            retractMethod!.Invoke(window, null);
            window.UpdateLayout();
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

    AssertTrue(renderThread.Join(TimeSpan.FromSeconds(15)), "Desktop calendar render thread should finish without hanging.");
    if (renderFailure is not null)
    {
        throw new InvalidOperationException("Desktop calendar should construct and render its populated Todo templates.", renderFailure);
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
        """{"id":"wx-normal","platform":"WeChat","chatId":"pay","chatName":"支付项目群","senderName":"赵经理","content":"支付联调今天继续推进","sentAt":"2026-06-03T10:10:00+08:00","messageType":"Text"}"""
    }, CancellationToken.None);

    var initializer = new SqliteDatabaseInitializer(databasePath);
    await initializer.InitializeAsync(CancellationToken.None);

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
        new UrgencyRanker(priorityContacts: new[] { "王经理" }, priorityProjectIds: new[] { 1L }));

    var firstRun = await pipeline.RunOnceAsync(CancellationToken.None);
    var secondRun = await pipeline.RunOnceAsync(CancellationToken.None);
    var recentMessages = await messageRepository.GetRecentAsync(10, CancellationToken.None);
    var pendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);

    AssertEqual(2, firstRun.CapturedCount, "First pipeline run should capture two messages.");
    AssertEqual(1, firstRun.CreatedTodoCount, "Only the mention should create a todo.");
    AssertEqual(0, secondRun.CapturedCount, "Offset should prevent duplicate capture.");
    AssertEqual(CaptureSourceRunStatus.Empty, secondRun.Sources.Single().Status, "A successful zero-message source must be distinct from failure.");
    AssertEqual(2, recentMessages.Count, "Two messages should be persisted.");
    AssertEqual(1, pendingTodos.Count, "One pending todo should be persisted.");
    AssertTrue(pendingTodos.Single().Title.Contains("@张三"), "Todo title should come from mention message.");

    await using var resultConnection = new SqliteConnection($"Data Source={databasePath}");
    await resultConnection.OpenAsync(CancellationToken.None);
    await using var resultCommand = resultConnection.CreateCommand();
    resultCommand.CommandText = "SELECT COUNT(*) FROM message_classifications;";
    AssertEqual(2, Convert.ToInt32(await resultCommand.ExecuteScalarAsync(CancellationToken.None)), "Every persisted message should have a classification result.");
    resultCommand.CommandText = "SELECT COUNT(*) FROM urgency_scores;";
    AssertEqual(2, Convert.ToInt32(await resultCommand.ExecuteScalarAsync(CancellationToken.None)), "Every persisted message should have an urgency result.");
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
    var project = await projectRepository.SaveAsync("智能管线", CancellationToken.None);
    await keywordRepository.SaveAsync(project.Id, "管线提升", CancellationToken.None);
    await keywordRepository.SaveAsync(project.Id, "WMS联调", CancellationToken.None);
    await keywordRepository.SaveAsync(project.Id, "wms联调", CancellationToken.None);

    var keywords = await keywordRepository.GetAllAsync(CancellationToken.None);
    AssertEqual(2, keywords.Count, "Duplicate keywords for the same project should not create another row.");
    AssertTrue(keywords.All(keyword => keyword.ProjectName == "智能管线"), "All keywords should retain their owning project name.");

    var classifier = new ProjectClassifier(keywords.Select(keyword =>
        new ProjectRule(keyword.ProjectId, keyword.ProjectName, ProjectRuleType.Keyword, keyword.Keyword, 170)));
    var result = classifier.Classify(CreateMessage(100, "实施群", "王工", "请确认 WMS联调 计划"));
    AssertEqual(project.Id, result.ProjectId, "Any keyword of one project should classify the message into that project.");
    AssertEqual("智能管线", result.ProjectName, "Keyword classification should return the shared project name.");

    await keywordRepository.DeleteByProjectIdAsync(project.Id, CancellationToken.None);
    AssertEqual(0, (await keywordRepository.GetAllAsync(CancellationToken.None)).Count, "Deleting a project keyword set should remove all its keywords.");
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

public sealed class RecordingNotificationPublisher : IUserNotificationPublisher
{
    public int PublishCount { get; private set; }

    public Task PublishAsync(ReminderDispatchItem item, CancellationToken cancellationToken)
    {
        PublishCount++;
        return Task.CompletedTask;
    }
}
