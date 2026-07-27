using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Capture;
using WechatDashboard.Infrastructure.Persistence;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Mention detector matches aliases and WeChat mention hints", TestMentionDetectorAsync),
    ("Project classifier uses chat and keyword rules", TestProjectClassifierAsync),
    ("Urgency ranker promotes mentioned incident due today to P0", TestUrgencyRankerAsync),
    ("Todo service creates a pending todo from a mention message", TestTodoCreationAsync),
    ("SQLite repositories initialize schema and round-trip message and todo", TestSqliteRoundTripAsync),
    ("Todo repository moves a checked todo to completed", TestTodoCompletionAsync),
    ("Todo repository sorts pending todos by source message time descending", TestTodoOrderByMessageTimeAsync),
    ("Default mention aliases include current user's WeChat display names", TestDefaultMentionAliasesAsync),
    ("Capture adapter factory creates enabled adapters for collaboration sources", TestCaptureAdapterFactoryAsync),
    ("Capture adapter factory creates live WeChat visible-window adapters", TestLiveCaptureAdapterFactoryAsync),
    ("Window text adapter captures normalized visible messages with stable keys", TestWindowTextCaptureAdapterAsync),
    ("Window text adapter captures UIA split visible message blocks", TestWindowTextCaptureAdapterSplitBlocksAsync),
    ("Windows UI Automation snapshot provider filters windows and aggregates visible text", TestWindowsUiAutomationSnapshotProviderAsync),
    ("WeChat OCR crop calculator focuses the chat panel", TestWeChatOcrCropCalculatorAsync),
    ("Windows OCR snapshot provider reads WeChat text when UIA exposes only chrome", TestWindowsOcrSnapshotProviderAsync),
    ("Window capture diagnostics service summarizes matching snapshots", TestWindowCaptureDiagnosticsServiceAsync),
    ("JSONL directory adapter captures only new messages and preserves source identity", TestJsonlDirectoryCaptureAdapterAsync),
    ("WeChat local export adapter captures local database export messages incrementally", TestWeChatLocalExportCaptureAdapterAsync),
    ("WeChat local command adapter captures structured messages and passes offsets", TestWeChatLocalCommandCaptureAdapterAsync),
    ("WeChat local command adapter exposes staged diagnostics from reader", TestWeChatLocalCommandStagedDiagnosticsAsync),
    ("WeChat local reader service extracts DB key through PowerShell probe", TestWeChatLocalReaderServiceExtractsDatabaseKeyAsync),
    ("WeChat local reader service reads today messages with paging", TestWeChatLocalReaderServiceReadsPagedMessagesAsync),
    ("Capture pipeline persists messages and creates todos for mentions", TestCapturePipelineAsync),
    ("Capture pipeline persists live WeChat local export messages", TestLiveWeChatLocalExportCapturePipelineAsync),
    ("Capture source settings repository saves and loads enabled sources", TestCaptureSourceSettingsRepositoryAsync),
    ("Capture pipeline uses saved source settings for adapter construction", TestCapturePipelineWithSavedSettingsAsync),
    ("User alias repository saves, loads and deletes configurable mention aliases", TestUserAliasRepositoryAsync)
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
    var message = CreateMessage(
        id: 30,
        chatName: "支付项目群",
        senderName: "赵经理",
        content: "@zhangsan 请今天给出支付联调问题反馈");
    var classification = new ClassificationResult(30, 2, "支付平台", MessageCategory.Question, 0.85, "keyword rule", "Rules");
    var urgency = new UrgencyScore(30, 72, PriorityLevel.P1, "@我; 今天");

    var todo = TodoService.CreateFromMention(message, classification, urgency);

    AssertEqual(TodoStatus.Pending, todo.Status, "Mention-created todo should start as pending.");
    AssertEqual(PriorityLevel.P1, todo.Priority, "Todo priority should follow urgency.");
    AssertEqual(30L, todo.SourceMessageId, "Todo should point to source message.");
    AssertEqual(2L, todo.ProjectId, "Todo should carry project classification.");
    AssertTrue(todo.IsAutoCreated, "Mention-created todo should be marked as automatic.");
    AssertTrue(todo.Title.Length <= 80, "Generated title should fit list display.");

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

    AssertTrue(savedMessage.Id > 0, "Saved message should get a database id.");
    AssertTrue(savedTodo.Id > 0, "Saved todo should get a database id.");
    AssertEqual(1, pendingTodos.Count, "One pending todo should round-trip.");
    AssertEqual("@张三 请今天处理线上故障", recentMessages.Single().Content, "Recent message content should round-trip.");
    AssertEqual(1, messagesById.Count, "ID lookup should return only matching messages.");
    AssertEqual(savedMessage.SentAt, messagesById.Single().SentAt, "ID lookup should preserve the original message time.");
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
            UpdatedAt = now,
            CompletedAt = null
        },
        CancellationToken.None);

    var bulkCompletionTime = now.AddSeconds(2);
    var bulkUpdatedCount = await todoRepository.MarkAllCompletedAsync(bulkCompletionTime, CancellationToken.None);
    var remainingPendingTodos = await todoRepository.GetPendingAsync(CancellationToken.None);
    var allCompletedTodos = await todoRepository.GetCompletedAsync(CancellationToken.None);

    AssertEqual(2, bulkUpdatedCount, "Bulk completion should update every pending todo.");
    AssertEqual(0, remainingPendingTodos.Count, "Bulk completion should leave no pending todos.");
    AssertEqual(3, allCompletedTodos.Count, "Individually and bulk completed todos should all be returned.");
    AssertEqual(2, allCompletedTodos.Count(item => item.CompletedAt == bulkCompletionTime), "Bulk completion time should be applied consistently.");
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
    AssertTrue(wechatWindowSource.IsEnabled, "WeChat visible-window source should be enabled by default.");
    var windowAdapters = CaptureAdapterFactory.CreateAdapters(
        new[] { wechatWindowSource },
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
    AssertTrue(definitions.Any(source => source.Source == "WeChat" && source.Kind == CaptureSourceKind.WindowText && source.IsEnabled), "Live capture should enable visible-window source by default.");

    var adapters = CaptureAdapterFactory.CreateAdapters(
        definitions,
        new StaticWindowTextSnapshotProvider(Array.Empty<WindowTextSnapshot>()));

    AssertTrue(adapters.Any(adapter => adapter.Name == "WeChat.LocalExport"), "Live adapters should include WeChat local export adapter.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "WeChat.WindowText"), "Live adapters should include WeChat visible-window adapter when provider is available.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "WeChat.JsonlDirectory"), "Live adapters should still include WeChat JSONL adapter.");
    AssertTrue(adapters.Any(adapter => adapter.Name == "DingTalk.JsonlDirectory"), "Live adapters should preserve extensibility for other sources.");

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

