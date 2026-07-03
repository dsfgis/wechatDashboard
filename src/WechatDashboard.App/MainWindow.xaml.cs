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

/// <summary>
/// 应用主窗口：承担消息看板的核心交互与编排职责。
/// 职责包括：初始化 SQLite 数据库、加载/刷新消息流与待办、微信本地库读取器初始化、
/// 实时监听循环、采集源设置管理、用户别名维护、可见窗口扫描诊断等。
/// 采用 MVVM 风格：本类既是 View 的 code-behind，也充当简易 ViewModel，
/// 通过 INotifyPropertyChanged 向 XAML 绑定推送状态变更。
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    // 数据库文件路径
    private readonly string _databasePath;
    // 数据库初始化器（建表/建索引）
    private readonly SqliteDatabaseInitializer _databaseInitializer;
    // 各仓储依赖
    private readonly SqliteMessageRepository _messageRepository;
    private readonly SqliteTodoRepository _todoRepository;
    private readonly SqliteProcessingOffsetRepository _offsetRepository;
    private readonly SqliteCaptureSourceSettingsRepository _settingsRepository;
    private readonly SqliteUserAliasRepository _aliasRepository;
    private readonly SqliteFollowedChatRepository _followedChatRepository;
    // JSONL 采集收件箱路径
    private readonly string _captureInboxPath;
    // 当前生效的 @我 别名集合（默认兜底）
    private IReadOnlyList<string> _currentAliases = DefaultMentionAliases.All;
    // 当前生效的关注群名称集合（空列表表示不过滤）
    private IReadOnlyList<string> _currentFollowedChats = Array.Empty<string>();
    // 关注群过滤模式：Include=只显示列表内群，Exclude=排除列表内群
    private FollowedChatFilterMode _followedChatFilterMode = FollowedChatFilterMode.Include;
    // 微信本地读取器服务
    private readonly WeChatLocalReaderService _readerService;
    // 采集并发控制信号量，避免实时循环与手动采集重叠
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    // 实时采集间隔
    private readonly TimeSpan _liveCaptureInterval = TimeSpan.FromSeconds(5);
    private const int DefaultWeChatMessagePageSize = 50;
    private const int DefaultMessageStreamPageSize = 50;

    // 实时监听取消令牌
    private CancellationTokenSource? _liveCaptureCts;
    // 实时监听后台任务
    private Task? _liveCaptureTask;

    // 顶部摘要文本
    private string _summaryText = "加载中";
    // 监听状态文本
    private string _listenerStatusText = "微信监听未启动";
    private int _todayMessageCount;
    private int _mentionCount;
    private int _pendingTodoCount;
    private int _highPriorityTodoCount;
    // 微信消息分页状态
    private int _wechatMessagePageNumber = 1;
    private int _wechatMessagePageSize = DefaultWeChatMessagePageSize;
    private int _wechatMessageTotalCount;
    private string _wechatMessagePageText = "第 0/0 页，共 0 条";
    private string _wechatMessageStatusText = "默认读取当天微信消息。请先提取 DB Key 并初始化本地库。";
    // 消息流分页状态
    private int _messageStreamPageNumber = 1;
    private int _messageStreamPageSize = DefaultMessageStreamPageSize;
    private int _messageStreamTotalCount;
    private string _messageStreamPageText = "第 0/0 页，共 0 条";
    private string _messageStreamStatusText = "正在加载消息流...";
    public MainWindow()
    {
        InitializeComponent();

        // 数据库文件位于 tools/result/data 目录下
        _databasePath = Path.Combine(
            ProjectToolPaths.DataDirectory,
            "wechat-dashboard.db");
        _databaseInitializer = new SqliteDatabaseInitializer(_databasePath);
        _messageRepository = new SqliteMessageRepository(_databasePath);
        _todoRepository = new SqliteTodoRepository(_databasePath);
        _offsetRepository = new SqliteProcessingOffsetRepository(_databasePath);
        _settingsRepository = new SqliteCaptureSourceSettingsRepository(_databasePath);
        _aliasRepository = new SqliteUserAliasRepository(_databasePath);
        _followedChatRepository = new SqliteFollowedChatRepository(_databasePath);
        _captureInboxPath = ProjectToolPaths.CaptureInboxDirectory;
        _readerService = new WeChatLocalReaderService();

        DatabasePath = _databasePath;
        CaptureInboxPath = _captureInboxPath;
        // 设置数据上下文为本窗口，使 XAML 绑定生效
        DataContext = this;
    }

    // 属性变更通知事件，供 WPF 绑定监听
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

    // 待办列表（UI 绑定）
    public ObservableCollection<TodoRow> Todos { get; } = new();

    // 消息流列表（UI 绑定）
    public ObservableCollection<MessageRow> Messages { get; } = new();

    // 支持高亮的消息流列表（UI 绑定）
    public ObservableCollection<HighlightableMessageRow> HighlightableMessages { get; } = new();

    // 微信原生消息列表（UI 绑定）
    public ObservableCollection<WeChatMessageRow> WeChatMessages { get; } = new();

    // 支持高亮的微信消息列表（UI 绑定）
    public ObservableCollection<HighlightableWeChatMessageRow> HighlightableWeChatMessages { get; } = new();

    // 项目汇总列表（UI 绑定）
    public ObservableCollection<ProjectSummaryRow> ProjectSummaries { get; } = new();

    // 采集诊断列表（UI 绑定）
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = new();

    // 窗口快照列表（UI 绑定）
    public ObservableCollection<WindowSnapshotRow> WindowSnapshots { get; } = new();

    // 采集源设置列表（UI 绑定）
    public ObservableCollection<CaptureSourceSettingRow> CaptureSourceSettings { get; } = new();

    // 用户别名列表（UI 绑定）
    public ObservableCollection<UserAliasRow> Aliases { get; } = new();

    // 关注群列表（UI 绑定）
    public ObservableCollection<FollowedChatRow> FollowedChats { get; } = new();

    /// <summary>窗口加载完成：初始化数据库、加载采集源设置，并在读取器就绪时读取当天消息。</summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeAndRefreshAsync(seedIfEmpty: true);
        await LoadCaptureSourceSettingsAsync();
        if (_readerService.IsInitialized)
        {
            await LoadTodayWeChatMessagesAsync(1, ensureInitialized: false);
        }
    }

    /// <summary>点击"刷新"按钮：重新初始化并刷新（不播种示例数据）。</summary>
    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeAndRefreshAsync(seedIfEmpty: false);
    }

    /// <summary>点击"读取当天微信消息"按钮：必要时初始化读取器后读取首页。</summary>
    private async void LoadTodayWeChatMessagesButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadTodayWeChatMessagesAsync(1, ensureInitialized: true);
    }

    /// <summary>微信消息上一页：已是首页时给出提示。</summary>
    private async void PreviousWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wechatMessagePageNumber <= 1)
        {
            WeChatMessageStatusText = "已是首页，没有上一页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(_wechatMessagePageNumber - 1, ensureInitialized: true);
    }

    /// <summary>微信消息下一页：已是末页时给出提示。</summary>
    private async void NextWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wechatMessagePageNumber >= CalculateWeChatMessagePageCount(_wechatMessageTotalCount))
        {
            WeChatMessageStatusText = "已是末页，没有下一页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(_wechatMessagePageNumber + 1, ensureInitialized: true);
    }

    /// <summary>跳到首页。</summary>
    private async void FirstWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wechatMessagePageNumber <= 1)
        {
            WeChatMessageStatusText = "已是首页。";
            return;
        }

        await LoadTodayWeChatMessagesAsync(1, ensureInitialized: true);
    }

    /// <summary>跳到末页。</summary>
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

    /// <summary>跳转到指定页码（解析输入框的页号）。</summary>
    private async void GoToWeChatMessagesPageButton_Click(object sender, RoutedEventArgs e)
    {
        var requestedPage = ReadRequestedWeChatMessagePage();
        if (!requestedPage.HasValue)
        {
            return;
        }

        await LoadTodayWeChatMessagesAsync(requestedPage.Value, ensureInitialized: true);
    }

    /// <summary>每页条数变化：尽量保持当前可视行，重新计算页码并刷新。</summary>
    private async void WeChatMessagePageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedPageSize = ReadSelectedWeChatMessagePageSize();
        if (selectedPageSize == _wechatMessagePageSize)
        {
            return;
        }

        // 保留当前首行所在页，避免跳回第一页
        var firstRowIndex = Math.Max(0, (_wechatMessagePageNumber - 1) * _wechatMessagePageSize);
        _wechatMessagePageSize = selectedPageSize;
        var targetPage = firstRowIndex / _wechatMessagePageSize + 1;
        UpdateWeChatMessagePagingText();

        // 窗口尚未加载完成则不触发查询
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

    /// <summary>播种示例消息用于首次体验。</summary>
    private async void SeedButton_Click(object sender, RoutedEventArgs e)
    {
        await SeedSampleMessagesAsync();
        await RefreshAsync();
    }

    /// <summary>手动采集：必要时先初始化读取器，再执行一次采集并刷新。</summary>
    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        // 读取器未初始化但可用时，先尝试初始化
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

    /// <summary>提取微信 DB Key：提示用户 5 分钟内重新登录微信。</summary>
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
            // 无论成功失败都恢复按钮可用
            ExtractDatabaseKeyButton.IsEnabled = true;
            InitLocalDatabaseButton.IsEnabled = true;
        }
    }

    /// <summary>初始化微信本地数据库读取器。</summary>
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

    /// <summary>从 UI 控件把引导参数同步到读取器服务。</summary>
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

    /// <summary>启动微信实时监听循环。</summary>
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

    /// <summary>停止微信实时监听。</summary>
    private async void StopWeChatListenerButton_Click(object sender, RoutedEventArgs e)
    {
        await StopLiveCaptureAsync();
    }

    /// <summary>扫描微信可见窗口并填充快照列表。</summary>
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

    /// <summary>窗口关闭时停止监听，避免后台任务泄漏。</summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        await StopLiveCaptureAsync();
    }

    /// <summary>初始化数据库并刷新；seedIfEmpty 控制首次空库时是否播种示例。</summary>
    private async Task InitializeAndRefreshAsync(bool seedIfEmpty)
    {
        await _databaseInitializer.InitializeAsync(CancellationToken.None);
        await LoadUserAliasesAsync();
        await LoadFollowedChatsAsync();
        if (seedIfEmpty)
        {
            // 空库时播种示例，便于首次体验
            var existingMessages = await _messageRepository.GetRecentAsync(1, CancellationToken.None);
            if (existingMessages.Count == 0)
            {
                await SeedSampleMessagesAsync();
            }
        }

        await RefreshAsync();
    }

    /// <summary>
    /// 刷新整个看板：从仓储重新读取最近消息、待办、采集源状态、汇总数据，
    /// 并重新计算各 Tab 的分页文本与顶部摘要。
    /// 该方法会触发多次数据库查询，UI 变更通过数据绑定自动反映。
    /// </summary>
    private async Task RefreshAsync()
    {
        // 取最近 100 条入库消息用于首页展示与摘要统计
        var recentMessages = await _messageRepository.GetRecentAsync(100, CancellationToken.None);
        // 取所有未完成待办，用于待办列表与项目汇总
        var pendingTodos = await _todoRepository.GetPendingAsync(CancellationToken.None);
        // 采集源：微信本地数据库（基于 wechat-local-reader 解密）
        var localDatabaseSource = CaptureAdapterFactory.CreateWeChatLocalDatabaseSource(_readerService);
        var localDatabaseConfigPath = CaptureAdapterFactory.GetWeChatLocalDatabaseConfigPath();
        // 微信数据目录（db_storage），用于初始化本地数据库采集源
        var dataDir = WeChatDataDirectoryLocator.Locate();
        // 计算本地数据库采集源的状态文案
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

        // 待办列表绑定数据：通过 SourceMessageId 关联消息获取群名和发送时间
        var messageLookup = recentMessages.ToDictionary(m => m.Id);
        Replace(Todos, pendingTodos.Select(todo =>
        {
            // 默认使用待办创建时间
            var sentAt = todo.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            var chatName = ProjectName(todo.ProjectId);

            // 若能找到关联消息，则取消息的群名和发送时间
            if (todo.SourceMessageId.HasValue && messageLookup.TryGetValue(todo.SourceMessageId.Value, out var msg))
            {
                chatName = msg.ChatName;
                sentAt = msg.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            }

            return new TodoRow(
                todo.Id,
                todo.Priority.ToString(),
                todo.Status.ToString(),
                chatName,
                todo.Title,
                todo.SourceMessageId?.ToString() ?? "",
                sentAt);
        }));

        // 项目汇总：按项目分组统计待办数量与高优先级数量
        Replace(ProjectSummaries, pendingTodos
            .GroupBy(todo => ProjectName(todo.ProjectId))
            .Select(group => new ProjectSummaryRow(
                group.Key,
                group.Count(),
                group.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1))));

        // 采集诊断列表：展示各采集源的可用性与状态
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

        // 顶部摘要统计：今日消息数、@我数、待办理数、高优先级待办数
        TodayMessageCount = recentMessages.Count(message => message.SentAt.LocalDateTime.Date == DateTime.Today);
        MentionCount = recentMessages.Count(message => message.IsMentionMe);
        PendingTodoCount = pendingTodos.Count;
        HighPriorityTodoCount = pendingTodos.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1);

        // 刷新消息流分页（强制重新计算总数）
        await LoadMessageStreamPageAsync(_messageStreamPageNumber, refreshCount: true);
        SummaryText = $"消息 {_messageStreamTotalCount} | @我 {MentionCount} | 待办理 {PendingTodoCount} | 高优先级 {HighPriorityTodoCount}";
    }

    /// <summary>重载消息流分页（默认不刷新总数）。</summary>
    private async Task LoadMessageStreamPageAsync(int pageNumber)
    {
        await LoadMessageStreamPageAsync(pageNumber, refreshCount: false);
    }

    /// <summary>
    /// 加载消息流分页数据。
    /// refreshCount=true 时强制重新查询总数；否则复用已知总数以减少 COUNT(*) 查询开销。
    /// 处理边界情况：页码越界自动回退到末页。
    /// </summary>
    private async Task LoadMessageStreamPageAsync(int pageNumber, bool refreshCount)
    {
        var safePage = Math.Max(1, pageNumber);
        IReadOnlyCollection<string>? chatFilter = _currentFollowedChats.Count > 0 ? _currentFollowedChats : null;
        var include = _followedChatFilterMode == FollowedChatFilterMode.Include;
        MessagePage page;
        if (refreshCount || _messageStreamTotalCount == 0)
        {
            page = await _messageRepository.GetPageAsync(safePage, _messageStreamPageSize, chatFilter, include, CancellationToken.None);
        }
        else
        {
            page = await _messageRepository.GetPageWithKnownCountAsync(
                safePage, _messageStreamPageSize, _messageStreamTotalCount, chatFilter, include, CancellationToken.None);
        }

        var pageCount = CalculateMessageStreamPageCount(page.TotalCount);
        if (page.Messages.Count == 0 && page.TotalCount > 0 && safePage > pageCount)
        {
            await LoadMessageStreamPageAsync(pageCount, refreshCount);
            return;
        }

        _messageStreamPageNumber = page.PageNumber;
        _messageStreamTotalCount = page.TotalCount;

        // 创建高亮检测器
        var mentionDetector = new MentionDetector(_currentAliases);

        Replace(Messages, page.Messages.Select(message => new MessageRow(
            message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            message.ChatName,
            message.SenderName,
            message.IsMentionMe ? "是" : "否",
            message.Content)));

        // 同时更新高亮版本的消息列表
        Replace(HighlightableMessages, page.Messages.Select(message =>
        {
            var segments = WechatDashboard.Application.Mentions.MessageHighlighter.HighlightMentions(
                message.Content,
                mentionDetector.ExtractMentionedAliases(message.Content));

            return new HighlightableMessageRow
            {
                SentAt = message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                ChatName = message.ChatName,
                SenderName = message.SenderName,
                IsMentionMe = message.IsMentionMe ? "是" : "否",
                RawContent = message.Content,
                ContentSegments = segments
            };
        }));

        UpdateMessageStreamPagingText();
        var modeText = _followedChatFilterMode == FollowedChatFilterMode.Exclude ? "排除" : "关注";
        MessageStreamStatusText = page.Messages.Count == 0
            ? _currentFollowedChats.Count > 0 ? $"暂无{modeText}群消息，请在关注群设置中调整群名或模式。" : "暂无消息。"
            : $"已加载本页 {page.Messages.Count} 条消息。";
    }

    /// <summary>消息流跳到首页：已是首页时给出提示。</summary>
    private async void FirstMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messageStreamPageNumber <= 1)
        {
            MessageStreamStatusText = "已是首页。";
            return;
        }

        await LoadMessageStreamPageAsync(1);
    }

    /// <summary>消息流上一页：已是首页时给出提示。</summary>
    private async void PreviousMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messageStreamPageNumber <= 1)
        {
            MessageStreamStatusText = "已是首页，没有上一页。";
            return;
        }

        await LoadMessageStreamPageAsync(_messageStreamPageNumber - 1);
    }

    /// <summary>消息流下一页：已是末页时给出提示。</summary>
    private async void NextMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_messageStreamPageNumber >= CalculateMessageStreamPageCount(_messageStreamTotalCount))
        {
            MessageStreamStatusText = "已是末页，没有下一页。";
            return;
        }

        await LoadMessageStreamPageAsync(_messageStreamPageNumber + 1);
    }

    /// <summary>消息流跳到末页：已是末页时给出提示。</summary>
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

    /// <summary>消息流跳转到指定页码（解析输入框的页号）。</summary>
    private async void GoToMessageStreamPageButton_Click(object sender, RoutedEventArgs e)
    {
        var requestedPage = ReadRequestedMessageStreamPage();
        if (!requestedPage.HasValue)
        {
            return;
        }

        await LoadMessageStreamPageAsync(requestedPage.Value);
    }

    /// <summary>消息流每页条数变化：保留当前首行所在页，重新计算页码并刷新。</summary>
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

    /// <summary>根据总条数与每页大小计算消息流总页数，至少为 1。</summary>
    private int CalculateMessageStreamPageCount(int totalCount)
    {
        return Math.Max(1, (int)Math.Ceiling(totalCount / (double)_messageStreamPageSize));
    }

    /// <summary>从下拉框读取用户选择的消息流每页条数，限制在 1-200 之间。</summary>
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

    /// <summary>读取页码输入框并校验，返回 1 到总页数之间的合法页码；非法时给出提示并返回 null。</summary>
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

    /// <summary>更新消息流分页文本与页码输入框，反映当前页/总页数/总条数/每页条数。</summary>
    private void UpdateMessageStreamPagingText()
    {
        var pageCount = CalculateMessageStreamPageCount(_messageStreamTotalCount);
        MessageStreamPageText = $"第 {_messageStreamPageNumber}/{pageCount} 页，共 {_messageStreamTotalCount} 条，每页 {_messageStreamPageSize} 条";
        if (MessageStreamPageNumberTextBox is not null)
        {
            MessageStreamPageNumberTextBox.Text = _messageStreamPageNumber.ToString();
        }
    }

    /// <summary>
    /// 加载当天微信消息分页。
    /// ensureInitialized=true 时若读取器未初始化则自动尝试初始化（耗时操作），
    /// ensureInitialized=false 时仅提示用户先初始化，不发起耗时操作。
    /// </summary>
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

        // 将本页读取到的微信消息同步入库：去重持久化，并为 @我 的消息自动创建待办。
        // 复用采集管线的"去重 -> 分类 -> 评分 -> 建待办"流程，SourceMessageKey 去重避免重复建待办。
        // 处理的是本地读取器原始消息（未经过关注群过滤），与采集管线行为保持一致。
        // 通过 _captureSemaphore 串行化，避免与实时监听循环并发写入导致重复入库/重复建待办。
        int syncedTodos = 0;
        int syncedPersisted = 0;
        if (page.Messages.Count > 0)
        {
            await _captureSemaphore.WaitAsync(CancellationToken.None);
            try
            {
                var syncPipeline = CreateCapturePipeline();
                var syncResult = await syncPipeline.ProcessAsync(page.Messages, CancellationToken.None);
                syncedPersisted = syncResult.PersistedCount;
                syncedTodos = syncResult.CreatedTodoCount;
            }
            finally
            {
                _captureSemaphore.Release();
            }
        }

        // 按关注群过滤消息（若配置了关注群）
        var filteredMessages = page.Messages;
        if (_currentFollowedChats.Count > 0)
        {
            var chatSet = new HashSet<string>(_currentFollowedChats, StringComparer.OrdinalIgnoreCase);
            filteredMessages = _followedChatFilterMode == FollowedChatFilterMode.Exclude
                ? page.Messages.Where(m => !chatSet.Contains(m.ChatName)).ToList()
                : page.Messages.Where(m => chatSet.Contains(m.ChatName)).ToList();
        }

        // 创建高亮检测器
        var mentionDetector = new MentionDetector(_currentAliases);

        Replace(WeChatMessages, filteredMessages.Select(message => new WeChatMessageRow(
            message.Content,
            message.ChatName,
            message.SenderName)));

        // 同时更新高亮版本的微信消息列表
        Replace(HighlightableWeChatMessages, filteredMessages.Select(message =>
        {
            var segments = WechatDashboard.Application.Mentions.MessageHighlighter.HighlightMentions(
                message.Content,
                mentionDetector.ExtractMentionedAliases(message.Content));

            return new HighlightableWeChatMessageRow
            {
                Content = message.Content,
                ChatName = message.ChatName,
                SenderName = message.SenderName,
                SentAt = message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                ContentSegments = segments
            };
        }));

        UpdateWeChatMessagePagingText();
        TodayMessageCount = filteredMessages.Count;
        var modeText2 = _followedChatFilterMode == FollowedChatFilterMode.Exclude ? "排除" : "关注";
        WeChatMessageStatusText = filteredMessages.Count == 0
            ? _currentFollowedChats.Count > 0 ? $"暂无{modeText2}群消息，请在关注群设置中调整群名或模式。" : "暂无消息。"
            : $"已读取今天微信消息 {filteredMessages.Count} 条。";
        // 若有新消息入库或新待办创建，刷新待办列表与顶部摘要，使 @我 同步结果即时可见
        if (syncedPersisted > 0 || syncedTodos > 0)
        {
            await RefreshAsync();
        }

        // 状态文案追加本次同步的待办数，便于用户感知 @我 消息已落入待办
        var syncHint = syncedTodos > 0
            ? $"（本次同步新增待办 {syncedTodos} 条）"
            : syncedPersisted > 0
                ? "（本次同步无新增 @我 待办）"
                : string.Empty;
        SummaryText = $"今天微信消息 {filteredMessages.Count} 条，当前第 {_wechatMessagePageNumber}/{CalculateWeChatMessagePageCount(page.TotalCount)} 页{syncHint}";
    }

    /// <summary>根据总条数与每页大小计算总页数，至少为 1。</summary>
    private int CalculateWeChatMessagePageCount(int totalCount)
    {
        return Math.Max(1, (int)Math.Ceiling(totalCount / (double)_wechatMessagePageSize));
    }

    /// <summary>从下拉框读取用户选择的微信消息每页条数，限制在 1-200 之间。</summary>
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

    /// <summary>读取页码输入框并校验，返回 1 到总页数之间的合法页码；非法时给出提示并返回 null。</summary>
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

    /// <summary>更新微信消息分页文本与页码输入框，反映当前页/总页数/总条数/每页条数。</summary>
    private void UpdateWeChatMessagePagingText()
    {
        var pageCount = CalculateWeChatMessagePageCount(_wechatMessageTotalCount);
        WeChatMessagePageText = $"第 {_wechatMessagePageNumber}/{pageCount} 页，共 {_wechatMessageTotalCount} 条，每页 {_wechatMessagePageSize} 条";
        if (WeChatMessagePageNumberTextBox is not null)
        {
            WeChatMessagePageNumberTextBox.Text = _wechatMessagePageNumber.ToString();
        }
    }

    /// <summary>
    /// 播种示例消息：构造 3 条带 @我 与项目关键词的样本消息，
    /// 走完 @我检测、项目分类、紧急度评分、待办创建的完整流程，便于首次体验与调试。
    /// </summary>
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

    /// <summary>
    /// 从仓储加载采集源设置，并与默认采集源合并：
    /// 已保存的设置覆盖默认开关，未保存的采用默认值。结果填充到 UI 列表。
    /// </summary>
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

    /// <summary>
    /// 加载用户别名：若库为空则播种默认别名集合。
    /// 同时更新当前生效的 @我 别名集合与 UI 列表。
    /// </summary>
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

    /// <summary>
    /// 加载关注群列表：同时更新当前生效的关注群名称集合与 UI 列表。
    /// </summary>
    private async Task LoadFollowedChatsAsync()
    {
        var chats = await _followedChatRepository.GetAllAsync(CancellationToken.None);
        _currentFollowedChats = chats.Select(c => c.ChatName).ToArray();
        _followedChatFilterMode = await _followedChatRepository.GetFilterModeAsync(CancellationToken.None);
        Replace(FollowedChats, chats.Select(c => new FollowedChatRow(c.Id, c.ChatName)));
        if (FollowedChatFilterModeCheckBox is not null)
        {
            FollowedChatFilterModeCheckBox.IsChecked = _followedChatFilterMode == FollowedChatFilterMode.Exclude;
        }
    }

    /// <summary>添加关注群：校验非空后保存并刷新列表。</summary>
    private async void AddFollowedChatButton_Click(object sender, RoutedEventArgs e)
    {
        var chatName = NewFollowedChatTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(chatName))
        {
            SummaryText = "请输入要添加的群名称。";
            return;
        }

        await _followedChatRepository.SaveAsync(chatName, CancellationToken.None);
        if (NewFollowedChatTextBox is not null)
        {
            NewFollowedChatTextBox.Text = "";
        }
        await LoadFollowedChatsAsync();
        SummaryText = $"已添加关注群「{chatName}」，消息流和微信消息将只显示关注的群。";
    }

    /// <summary>删除关注群：需先在列表中选中一行，删除后刷新列表。</summary>
    private async void DeleteFollowedChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (FollowedChatsGrid?.SelectedItem is not FollowedChatRow row)
        {
            SummaryText = "请先选中要删除的关注群。";
            return;
        }

        await _followedChatRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadFollowedChatsAsync();
        SummaryText = $"已删除关注群「{row.ChatName}」。";
    }

    /// <summary>切换关注群过滤模式：勾选=排除列表内群（黑名单），未勾选=只显示列表内群（白名单）。</summary>
    private async void FollowedChatFilterModeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb)
        {
            return;
        }

        var newMode = cb.IsChecked == true ? FollowedChatFilterMode.Exclude : FollowedChatFilterMode.Include;
        if (newMode == _followedChatFilterMode)
        {
            return;
        }

        _followedChatFilterMode = newMode;
        await _followedChatRepository.SetFilterModeAsync(newMode, CancellationToken.None);
        await LoadMessageStreamPageAsync(1, refreshCount: true);
        SummaryText = newMode == FollowedChatFilterMode.Exclude
            ? "已切换为排除模式：将不显示列表中的群消息。"
            : "已切换为只显示模式：将仅显示列表中的群消息。";
    }

    /// <summary>保存采集源设置：清空旧设置后批量写入当前 UI 列表中的设置项。</summary>
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

    /// <summary>添加别名：校验非空后保存并刷新别名列表与当前生效集合。</summary>
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

    /// <summary>删除别名：需先在列表中选中一行，删除后刷新别名集合。</summary>
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

    /// <summary>
    /// 构造消息采集管线：合并默认采集源与用户保存的开关设置，
    /// 装配适配器、仓储、@我检测器、项目分类器、紧急度评分器等依赖。
    /// </summary>
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

    /// <summary>
    /// 执行一次采集：通过信号量串行化避免与实时监听循环重叠，
    /// 确保数据库已初始化、采集收件箱目录存在，然后运行管线并返回结果。
    /// </summary>
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

    /// <summary>
    /// 实时采集后台循环：周期性执行采集，刷新 UI 状态文本与摘要。
    /// 捕获异常后继续循环，仅取消令牌触发时退出，循环间隔由 _liveCaptureInterval 控制。
    /// </summary>
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

    /// <summary>
    /// 停止实时监听：取消循环令牌，等待后台任务结束，释放资源并更新状态文本。
    /// </summary>
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

    /// <summary>
    /// 在 UI 线程刷新看板：若当前已在 UI 线程则直接刷新，
    /// 否则通过 Dispatcher 切换到 UI 线程后刷新。
    /// </summary>
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

    /// <summary>构造窗口文本快照提供者：组合 UI 自动化读取器与屏幕 OCR 读取器。</summary>
    private static IWindowTextSnapshotProvider CreateWindowTextSnapshotProvider()
    {
        return new WindowsOcrWindowTextSnapshotProvider(
            new SystemWindowsAutomationReader(),
            new WindowsScreenOcrReader());
    }

    /// <summary>构造微信可见窗口采集选项：匹配标题含"微信"的窗口，排除本看板自身。</summary>
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

    /// <summary>构造示例消息：按给定偏移分钟数设置发送时间，生成唯一 SourceMessageKey。</summary>
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

    /// <summary>根据项目 ID 返回项目名称，未匹配返回"未分类"。</summary>
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

    /// <summary>清空集合并依次追加新项，用于 ObservableCollection 的批量替换。</summary>
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    /// <summary>
    /// 设置属性字段并在值变更时触发 PropertyChanged 事件，
    /// 利用 [CallerMemberName] 自动推断属性名，供 WPF 数据绑定监听。
    /// </summary>
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

