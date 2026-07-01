using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
    private readonly SqliteCaptureSourceSettingsRepository _settingsRepository;
    private readonly SqliteUserAliasRepository _aliasRepository;
    private readonly string _captureInboxPath;
    private IReadOnlyList<string> _currentAliases = DefaultMentionAliases.All;
    private readonly WeChatLocalReaderService _readerService;
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    private readonly TimeSpan _liveCaptureInterval = TimeSpan.FromSeconds(5);
    private const int DefaultWeChatMessagePageSize = 50;
    private const int DefaultMessageStreamPageSize = 50;

    private CancellationTokenSource? _liveCaptureCts;
    private Task? _liveCaptureTask;

    private string _summaryText = "加载中";
    private string _listenerStatusText = "微信监听未启动";
    private int _todayMessageCount;
    private int _mentionCount;
    private int _pendingTodoCount;
    private int _highPriorityTodoCount;
    private int _wechatMessagePageNumber = 1;
    private int _wechatMessagePageSize = DefaultWeChatMessagePageSize;
    private int _wechatMessageTotalCount;
    private string _wechatMessagePageText = "第 0/0 页，共 0 条";
    private string _wechatMessageStatusText = "默认读取当天微信消息。请先提取 DB Key 并初始化本地库。";
    private int _messageStreamPageNumber = 1;
    private int _messageStreamPageSize = DefaultMessageStreamPageSize;
    private int _messageStreamTotalCount;
    private string _messageStreamPageText = "第 0/0 页，共 0 条";
    private string _messageStreamStatusText = "正在加载消息流...";
    public MainWindow()
    {
        InitializeComponent();

        _databasePath = Path.Combine(
            ProjectToolPaths.DataDirectory,
            "wechat-dashboard.db");
        _databaseInitializer = new SqliteDatabaseInitializer(_databasePath);
        _messageRepository = new SqliteMessageRepository(_databasePath);
        _todoRepository = new SqliteTodoRepository(_databasePath);
        _offsetRepository = new SqliteProcessingOffsetRepository(_databasePath);
        _settingsRepository = new SqliteCaptureSourceSettingsRepository(_databasePath);
        _aliasRepository = new SqliteUserAliasRepository(_databasePath);
        _captureInboxPath = ProjectToolPaths.CaptureInboxDirectory;
        _readerService = new WeChatLocalReaderService();

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

    public string WeChatMessagePageText
    {
        get => _wechatMessagePageText;
        private set => SetField(ref _wechatMessagePageText, value);
    }

    public string WeChatMessageStatusText
    {
        get => _wechatMessageStatusText;
        private set => SetField(ref _wechatMessageStatusText, value);
    }

    public string MessageStreamPageText
    {
        get => _messageStreamPageText;
        private set => SetField(ref _messageStreamPageText, value);
    }

    public string MessageStreamStatusText
    {
        get => _messageStreamStatusText;
        private set => SetField(ref _messageStreamStatusText, value);
    }

    public ObservableCollection<TodoRow> Todos { get; } = new();

    public ObservableCollection<MessageRow> Messages { get; } = new();

    public ObservableCollection<WeChatMessageRow> WeChatMessages { get; } = new();

    public ObservableCollection<ProjectSummaryRow> ProjectSummaries { get; } = new();

    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = new();

    public ObservableCollection<WindowSnapshotRow> WindowSnapshots { get; } = new();

    public ObservableCollection<CaptureSourceSettingRow> CaptureSourceSettings { get; } = new();

    public ObservableCollection<UserAliasRow> Aliases { get; } = new();

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeAndRefreshAsync(seedIfEmpty: true);
        await LoadCaptureSourceSettingsAsync();
        if (_readerService.IsInitialized)
        {
            await LoadTodayWeChatMessagesAsync(1, ensureInitialized: false);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeAndRefreshAsync(seedIfEmpty: false);
    }

    private async void LoadTodayWeChatMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadTodayWeChatMessagesAsync(1, ensureInitialized: true);
    }

    private async void PreviousWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wechatMessagePageNumber <= 1)
        {
            WeChatMessageStatusText = "已是首页，没有上一页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(_wechatMessagePageNumber - 1, ensureInitialized: true);
    }

    private async void NextWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wechatMessagePageNumber >= CalculateWeChatMessagePageCount(_wechatMessageTotalCount))
        {
            WeChatMessageStatusText = "已是末页，没有下一页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(_wechatMessagePageNumber + 1, ensureInitialized: true);
    }

    private async void FirstWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wechatMessagePageNumber <= 1)
        {
            WeChatMessageStatusText = "已是首页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(1, ensureInitialized: true);
    }

    private async void LastWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = CalculateWeChatMessagePageCount(_wechatMessageTotalCount);
        if (_wechatMessagePageNumber >= pageCount)
        {
            WeChatMessageStatusText = "已是末页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(pageCount, ensureInitialized: true);
    }

    private async void GoToWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        var requestedPage = ReadRequestedWeChatMessagePage();
        if (!requestedPage.HasValue)
        {
            return;
        }

        await LoadTodayWeChatMessagesAsync(requestedPage.Value, ensureInitialized: true);
    }

    private async void WeChatMessagePageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedPageSize = ReadSelectedWeChatMessagePageSize();
        if (selectedPageSize == _wechatMessagePageSize)
        {
            return;
        }

        var firstRowIndex = Math.Max(0, (_wechatMessagePageNumber - 1) * _wechatMessagePageSize);
        _wechatMessagePageSize = selectedPageSize;
        var targetPage = firstRowIndex / _wechatMessagePageSize + 1;
        UpdateWeChatMessagePagingText();

        if (!IsLoaded)
        {
            return;
        }

        if (!_readerService.IsInitialized && WeChatMessages.Count == 0)
        {
            WeChatMessageStatusText = $"每页显示 {_wechatMessagePageSize} 条。读取消息前请先初始化本地库。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(targetPage, ensureInitialized: true);
    }

    private async void SeedButton_Click(object sender, RoutedEventArgs e)
    {
        await SeedSampleMessagesAsync();
        await RefreshAsync();
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_readerService.IsInitialized && _readerService.IsAvailable)
        {
            ApplyBootstrapRange();
            SummaryText = "正在初始化微信本地数据库读取器...";
            var initSuccess = await _readerService.InitializeAsync(CancellationToken.None);
            if (initSuccess)
            {
                SummaryText = "本地数据库初始化成功，重新加载采集源...";
                await LoadCaptureSourceSettingsAsync();
            }
            else
            {
                SummaryText = $"本地数据库初始化失败：{_readerService.LastError ?? "未知错误"}，将使用可见窗口采集";
            }
        }

        var result = await RunCaptureOnceAsync(CancellationToken.None);
        SummaryText = $"本次采集 {result.CapturedCount} 条，入库 {result.PersistedCount} 条，创建待办 {result.CreatedTodoCount} 条";
        await RefreshAsync();
        if (_readerService.IsInitialized)
        {
            await LoadTodayWeChatMessagesAsync(_wechatMessagePageNumber, ensureInitialized: false);
        }
    }

    private async void ExtractDatabaseKeyButton_Click(object sender, RoutedEventArgs e)
    {
        SummaryText = "正在自动提取微信 DB Key。请在 5 分钟内在微信里重新登录，不要关闭微信进程...";
        ExtractDatabaseKeyButton.IsEnabled = false;
        InitLocalDatabaseButton.IsEnabled = false;
        try
        {
            var result = await _readerService.ExtractDatabaseKeyAsync(CancellationToken.None);
            if (result is null)
            {
                SummaryText = $"DB Key 提取失败：{_readerService.LastError ?? "未知错误"}";
                return;
            }

            ExternalKeyFileTextBox.Text = result.KeyPath;
            SummaryText = $"DB Key 提取成功（微信 PID {result.TargetProcessId}），Key 文件已写入 tools/result。点击\"初始化本地库\"继续。";
        }
        finally
        {
            ExtractDatabaseKeyButton.IsEnabled = true;
            InitLocalDatabaseButton.IsEnabled = true;
        }
    }

    private async void InitLocalDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_readerService.IsAvailable)
        {
            SummaryText = "微信本地读取器未安装。请将 wechat-local-reader 放置到工具目录。";
            return;
        }

        ApplyBootstrapRange();
        SummaryText = "正在初始化微信本地数据库读取器...";
        InitLocalDatabaseButton.IsEnabled = false;
        try
        {
            var success = await _readerService.InitializeAsync(CancellationToken.None);
            if (success)
            {
                SummaryText = "本地数据库初始化成功，正在读取当天微信消息...";
                await LoadCaptureSourceSettingsAsync();
                await RefreshAsync();
                await LoadTodayWeChatMessagesAsync(1, ensureInitialized: false);
            }
            else
            {
                SummaryText = $"初始化失败：{_readerService.LastError ?? "未知错误"}";
            }
        }
        finally
        {
            InitLocalDatabaseButton.IsEnabled = true;
        }
    }

    private void ApplyBootstrapRange()
    {
        if (BootstrapRangeComboBox?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag)
        {
            _readerService.BootstrapRange = tag;
        }

        _readerService.ImportedDatabaseKey = ImportedDatabaseKeyBox?.Password;
        _readerService.ExternalKeyCommand = ExternalKeyCommandTextBox?.Text;
        _readerService.ExternalKeyFile = ExternalKeyFileTextBox?.Text;
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
        await LoadUserAliasesAsync();
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
        var localDatabaseSource = CaptureAdapterFactory.CreateWeChatLocalDatabaseSource(_readerService);
        var localDatabaseConfigPath = CaptureAdapterFactory.GetWeChatLocalDatabaseConfigPath();
        var dataDir = WeChatDataDirectoryLocator.Locate();
        var localDbStatus = localDatabaseSource.IsEnabled ? "已就绪"
            : _readerService.IsAvailable ? (_readerService.LastError ?? "等待初始化")
            : "未安装";
        var localDbDetail = localDatabaseSource.IsEnabled
            ? $"配置: {localDatabaseConfigPath} | 历史范围: {_readerService.BootstrapRange}"
            : dataDir is not null
                ? $"数据目录已检测: {dataDir} | 历史范围: {_readerService.BootstrapRange}，点击\"初始化本地库\"按钮完成设置"
                : _readerService.IsAvailable
                    ? "未检测到微信数据目录，请确认微信正在运行"
                    : localDatabaseSource.Location;

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
            new DiagnosticRow("WeChat.LocalExport", "启用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "WeChatLocalExport")),
            new DiagnosticRow(
                "WeChat.LocalDatabase",
                localDbStatus,
                localDatabaseSource.IsEnabled ? DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-",
                localDbDetail),
            new DiagnosticRow("Feishu.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "Feishu")),
            new DiagnosticRow("Shihuatong.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "Shihuatong")),
            new DiagnosticRow("DingTalk.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "DingTalk")),
            new DiagnosticRow("WeChat.WindowText", "已启用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), "UIA+OCR 读取可见微信窗口，实时采集，窗口最小化后不可用"),
            new DiagnosticRow("WindowsNotificationAdapter", "未启用", "-", "待接入 Windows 通知监听")
        });

        TodayMessageCount = recentMessages.Count(message => message.SentAt.LocalDateTime.Date == DateTime.Today);
        MentionCount = recentMessages.Count(message => message.IsMentionMe);
        PendingTodoCount = pendingTodos.Count;
        HighPriorityTodoCount = pendingTodos.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1);

        await LoadMessageStreamPageAsync(_messageStreamPageNumber, refreshCount: true);
        SummaryText = $"消息 {_messageStreamTotalCount} | @我 {MentionCount} | 待办理 {PendingTodoCount} | 高优先级 {HighPriorityTodoCount}";
    }

    private async Task LoadMessageStreamPageAsync(int pageNumber)
    {
        await LoadMessageStreamPageAsync(pageNumber, refreshCount: false);
    }

    private async Task LoadMessageStreamPageAsync(int pageNumber, bool refreshCount)
    {
        var safePage = Math.Max(1, pageNumber);
        MessagePage page;
        if (refreshCount || _messageStreamTotalCount == 0)
        {
            page = await _messageRepository.GetPageAsync(safePage, _messageStreamPageSize, CancellationToken.None);
        }
        else
        {
            page = await _messageRepository.GetPageWithKnownCountAsync(
                safePage, _messageStreamPageSize, _messageStreamTotalCount, CancellationToken.None);
        }

        var pageCount = CalculateMessageStreamPageCount(page.TotalCount);
        if (page.Messages.Count == 0 && page.TotalCount > 0 && safePage > pageCount)
        {
            await LoadMessageStreamPageAsync(pageCount, refreshCount);
            return;
        }

        _messageStreamPageNumber = page.PageNumber;
        _messageStreamTotalCount = page.TotalCount;
        Replace(Messages, page.Messages.Select(message => new MessageRow(
            message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            message.ChatName,
            message.SenderName,
            message.IsMentionMe ? "是" : "否",
            message.Content)));
        UpdateMessageStreamPagingText();
        MessageStreamStatusText = page.Messages.Count == 0
            ? "暂无消息。"
            : $"已加载本页 {page.Messages.Count} 条消息。";
    }

    private async void FirstMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messageStreamPageNumber <= 1)
        {
            MessageStreamStatusText = "已是首页。";
            return;
        }

        await LoadMessageStreamPageAsync(1);
    }

    private async void PreviousMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messageStreamPageNumber <= 1)
        {
            MessageStreamStatusText = "已是首页，没有上一页。";
            return;
        }

        await LoadMessageStreamPageAsync(_messageStreamPageNumber - 1);
    }

    private async void NextMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messageStreamPageNumber >= CalculateMessageStreamPageCount(_messageStreamTotalCount))
        {
            MessageStreamStatusText = "已是末页，没有下一页。";
            return;
        }

        await LoadMessageStreamPageAsync(_messageStreamPageNumber + 1);
    }

    private async void LastMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        var pageCount = CalculateMessageStreamPageCount(_messageStreamTotalCount);
        if (_messageStreamPageNumber >= pageCount)
        {
            MessageStreamStatusText = "已是末页。";
            return;
        }

        await LoadMessageStreamPageAsync(pageCount);
    }

    private async void GoToMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        var requestedPage = ReadRequestedMessageStreamPage();
        if (!requestedPage.HasValue)
        {
            return;
        }

        await LoadMessageStreamPageAsync(requestedPage.Value);
    }

    private async void MessageStreamPageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedPageSize = ReadSelectedMessageStreamPageSize();
        if (selectedPageSize == _messageStreamPageSize)
        {
            return;
        }

        var firstRowIndex = Math.Max(0, (_messageStreamPageNumber - 1) * _messageStreamPageSize);
        _messageStreamPageSize = selectedPageSize;
        var targetPage = firstRowIndex / _messageStreamPageSize + 1;
        UpdateMessageStreamPagingText();

        if (!IsLoaded)
        {
            return;
        }

        await LoadMessageStreamPageAsync(targetPage);
    }

    private int CalculateMessageStreamPageCount(int totalCount)
    {
        return Math.Max(1, (int)Math.Ceiling(totalCount / (double)_messageStreamPageSize));
    }

    private int ReadSelectedMessageStreamPageSize()
    {
        if (MessageStreamPageSizeComboBox?.SelectedItem is ComboBoxItem item)
        {
            var rawValue = item.Tag?.ToString() ?? item.Content?.ToString();
            if (int.TryParse(rawValue, out var selectedPageSize))
            {
                return Math.Clamp(selectedPageSize, 1, 200);
            }
        }

        return _messageStreamPageSize;
    }

    private int? ReadRequestedMessageStreamPage()
    {
        var rawPage = MessageStreamPageNumberTextBox?.Text?.Trim();
        if (!int.TryParse(rawPage, out var requestedPage) || requestedPage <= 0)
        {
            MessageStreamStatusText = "请输入有效页码。";
            return null;
        }

        var pageCount = CalculateMessageStreamPageCount(_messageStreamTotalCount);
        return Math.Clamp(requestedPage, 1, pageCount);
    }

    private void UpdateMessageStreamPagingText()
    {
        var pageCount = CalculateMessageStreamPageCount(_messageStreamTotalCount);
        MessageStreamPageText = $"第 {_messageStreamPageNumber}/{pageCount} 页，共 {_messageStreamTotalCount} 条，每页 {_messageStreamPageSize} 条";
        if (MessageStreamPageNumberTextBox is not null)
        {
            MessageStreamPageNumberTextBox.Text = _messageStreamPageNumber.ToString();
        }
    }

    private async Task LoadTodayWeChatMessagesAsync(int pageNumber, bool ensureInitialized)
    {
        if (!_readerService.IsAvailable)
        {
            WeChatMessageStatusText = "微信本地读取器未安装。";
            SummaryText = "微信本地读取器未安装。";
            return;
        }

        ApplyBootstrapRange();
        if (!_readerService.IsInitialized)
        {
            if (!ensureInitialized)
            {
                WeChatMessageStatusText = "本地库尚未初始化，点击\"初始化本地库\"或\"读取当天消息\"。";
                return;
            }

            SummaryText = "正在初始化微信本地数据库读取器...";
            var initialized = await _readerService.InitializeAsync(CancellationToken.None);
            if (!initialized)
            {
                var error = _readerService.LastError ?? "未知错误";
                WeChatMessageStatusText = $"初始化失败：{error}";
                SummaryText = $"初始化失败：{error}";
                return;
            }

            await LoadCaptureSourceSettingsAsync();
        }

        _wechatMessagePageSize = ReadSelectedWeChatMessagePageSize();
        var safePage = Math.Max(1, pageNumber);
        WeChatMessageStatusText = $"正在读取今天微信消息第 {safePage} 页，每页 {_wechatMessagePageSize} 条...";
        var page = await _readerService.ReadMessagesAsync(
            new WeChatLocalMessageReadOptions(DateTime.Today, safePage, _wechatMessagePageSize),
            CancellationToken.None);
        if (page is null)
        {
            var error = _readerService.LastError ?? "未知错误";
            WeChatMessageStatusText = $"读取失败：{error}";
            SummaryText = $"读取微信消息失败：{error}";
            return;
        }

        var pageCount = CalculateWeChatMessagePageCount(page.TotalCount);
        if (page.Messages.Count == 0 && page.TotalCount > 0 && safePage > pageCount)
        {
            await LoadTodayWeChatMessagesAsync(pageCount, ensureInitialized: false);
            return;
        }

        _wechatMessagePageNumber = page.PageNumber;
        _wechatMessageTotalCount = page.TotalCount;
        Replace(WeChatMessages, page.Messages.Select(message => new WeChatMessageRow(
            message.Content,
            message.ChatName,
            message.SenderName)));
        UpdateWeChatMessagePagingText();
        TodayMessageCount = page.TotalCount;
        WeChatMessageStatusText = $"已读取今天微信消息 {page.Messages.Count} 条，本页最多 {_wechatMessagePageSize} 条。";
        SummaryText = $"今天微信消息 {page.TotalCount} 条，当前第 {_wechatMessagePageNumber}/{CalculateWeChatMessagePageCount(page.TotalCount)} 页";
    }

    private int CalculateWeChatMessagePageCount(int totalCount)
    {
        return Math.Max(1, (int)Math.Ceiling(totalCount / (double)_wechatMessagePageSize));
    }

    private int ReadSelectedWeChatMessagePageSize()
    {
        if (WeChatMessagePageSizeComboBox?.SelectedItem is ComboBoxItem item)
        {
            var rawValue = item.Tag?.ToString() ?? item.Content?.ToString();
            if (int.TryParse(rawValue, out var selectedPageSize))
            {
                return Math.Clamp(selectedPageSize, 1, 200);
            }
        }

        return _wechatMessagePageSize;
    }

    private int? ReadRequestedWeChatMessagePage()
    {
        var rawPage = WeChatMessagePageNumberTextBox?.Text?.Trim();
        if (!int.TryParse(rawPage, out var requestedPage) || requestedPage <= 0)
        {
            WeChatMessageStatusText = "请输入有效页码。";
            return null;
        }

        var pageCount = CalculateWeChatMessagePageCount(_wechatMessageTotalCount);
        return Math.Clamp(requestedPage, 1, pageCount);
    }

    private void UpdateWeChatMessagePagingText()
    {
        var pageCount = CalculateWeChatMessagePageCount(_wechatMessageTotalCount);
        WeChatMessagePageText = $"第 {_wechatMessagePageNumber}/{pageCount} 页，共 {_wechatMessageTotalCount} 条，每页 {_wechatMessagePageSize} 条";
        if (WeChatMessagePageNumberTextBox is not null)
        {
            WeChatMessagePageNumberTextBox.Text = _wechatMessagePageNumber.ToString();
        }
    }

    private async Task SeedSampleMessagesAsync()
    {
        var mentionDetector = new MentionDetector(_currentAliases);
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

    private async Task LoadCaptureSourceSettingsAsync()
    {
        var savedSettings = await _settingsRepository.GetAllAsync(CancellationToken.None);
        _readerService.ResetInitializationState();
        var defaultSources = CaptureAdapterFactory.CreateDefaultLiveSources(_captureInboxPath, _readerService);

        var rows = new List<CaptureSourceSettingRow>();
        foreach (var source in defaultSources)
        {
            var saved = savedSettings.FirstOrDefault(s =>
                s.Source == source.Source && s.Kind == source.Kind.ToString());

            rows.Add(new CaptureSourceSettingRow
            {
                Source = source.Source,
                DisplayName = source.DisplayName,
                Kind = source.Kind.ToString(),
                Location = source.Location,
                IsEnabled = saved?.IsEnabled ?? source.IsEnabled
            });
        }

        Replace(CaptureSourceSettings, rows);
    }

    private async Task LoadUserAliasesAsync()
    {
        var aliases = await _aliasRepository.GetAllAsync(CancellationToken.None);
        if (aliases.Count == 0)
        {
            var seeded = new List<UserAlias>();
            foreach (var alias in DefaultMentionAliases.All)
            {
                seeded.Add(await _aliasRepository.SaveAsync(alias, CancellationToken.None));
            }
            aliases = seeded;
        }

        _currentAliases = aliases.Select(alias => alias.Alias).ToArray();
        Replace(Aliases, aliases.Select(alias => new UserAliasRow(alias.Id, alias.Alias)));
    }

    private async void SaveCaptureSourceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        var settings = CaptureSourceSettings.Select(row => new CaptureSourceSettings(
            Id: 0,
            Source: row.Source,
            DisplayName: row.DisplayName,
            Kind: row.Kind,
            Location: row.Location,
            IsEnabled: row.IsEnabled,
            CreatedAt: now,
            UpdatedAt: now)).ToList();

        await _settingsRepository.DeleteAllAsync(CancellationToken.None);
        await _settingsRepository.SaveAllAsync(settings, CancellationToken.None);
        SummaryText = $"采集源设置已保存，共 {settings.Count} 个源";
    }

    private async void AddAliasButton_Click(object sender, RoutedEventArgs e)
    {
        var alias = NewAliasTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(alias))
        {
            SummaryText = "请输入要添加的别名。";
            return;
        }

        await _aliasRepository.SaveAsync(alias, CancellationToken.None);
        if (NewAliasTextBox is not null)
        {
            NewAliasTextBox.Text = "";
        }
        await LoadUserAliasesAsync();
        SummaryText = $"已添加别名「{alias}」，新采集将按更新后的别名识别 @我。";
    }

    private async void DeleteAliasButton_Click(object sender, RoutedEventArgs e)
    {
        if (AliasesGrid?.SelectedItem is not UserAliasRow row)
        {
            SummaryText = "请先选中要删除的别名。";
            return;
        }

        await _aliasRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadUserAliasesAsync();
        SummaryText = $"已删除别名「{row.Alias}」。";
    }

    private MessageCapturePipeline CreateCapturePipeline()
    {
        _readerService.ResetInitializationState();
        var defaultSources = CaptureAdapterFactory.CreateDefaultLiveSources(_captureInboxPath, _readerService);
        var effectiveSources = defaultSources.Select(source =>
        {
            var saved = CaptureSourceSettings.FirstOrDefault(row =>
                row.Source == source.Source && row.Kind == source.Kind.ToString());
            return saved is null ? source : source with { IsEnabled = saved.IsEnabled };
        }).ToArray();

        return new MessageCapturePipeline(
            adapters: CaptureAdapterFactory.CreateAdapters(
                effectiveSources,
                CreateWindowTextSnapshotProvider()),
            _messageRepository,
            _todoRepository,
            _offsetRepository,
            new MentionDetector(_currentAliases),
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

public sealed record WeChatMessageRow(string Content, string ChatName, string SenderName);

public sealed record ProjectSummaryRow(string Project, int PendingTodos, int HighPriorityTodos);

public sealed record DiagnosticRow(string Adapter, string Status, string LastSuccessAt, string Detail);

public sealed record WindowSnapshotRow(string WindowTitle, string CapturedAt, int TextLength, string Preview);

public sealed record CaptureSourceSettingRow
{
    public string Source { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsEnabled { get; set; }
}

public sealed record UserAliasRow(long Id, string Alias);