static async Task TestWeChatLocalCommandCaptureAdapterAsync()
{
    var runner = new FakeExternalCommandRunner(new ExternalCommandResult(
        ExitCode: 0,
        StandardOutput: """
                        {
                          "nextOffset": "1700000000:10003",
                          "messages": [
                            {
                              "id": "10003",
                              "chatId": "room-digital",
                              "chatName": "数字石化（二期）",
                              "senderName": "刘荐辉",
                              "content": "@白驹过隙 今天下班前反馈",
                              "sentAt": "2026-06-06T09:20:00+08:00",
                              "messageType": "Text"
                            }
                          ]
                        }
                        """,
        StandardError: ""));
    var adapter = new WeChatLocalCommandCaptureAdapter(
        new WeChatLocalCommandOptions("wechat-local-reader.exe", new[] { "capture", "--format", "json" }),
        runner);

    var batch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>
    {
        [adapter.Name] = "1699999999:9999"
    }), CancellationToken.None);

    AssertEqual("WeChat.LocalDatabase", adapter.Name, "Command adapter should identify the local database source.");
    AssertEqual(1, batch.Messages.Count, "Command adapter should capture structured messages.");
    AssertEqual("WeChat:local:10003", batch.Messages[0].SourceMessageKey, "Command message should use stable local identity.");
    AssertEqual("数字石化（二期）", batch.Messages[0].ChatName, "Command message should preserve chat name.");
    AssertEqual("@白驹过隙 今天下班前反馈", batch.Messages[0].Content, "Command message should preserve content.");
    AssertEqual("1700000000:10003", batch.NextOffset, "Command adapter should use the reader's next offset.");
    AssertEqual("1699999999:9999", runner.LastEnvironment["WECHAT_DASHBOARD_OFFSET"], "Existing offset should be passed to the reader.");
}