/// <summary>待办列表行数据：用于 UI 绑定展示单条待办。</summary>
public sealed record TodoRow(long Id, string Priority, string Status, string ChatName, string Title, string SourceMessageId, string SentAt);

/// <summary>消息流列表行数据：用于 UI 绑定展示单条入库消息。</summary>
public sealed record MessageRow(string SentAt, string ChatName, string SenderName, string IsMentionMe, string Content);

/// <summary>微信消息列表行数据：用于 UI 绑定展示单条微信原生消息。</summary>
public sealed record WeChatMessageRow(string Content, string ChatName, string SenderName);

/// <summary>支持富文本高亮的消息行：用于显示带 @ 高亮的消息内容。</summary>
public sealed class HighlightableMessageRow : INotifyPropertyChanged
{
    private string _sentAt = "";
    private string _chatName = "";
    private string _senderName = "";
    private string _isMentionMe = "";
    private string _rawContent = "";
    private IEnumerable<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment> _contentSegments = Array.Empty<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SentAt
    {
        get => _sentAt;
        set { _sentAt = value; OnPropertyChanged(); }
    }

    public string ChatName
    {
        get => _chatName;
        set { _chatName = value; OnPropertyChanged(); }
    }

    public string SenderName
    {
        get => _senderName;
        set { _senderName = value; OnPropertyChanged(); }
    }

