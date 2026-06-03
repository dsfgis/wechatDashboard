using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Capture;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly string _databasePath;
    private readonly SqliteDatabaseInitializer _databaseInitializer;
    private readonly SqliteMessageRepository _messageRepository;
    private readonly SqliteTodoRepository _todoRepository;
    private readonly SqliteProcessingOffsetRepository _offsetRepository;
    private readonly string _captureInboxPath;

    private string _summaryText = "加载中";
    private int _todayMessageCount;
    private int _mentionCount;
    private int _pendingTodoCount;
    private int _highPriorityTodoCount;

    public MainWindow()
    {
        InitializeComponent();

        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WechatDashboard",
            "data",
            "wechat-dashboard.db");
        _databaseInitializer = new SqliteDatabaseInitializer(_databasePath);
        _messageRepository = new SqliteMessageRepository(_databasePath);
        _todoRepository = new SqliteTodoRepository(_databasePath);
        _offsetRepository = new SqliteProcessingOffsetRepository(_databasePath);
        _captureInboxPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WechatDashboard",
            "capture-inbox");

        DatabasePath = _databasePath;
        CaptureInboxPath = _captureInboxPath;
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DatabasePath { get; }

    public string CaptureInboxPath { get; }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    public int TodayMessageCount
    {
        get => _todayMessageCount;
        private set => SetField(ref _todayMessageCount, value);
    }

    public int MentionCount
    {
        get => _mentionCount;
        private set => SetField(ref _mentionCount, value);
    }

    public int PendingTodoCount
    {
        get => _pendingTodoCount;
        private set => SetField(ref _pendingTodoCount, value);
    }

    public int HighPriorityTodoCount
    {
        get => _highPriorityTodoCount;
        private set => SetField(ref _highPriorityTodoCount, value);
    }

    public ObservableCollection<TodoRow> Todos { get; } = new();

    public ObservableCollection<MessageRow> Messages { get; } = new();

    public ObservableCollection<ProjectSummaryRow> ProjectSummaries { get; } = new();

    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = new();

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeAndRefreshAsync(seedIfEmpty: true);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeAndRefreshAsync(seedIfEmpty: false);
    }

    private async void SeedButton_Click(object sender, RoutedEventArgs e)
    {
        await SeedSampleMessagesAsync();
        await RefreshAsync();
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await _databaseInitializer.InitializeAsync(CancellationToken.None);
        Directory.CreateDirectory(_captureInboxPath);
        var pipeline = CreateCapturePipeline();
        var result = await pipeline.RunOnceAsync(CancellationToken.None);
        SummaryText = $"本次采集 {result.CapturedCount} 条，入库 {result.PersistedCount} 条，创建待办 {result.CreatedTodoCount} 条";
        await RefreshAsync();
    }

    private async Task InitializeAndRefreshAsync(bool seedIfEmpty)
    {
        await _databaseInitializer.InitializeAsync(CancellationToken.None);
        if (seedIfEmpty)
        {
            var existingMessages = await _messageRepository.GetRecentAsync(1, CancellationToken.None);
            if (existingMessages.Count == 0)
            {
                await SeedSampleMessagesAsync();
            }
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var recentMessages = await _messageRepository.GetRecentAsync(100, CancellationToken.None);
        var pendingTodos = await _todoRepository.GetPendingAsync(CancellationToken.None);

        Replace(Messages, recentMessages.Select(message => new MessageRow(
            message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            message.ChatName,
            message.SenderName,
            message.IsMentionMe ? "是" : "否",
            message.Content)));

        Replace(Todos, pendingTodos.Select(todo => new TodoRow(
            todo.Id,
            todo.Priority.ToString(),
            todo.Status.ToString(),
            ProjectName(todo.ProjectId),
            todo.Title,
            todo.SourceMessageId?.ToString() ?? "")));

        Replace(ProjectSummaries, pendingTodos
            .GroupBy(todo => ProjectName(todo.ProjectId))
            .Select(group => new ProjectSummaryRow(
                group.Key,
                group.Count(),
                group.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1))));

        Replace(Diagnostics, new[]
        {
            new DiagnosticRow("ManualImportAdapter", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), "示例消息和手动导入入口已就绪"),
            new DiagnosticRow("WeChat.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), _captureInboxPath),
            new DiagnosticRow("Feishu.JsonlDirectory", "可扩展", "-", "新增飞书 Adapter 后接入同一采集流水线"),
            new DiagnosticRow("Shihuatong.JsonlDirectory", "可扩展", "-", "新增石化通 Adapter 后接入同一采集流水线"),
            new DiagnosticRow("DingTalk.JsonlDirectory", "可扩展", "-", "新增钉钉 Adapter 后接入同一采集流水线"),
            new DiagnosticRow("WeChatUiaAdapter", "未启用", "-", "待接入 Windows UI Automation"),
            new DiagnosticRow("WindowsNotificationAdapter", "未启用", "-", "待接入 Windows 通知监听")
        });

        TodayMessageCount = recentMessages.Count(message => message.SentAt.LocalDateTime.Date == DateTime.Today);
        MentionCount = recentMessages.Count(message => message.IsMentionMe);
        PendingTodoCount = pendingTodos.Count;
        HighPriorityTodoCount = pendingTodos.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1);
        SummaryText = $"消息 {recentMessages.Count} | @我 {MentionCount} | 待办理 {PendingTodoCount} | 高优先级 {HighPriorityTodoCount}";
    }

    private async Task SeedSampleMessagesAsync()
    {
        var mentionDetector = new MentionDetector(new[] { "张三", "zhangsan" });
        var classifier = new ProjectClassifier(new[]
        {
            new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100),
            new ProjectRule(2, "支付平台", ProjectRuleType.Keyword, "支付", 80),
            new ProjectRule(3, "数据中台", ProjectRuleType.Keyword, "数据", 70)
        });
        var ranker = new UrgencyRanker(
            priorityContacts: new[] { "王经理", "赵经理" },
            priorityProjectIds: new[] { 1L, 2L });

        var samples = new[]
        {
            CreateSampleMessage("sample-crm-urgent", "CRM项目群", "王经理", "@张三 紧急，今天下班前处理线上故障", -45),
            CreateSampleMessage("sample-payment-question", "支付项目群", "赵经理", "@zhangsan 请今天给出支付联调问题反馈", -25),
            CreateSampleMessage("sample-data-fyi", "数据中台群", "李工", "同步一下数据看板口径变更，明早评审", -10)
        };

        foreach (var sample in samples)
        {
            var isMentionMe = mentionDetector.IsMentioned(sample.Content);
            var saved = await _messageRepository.SaveAsync(sample with { IsMentionMe = isMentionMe }, CancellationToken.None);
            var classification = classifier.Classify(saved);
            var urgency = ranker.Calculate(saved, isMentionMe, classification);

            if (isMentionMe)
            {
                await _todoRepository.SaveAsync(TodoService.CreateFromMention(saved, classification, urgency), CancellationToken.None);
            }
        }
    }

    private MessageCapturePipeline CreateCapturePipeline()
    {
        return new MessageCapturePipeline(
            adapters: new IMessageCaptureAdapter[]
            {
                new JsonlDirectoryCaptureAdapter("WeChat", _captureInboxPath)
            },
            _messageRepository,
            _todoRepository,
            _offsetRepository,
            new MentionDetector(new[] { "张三", "zhangsan" }),
            new ProjectClassifier(new[]
            {
                new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100),
                new ProjectRule(2, "支付平台", ProjectRuleType.Keyword, "支付", 80),
                new ProjectRule(3, "数据中台", ProjectRuleType.Keyword, "数据", 70)
            }),
            new UrgencyRanker(
                priorityContacts: new[] { "王经理", "赵经理" },
                priorityProjectIds: new[] { 1L, 2L }));
    }

    private static Message CreateSampleMessage(string key, string chatName, string senderName, string content, int minutesOffset)
    {
        var sentAt = DateTimeOffset.Now.AddMinutes(minutesOffset);
        return new Message(
            Id: 0,
            Source: "Sample",
            SourceMessageKey: key + "-" + Guid.NewGuid().ToString("N"),
            ChatSessionId: Math.Abs(chatName.GetHashCode()),
            ChatName: chatName,
            SenderName: senderName,
            Content: content,
            SentAt: sentAt,
            CapturedAt: sentAt,
            MessageType: MessageType.Text,
            IsMentionMe: false);
    }

    private static string ProjectName(long? projectId)
    {
        return projectId switch
        {
            1 => "CRM升级",
            2 => "支付平台",
            3 => "数据中台",
            _ => "未分类"
        };
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record TodoRow(long Id, string Priority, string Status, string Project, string Title, string SourceMessageId);

public sealed record MessageRow(string SentAt, string ChatName, string SenderName, string IsMentionMe, string Content);

public sealed record ProjectSummaryRow(string Project, int PendingTodos, int HighPriorityTodos);

public sealed record DiagnosticRow(string Adapter, string Status, string LastSuccessAt, string Detail);