static async Task TestWeChatLocalCommandStagedDiagnosticsAsync()
{
    var runner = new FakeExternalCommandRunner(new ExternalCommandResult(
        ExitCode: 0,
        StandardOutput: """
                        {
                          "status": "ok",
                          "stages": [
                            {"stage": "config", "status": "loaded", "db_dir": "D:/cache/xwechat_files/dsfgis_84f8/db_storage"},
                            {"stage": "keys", "status": "loaded", "key_count": 12},
                            {"stage": "decrypt", "status": "ok", "total": 12, "decrypted": 3, "skipped": 9, "failed": 0},
                            {"stage": "offset", "status": "ok", "last_offset": 1700000000, "query_start": 1699999990},
                            {"stage": "query", "status": "ok", "sessions": 5, "matched_tables": 3, "shards_scanned": 2, "rows_read": 1}
                          ],
                          "nextOffset": "1700000200",
                          "messages": [
                            {
                              "id": "room@chatroom:message_0.db:1001:1700000100",
                              "chatId": "room@chatroom",
                              "chatName": "测试群",
                              "senderName": "Alice",
                              "content": "@白驹过隙 确认一下",
                              "sentAt": 1700000100,
                              "messageType": "Text"
                            }
                          ]
                        }
                        """,
        StandardError: ""));
    var adapter = new WeChatLocalCommandCaptureAdapter(
        new WeChatLocalCommandOptions("wechat-local-reader.exe", new[] { "capture", "--format", "json" }),
        runner);

    var batch = await adapter.CaptureAsync(new CaptureContext(new Dictionary<string, string>()), CancellationToken.None);

    AssertEqual(1, batch.Messages.Count, "Staged diagnostics test should still capture messages.");
    AssertEqual("1700000200", batch.NextOffset, "Next offset should come from reader output.");
    AssertNotNull(adapter.LastStages, "Adapter should expose staged diagnostics.");
    AssertEqual("ok", adapter.LastStages!.Status, "Overall status should be ok.");
    AssertEqual(5, adapter.LastStages.Stages.Count, "All five stages should be parsed.");
    AssertEqual("decrypt", adapter.LastStages.Stages[2].Name, "Third stage should be decrypt.");
    AssertEqual("ok", adapter.LastStages.Stages[2].Status, "Decrypt stage should be ok.");
    AssertTrue(adapter.LastStages.Stages[2].Detail.Contains("decrypted=3"), "Decrypt detail should include decrypted count.");
    AssertEqual("query", adapter.LastStages.Stages[4].Name, "Last stage should be query.");
    AssertTrue(adapter.LastStages.Stages[4].Detail.Contains("rows_read=1"), "Query detail should include rows_read.");
}
static async Task TestWeChatLocalReaderServiceExtractsDatabaseKeyAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var scriptPath = Path.Combine(root, "run-wx-key-probe.ps1");
    var dllDirectory = Path.Combine(root, "dll");
    var keyPath = Path.Combine(root, "wx-key-found.txt");
    var logPath = Path.Combine(root, "wx-key-probe.log");
    Directory.CreateDirectory(dllDirectory);
    await File.WriteAllTextAsync(scriptPath, "# fake wx_key probe", CancellationToken.None);

    var runner = new FakeExternalCommandRunner(
        new ExternalCommandResult(0, "", ""),
        onRun: (_, _, _, _) => File.WriteAllText(keyPath, "DB Key: " + new string('a', 64)));
    var service = new WeChatLocalReaderService(runner);

    var result = await service.ExtractDatabaseKeyAsync(
        new WeChatDatabaseKeyExtractionOptions(
            ScriptPath: scriptPath,
            DllDirectory: dllDirectory,
            KeyPath: keyPath,
            LogPath: logPath,
            Seconds: 7,
            TargetProcessId: 1234),
        CancellationToken.None);

    AssertNotNull(result, "DB key extraction should return a result.");
    AssertEqual(1234, result!.TargetProcessId, "Configured target PID should be used.");
    AssertEqual(keyPath, result.KeyPath, "Result should expose the generated key file path.");
    AssertEqual(keyPath, service.ExternalKeyFile, "Service should reuse the generated key file for reader init.");
    var probeArguments = runner.LastArguments
        ?? throw new InvalidOperationException("PowerShell probe arguments should be captured.");
    AssertTrue(probeArguments.Contains("-TargetPid"), "Probe should receive TargetPid.");
    AssertTrue(probeArguments.Contains("1234"), "Probe should receive the selected PID.");
    AssertTrue(probeArguments.Contains("-DllDir"), "Probe should receive DllDir.");
    AssertTrue(probeArguments.Contains(dllDirectory), "Probe should receive the wx_key DLL directory.");
    AssertTrue(probeArguments.Contains("-KeyPath"), "Probe should receive KeyPath.");
    AssertTrue(probeArguments.Contains(keyPath), "Probe should write to the configured key file.");
    AssertTrue(probeArguments.Contains("-LogPath"), "Probe should receive LogPath.");
    AssertTrue(probeArguments.Contains(logPath), "Probe should write logs to the configured log path.");
}

