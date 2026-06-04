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
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private readonly TimeSpan _liveCaptureInterval = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _liveCaptureCts;
    private Task? _liveCaptureTask;

    private string _summaryText = "加载中";
    private string _listenerStatusText = "微信监听未启动";
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

    public string ListenerStatusText
    {
        get => _listenerStatusText;
        private set => SetField(ref _listenerStatusText, value);
    }

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

    public ObservableCollection<WindowSnapshotRow> WindowSnapshots { get; } = new();

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
        var result = await RunCaptureOnceAsync(CancellationToken.None);
        SummaryText = $"本次采集 {result.CapturedCount} 条，入库 {result.PersistedCount} 条，创建待办 {result.CreatedTodoCount} 条";
        await RefreshAsync();
    }

    private void StartWeChatListenerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_liveCaptureTask is { IsCompleted: false })
        {
            ListenerStatusText = "微信监听已在运行";
            return;
        }

        _liveCaptureCts = new CancellationTokenSource();
        _liveCaptureTask = RunLiveCaptureLoopAsync(_liveCaptureCts.Token);
        ListenerStatusText = $"微信监听运行中，间隔 {_liveCaptureInterval.TotalSeconds:0} 秒";
    }

    private async void StopWeChatListenerButton_Click(object sender, RoutedEventArgs e)
    {
        await StopLiveCaptureAsync();
    }

    private async void ScanWeChatWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        var service = new WindowCaptureDiagnosticsService(CreateWindowTextSnapshotProvider());
        var rows = await service.ScanAsync(
            CreateWeChatWindowTextOptions(),
            CancellationToken.None);

        Replace(WindowSnapshots, rows.Select(row => new WindowSnapshotRow(
            row.WindowTitle,
            row.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            row.TextLength,
            row.Preview)));

        SummaryText = $"扫描到 {rows.Count} 个微信可见窗口快照";
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        await StopLiveCaptureAsync();
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
            new DiagnosticRow("WeChat.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "WeChat")),
            new DiagnosticRow("Feishu.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "Feishu")),
            new DiagnosticRow("Shihuatong.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "Shihuatong")),
            new DiagnosticRow("DingTalk.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "DingTalk")),
            new DiagnosticRow("WeChat.WindowText", "启用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), "通过 Windows UI Automation + OCR 读取微信可见窗口文本"),
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
        var mentionDetector = new MentionDetector(DefaultMentionAliases.All);
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
            CreateSampleMessage("sample-crm-urgent", "CRM项目群", "王经理", "@白驹过隙 紧急，今天下班前处理线上故障", -45),
            CreateSampleMessage("sample-payment-question", "支付项目群", "赵经理", "@戴少峰 请今天给出支付联调问题反馈", -25),
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
            adapters: CaptureAdapterFactory.CreateAdapters(
                CaptureAdapterFactory.CreateDefaultLiveSources(_captureInboxPath),
                CreateWindowTextSnapshotProvider()),
            _messageRepository,
            _todoRepository,
            _offsetRepository,
            new MentionDetector(DefaultMentionAliases.All),
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

    private async Task<CaptureRunResult> RunCaptureOnceAsync(CancellationToken cancellationToken)
    {
        await _captureSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _databaseInitializer.InitializeAsync(cancellationToken);
            Directory.CreateDirectory(_captureInboxPath);
            var pipeline = CreateCapturePipeline();
            return await pipeline.RunOnceAsync(cancellationToken);
        }
        finally
        {
            _captureSemaphore.Release();
        }
    }

    private async Task RunLiveCaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunCaptureOnceAsync(cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    ListenerStatusText = $"微信监听运行中，最近采集 {result.CapturedCount} 条，入库 {result.PersistedCount} 条，待办 {result.CreatedTodoCount} 条";
                    SummaryText = $"监听采集 {result.CapturedCount} 条，入库 {result.PersistedCount} 条，创建待办 {result.CreatedTodoCount} 条";
                });

                if (result.PersistedCount > 0 || result.CreatedTodoCount > 0)
                {
                    await RefreshOnUiThreadAsync();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ListenerStatusText = $"微信监听异常：{ex.Message}";
                    SummaryText = $"微信监听异常：{ex.Message}";
                });
            }

            try
            {
                await Task.Delay(_liveCaptureInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task StopLiveCaptureAsync()
    {
        if (_liveCaptureCts is null)
        {
            ListenerStatusText = "微信监听未启动";
            return;
        }

        _liveCaptureCts.Cancel();
        try
        {
            if (_liveCaptureTask is not null)
            {
                await _liveCaptureTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _liveCaptureCts.Dispose();
            _liveCaptureCts = null;
            _liveCaptureTask = null;
            ListenerStatusText = "微信监听已停止";
        }
    }

    private async Task RefreshOnUiThreadAsync()
    {
        if (Dispatcher.CheckAccess())
        {
            await RefreshAsync();
            return;
        }

        var refreshTask = await Dispatcher.InvokeAsync(RefreshAsync);
        await refreshTask;
    }

    private static IWindowTextSnapshotProvider CreateWindowTextSnapshotProvider()
    {
        return new WindowsOcrWindowTextSnapshotProvider(
            new SystemWindowsAutomationReader(),
            new WindowsScreenOcrReader());
    }

    private static WindowTextCaptureOptions CreateWeChatWindowTextOptions()
    {
        return new WindowTextCaptureOptions(
            Source: "WeChat",
            DisplayName: "微信可见窗口",
            WindowTitleContains: "微信",
            ChatId: "visible-window",
            ChatName: "微信可见窗口")
        {
            IgnoreWindowTitleContains = new[] { "微信项目消息看板", "WeChat Dashboard" }
        };
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

public sealed record WindowSnapshotRow(string WindowTitle, string CapturedAt, int TextLength, string Preview);