    public string IsMentionMe
    {
        get => _isMentionMe;
        set { _isMentionMe = value; OnPropertyChanged(); }
    }

    public string RawContent
    {
        get => _rawContent;
        set { _rawContent = value; OnPropertyChanged(); }
    }

    public IEnumerable<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment> ContentSegments
    {
        get => _contentSegments;
        set { _contentSegments = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>支持富文本高亮的微信消息行。</summary>
public sealed class HighlightableWeChatMessageRow : INotifyPropertyChanged
{
    private string _content = "";
    private string _chatName = "";
    private string _senderName = "";
    private string _sentAt = "";
    private IEnumerable<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment> _contentSegments = Array.Empty<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Content
    {
        get => _content;
        set { _content = value; OnPropertyChanged(); }
    }

    public string ChatName
    {
        get => _chatName;
        set { _chatName = value; OnPropertyChanged(); }
    }

    public string SentAt
    {
        get => _sentAt;
        set { _sentAt = value; OnPropertyChanged(); }
    }

    public string SenderName
    {
        get => _senderName;
        set { _senderName = value; OnPropertyChanged(); }
    }

    public IEnumerable<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment> ContentSegments
    {
        get => _contentSegments;
        set { _contentSegments = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>项目汇总行数据：按项目聚合的待办数量与高优先级数量。</summary>
public sealed record ProjectSummaryRow(string Project, int PendingTodos, int HighPriorityTodos);

/// <summary>采集诊断行数据：展示各采集适配器的状态与最近成功时间。</summary>
public sealed record DiagnosticRow(string Adapter, string Status, string LastSuccessAt, string Detail);

/// <summary>窗口快照行数据：展示扫描到的微信可见窗口文本快照。</summary>
public sealed record WindowSnapshotRow(string WindowTitle, string CapturedAt, int TextLength, string Preview);

/// <summary>采集源设置行数据：用于 UI 编辑各采集源的启用状态。</summary>
public sealed record CaptureSourceSettingRow
{
    // 采集源标识（如 WeChat.LocalDatabase）
    public string Source { get; set; } = "";
    // 显示名称
    public string DisplayName { get; set; } = "";
    // 采集源类型
    public string Kind { get; set; } = "";
    // 采集源位置/路径
    public string Location { get; set; } = "";
    // 是否启用
    public bool IsEnabled { get; set; }
}

/// <summary>用户别名行数据：用于 UI 展示与删除操作。</summary>
public sealed record UserAliasRow(long Id, string Alias);

/// <summary>关注群行数据：用于 UI 展示与删除操作。</summary>
public sealed record FollowedChatRow(long Id, string ChatName);
