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
    ("Capture adapter factory creates enabled adapters for collaboration sources", TestCaptureAdapterFactoryAsync),
    ("Window text adapter captures normalized visible messages with stable keys", TestWindowTextCaptureAdapterAsync),
    ("JSONL directory adapter captures only new messages and preserves source identity", TestJsonlDirectoryCaptureAdapterAsync),
    ("Capture pipeline persists messages and creates todos for mentions", TestCapturePipelineAsync)
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

    AssertTrue(savedMessage.Id > 0, "Saved message should get a database id.");
    AssertTrue(savedTodo.Id > 0, "Saved todo should get a database id.");
    AssertEqual(1, pendingTodos.Count, "One pending todo should round-trip.");
    AssertEqual("@张三 请今天处理线上故障", recentMessages.Single().Content, "Recent message content should round-trip.");
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
    AssertFalse(wechatWindowSource.IsEnabled, "WeChat visible-window source should stay disabled until validated on the real app.");
    AssertEqual(0, CaptureAdapterFactory.CreateAdapters(new[] { wechatWindowSource }).Count, "Disabled window text source should be skipped.");

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