static async Task TestWeChatLocalReaderServiceReadsPagedMessagesAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var configPath = Path.Combine(root, "config.json");
    await File.WriteAllTextAsync(configPath, "{}", CancellationToken.None);

    var runner = new FakeExternalCommandRunner(new ExternalCommandResult(
        ExitCode: 0,
        StandardOutput: """
                        {
                          "status": "ok",
                          "totalMessages": 75,
                          "offset": 50,
                          "limit": 50,
                          "messages": [
                            {
                              "id": "room@chatroom:message_0.db:1051:1782702000",
                              "chatId": "room@chatroom",
                              "chatName": "项目群",
                              "senderName": "Alice",
                              "content": "第51条消息",
                              "sentAt": "2026-06-29T10:00:00+08:00",
                              "messageType": "Text"
                            }
                          ]
                        }
                        """,
        StandardError: ""));
    var service = new WeChatLocalReaderService(runner);
    var date = new DateTime(2026, 6, 29);

    var page = await service.ReadMessagesAsync(
        new WeChatLocalMessageReadOptions(date, PageNumber: 2, PageSize: 50, ConfigPath: configPath),
        CancellationToken.None);

    AssertNotNull(page, "Message page should be returned.");
    AssertEqual(75, page!.TotalCount, "Total message count should come from reader output.");
    AssertEqual(2, page.PageNumber, "Requested page number should round-trip.");
    AssertEqual(50, page.PageSize, "Requested page size should round-trip.");
    AssertEqual(1, page.Messages.Count, "Second page should contain parsed messages.");
    AssertEqual("第51条消息", page.Messages[0].Content, "Message content should be parsed for the table.");
    AssertEqual("项目群", page.Messages[0].ChatName, "Chat name should be parsed for the table.");
    AssertEqual("Alice", page.Messages[0].SenderName, "Sender should be parsed for the table.");

    var arguments = runner.LastArguments
        ?? throw new InvalidOperationException("Reader arguments should be captured.");
    var localDate = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
    var startTimestamp = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate)).ToUnixTimeSeconds().ToString();
    var endTimestamp = new DateTimeOffset(localDate.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(localDate.AddDays(1))).ToUnixTimeSeconds().ToString();
    AssertTrue(arguments.Contains("capture"), "Reader should run the capture command.");
    AssertTrue(arguments.Contains("--start-timestamp"), "Reader should receive the local day start timestamp flag.");
    AssertTrue(arguments.Contains(startTimestamp), "Reader should receive the local day start timestamp.");
    AssertTrue(arguments.Contains("--end-timestamp"), "Reader should receive the next day timestamp flag.");
    AssertTrue(arguments.Contains(endTimestamp), "Reader should receive the next day timestamp.");
    AssertTrue(arguments.Contains("--offset"), "Reader should receive a paging offset flag.");
    AssertTrue(arguments.Contains("50"), "Second page should use offset 50 and limit 50.");
    AssertTrue(arguments.Contains("--limit"), "Reader should receive a page-size limit flag.");
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
        messageRepository,
        todoRepository,
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
    AssertEqual(2, recentMessages.Count, "Two messages should be persisted.");
    AssertEqual(1, pendingTodos.Count, "One pending todo should be persisted.");
    AssertTrue(pendingTodos.Single().Title.Contains("@张三"), "Todo title should come from mention message.");
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
        .Where(source => source.Kind != CaptureSourceKind.WeChatLocalCommand)
        .ToArray();
    var pipeline = new MessageCapturePipeline(
        adapters: CaptureAdapterFactory.CreateAdapters(sources),
        messageRepository,
        todoRepository,
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
        .Where(source => source.Kind != CaptureSourceKind.WeChatLocalCommand)
        .ToArray();
    var effectiveSources = defaultSources.Select(source =>
    {
        var saved = savedSettings.FirstOrDefault(s =>
            s.Source == source.Source && s.Kind == source.Kind.ToString());
        return saved is null ? source : source with { IsEnabled = saved.IsEnabled };
    }).ToArray();

    var pipeline = new MessageCapturePipeline(
        adapters: CaptureAdapterFactory.CreateAdapters(effectiveSources),
        messageRepository,
        todoRepository,
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

public sealed class FakeExternalCommandRunner : IExternalCommandRunner
{
    private readonly ExternalCommandResult _result;
    private readonly Action<string, IReadOnlyList<string>, string?, IReadOnlyDictionary<string, string>>? _onRun;

    public FakeExternalCommandRunner(
        ExternalCommandResult result,
        Action<string, IReadOnlyList<string>, string?, IReadOnlyDictionary<string, string>>? onRun = null)
    {
        _result = result;
        _onRun = onRun;
    }

    public string? LastExecutablePath { get; private set; }

    public IReadOnlyList<string>? LastArguments { get; private set; }

    public string? LastWorkingDirectory { get; private set; }

    public IReadOnlyDictionary<string, string> LastEnvironment { get; private set; } =
        new Dictionary<string, string>();

    public Task<ExternalCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        LastExecutablePath = executablePath;
        LastArguments = arguments.ToArray();
        LastWorkingDirectory = workingDirectory;
        LastEnvironment = environment;
        _onRun?.Invoke(executablePath, LastArguments, workingDirectory, environment);
        return Task.FromResult(_result);
    }
}
