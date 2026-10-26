using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.App.Services;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.App.Views.Todos;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Agent;
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
    private readonly SqliteModelSettingsRepository _modelSettingsRepository;
    private readonly SqliteUserAliasRepository _aliasRepository;
    private readonly SqliteFollowedChatRepository _followedChatRepository;
    private readonly SqliteFollowedProjectRepository _followedProjectRepository;
    private readonly SqliteFollowedProjectKeywordRepository _followedProjectKeywordRepository;
    // JSONL 采集收件箱路径
    private readonly string _captureInboxPath;
    // 当前生效的 @我 别名集合（默认兜底）
    private IReadOnlyList<string> _currentAliases = DefaultMentionAliases.All;
    // 当前生效的关注群名称集合（空列表表示不过滤）
    private IReadOnlyList<string> _currentFollowedChats = Array.Empty<string>();
    // 关注群过滤模式：Include=只显示列表内群，Exclude=排除列表内群
    private FollowedChatFilterMode _followedChatFilterMode = FollowedChatFilterMode.Include;
    // 当前生效的关注项目名称集合（群名包含项目名时，该群消息重点关注）
    private IReadOnlyList<string> _currentFollowedProjects = Array.Empty<string>();
    private IReadOnlyList<FollowedProjectKeyword> _currentFollowedProjectKeywords = Array.Empty<FollowedProjectKeyword>();
    // 微信本地读取器服务
    private readonly WeChatLocalReaderService _readerService;
    // DeepSeek 密钥仅在启动时读取，界面保存后于下次重启生效。
    private readonly ProtectedDeepSeekApiKeyStore _deepSeekApiKeyStore;
    private readonly OpenAiCompatibleMessageActionModel _messageActionModel;
    // 后台采集直接读取该内存开关，复选框无需等待重启或重建管线。
    private volatile bool _analyzeMessageContent;
    // 采集并发控制信号量，避免实时循环与手动采集重叠
    private readonly SemaphoreSlim _captureSemaphore = new(1, 1);
    // 监听空闲时自动降频；托盘后台只做低频增量检查，恢复窗口时立即唤醒。
    private static readonly TimeSpan ForegroundActiveCaptureInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForegroundIdleCaptureInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackgroundActiveCaptureInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackgroundIdleCaptureInterval = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _liveCaptureWakeSignal = new(0, 1);
    private const int DefaultWeChatMessagePageSize = 50;
    private const int DashboardMessageSampleLimit = 5000;

    // 实时监听取消令牌
    private CancellationTokenSource? _liveCaptureCts;
    // 实时监听后台任务
    private Task? _liveCaptureTask;
    private volatile bool _isBackgroundMode;
    private int _capturePipelineVersion;
    // 系统托盘图标及其右键菜单
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly System.Drawing.Icon? _ownedTrayIcon;
    // 开机自动启动（当前用户 Run 注册表项）
    private readonly AutoStartService _autoStartService;
    private readonly Forms.ToolStripMenuItem _autoStartMenuItem;
    private DesktopCalendarWindow? _desktopCalendarWindow;
    // 从托盘恢复时保留最小化前的窗口状态
    private WindowState _windowStateBeforeMinimize = WindowState.Normal;
    // 关闭前异步清理只执行一次
    private bool _isClosingAfterCleanup;
    // 只有托盘“退出”或系统结束会话时才真正关闭；标题栏关闭按钮仅隐藏到托盘。
    private bool _isExitRequested;
    private IReadOnlyDictionary<string, CaptureSourceRunResult> _latestCaptureSourceResults =
        new Dictionary<string, CaptureSourceRunResult>(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _captureSourceLastSuccessAt =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _captureSourceBaseDetails =
        new(StringComparer.Ordinal);

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
    private bool _wechatMessagesLoaded;
    private string _wechatMessagePageText = "第 0/0 页，共 0 条";
    private string _wechatMessageStatusText = "默认读取当天微信消息。请先提取 DB Key 并初始化本地库。";
    private string _dashboardStatusText = "按项目统计最近消息。";
    private string _dashboardChartType = "bar";
    // 模型配置状态只描述保存情况，不包含或回显 API Key。
    private string _modelConfigStatusText = "尚未保存本机 DeepSeek API Key。";
    public TodoFeatureCoordinator TodoFeature { get; }
    public DesktopCalendarViewModel ScheduleOverview { get; }

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
        _modelSettingsRepository = new SqliteModelSettingsRepository(_databasePath);
        _aliasRepository = new SqliteUserAliasRepository(_databasePath);
        _followedChatRepository = new SqliteFollowedChatRepository(_databasePath);
        _followedProjectRepository = new SqliteFollowedProjectRepository(_databasePath);
        _followedProjectKeywordRepository = new SqliteFollowedProjectKeywordRepository(_databasePath);
        _captureInboxPath = ProjectToolPaths.CaptureInboxDirectory;
        _readerService = new WeChatLocalReaderService();

        // 必须先读取启动配置，再创建消息建议 Agent，确保重启后使用新密钥。
        _deepSeekApiKeyStore = new ProtectedDeepSeekApiKeyStore();
        string? savedDeepSeekApiKey = null;
        try
        {
            savedDeepSeekApiKey = _deepSeekApiKeyStore.Load();
            _modelConfigStatusText = savedDeepSeekApiKey is null
                ? "尚未保存本机 DeepSeek API Key。"
                : "已加载本机 DeepSeek API Key；密钥不会回显。";
        }
        catch (InvalidOperationException exception)
        {
            // 密钥读取失败不影响其他页面，本次仍保留原环境变量配置能力。
            _modelConfigStatusText = $"{exception.Message} 本次将尝试使用环境变量配置。";
        }

        _messageActionModel = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment(savedDeepSeekApiKey);
        TodoFeature = new TodoFeatureCoordinator(
            _databasePath,
            _messageActionModel,
            () => _currentAliases,
            () => _currentFollowedChats,
            () => _followedChatFilterMode,
            tabIndex =>
            {
                MainContentTabs.SelectedIndex = tabIndex;
                SidebarNavigation.SelectedItem = SidebarNavigation.Items
                    .OfType<ListBoxItem>()
                    .FirstOrDefault(item => item.Tag?.ToString() == tabIndex.ToString());
            },
            async () => await RefreshAsync());
        ScheduleOverview = new DesktopCalendarViewModel(
            _todoRepository,
            SystemClock.Instance,
            TimeZoneInfo.Local,
            TodoFeature.Workbench.OpenTodoAsync,
            TodoFeature.RefreshAsync,
            _followedProjectRepository,
            _messageRepository);

        DatabasePath = _databasePath;
        CaptureInboxPath = _captureInboxPath;
        // 设置数据上下文为本窗口，使 XAML 绑定生效
        DataContext = this;

        var showMenuItem = new Forms.ToolStripMenuItem("显示");
        showMenuItem.Click += (_, _) => ShowMainWindowFromTray();
        var exitMenuItem = new Forms.ToolStripMenuItem("退出");
        exitMenuItem.Click += (_, _) => ExitFromTray();
        // 勾选状态以注册表为准，菜单每次打开时刷新，避免与其他方式修改后状态不一致。
        _autoStartService = AutoStartService.CreateDefault();
        _autoStartMenuItem = new Forms.ToolStripMenuItem("开机自动启动")
        {
            Checked = _autoStartService.IsEnabled()
        };
        _autoStartMenuItem.Click += (_, _) => ToggleAutoStart();

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add(showMenuItem);
        _trayMenu.Items.Add(_autoStartMenuItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitMenuItem);
        _trayMenu.Opening += (_, _) => _autoStartMenuItem.Checked = _autoStartService.IsEnabled();
        _ownedTrayIcon = LoadApplicationIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = Title,
            Icon = _ownedTrayIcon ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindowFromTray();
        System.Windows.Application.Current.SessionEnding += Application_SessionEnding;
    }

    /// <summary>Switches the content page from the selected sidebar item.</summary>
    private async void SidebarNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainContentTabs is null ||
            SidebarNavigation.SelectedItem is not ListBoxItem selectedItem ||
            !int.TryParse(selectedItem.Tag?.ToString(), out var tabIndex))
        {
            return;
        }

        MainContentTabs.SelectedIndex = tabIndex;
        switch (tabIndex)
        {
            case 0:
            case 1:
                await TodoFeature.Workbench.RefreshAsync();
                break;
            case 2:
                await TodoFeature.MessageFeed.RefreshAsync();
                break;
            case 3 when !_wechatMessagesLoaded:
                await LoadTodayWeChatMessagesAsync(1, ensureInitialized: true);
                break;
            case 8 when ScheduleOverview is not null:
                await ScheduleOverview.RefreshAsync();
                break;
        }
    }

    private async void DesktopCalendarButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_desktopCalendarWindow is null)
            {
                var stateStore = new DesktopCalendarStateStore(
                    Path.Combine(ProjectToolPaths.UserDataRoot, "desktop-calendar.json"));
                _desktopCalendarWindow = new DesktopCalendarWindow(ScheduleOverview, stateStore);
                _desktopCalendarWindow.Closed += (_, _) => _desktopCalendarWindow = null;
            }

            await _desktopCalendarWindow.ShowWidgetAsync();
        }
        catch (Exception ex)
        {
            var failedWindow = _desktopCalendarWindow;
            _desktopCalendarWindow = null;

            try
            {
                failedWindow?.Shutdown();
            }
            catch
            {
                // Keep the main window alive even if cleanup of a partially rendered widget fails.
            }

            MessageBox.Show(
                this,
                $"桌面日历打开失败：{ex.Message}",
                "桌面日历",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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


    public string DashboardStatusText
    {
        get => _dashboardStatusText;
        private set => SetField(ref _dashboardStatusText, value);
    }
    public string DashboardChartType
    {
        get => _dashboardChartType;
        private set => SetField(ref _dashboardChartType, value);
    }

    /// <summary>模型密钥保存状态，不承载任何敏感内容。</summary>
    public string ModelConfigStatusText
    {
        get => _modelConfigStatusText;
        private set => SetField(ref _modelConfigStatusText, value);
    }


    // 微信原生消息列表（UI 绑定）
    public ObservableCollection<WeChatMessageRow> WeChatMessages { get; } = new();

    // 支持高亮的微信消息列表（UI 绑定）
    public ObservableCollection<HighlightableWeChatMessageRow> HighlightableWeChatMessages { get; } = new();

    // 项目汇总列表（UI 绑定）
    public ObservableCollection<ProjectSummaryRow> ProjectSummaries { get; } = new();

    // 项目看板统计图行（UI 绑定）
    public ObservableCollection<DashboardChartRow> DashboardChartRows { get; } = new();

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

    // 关注项目列表（UI 绑定）
    public ObservableCollection<FollowedProjectRow> FollowedProjects { get; } = new();
    // 项目关键字列表（UI 绑定）
    public ObservableCollection<FollowedProjectKeywordRow> FollowedProjectKeywords { get; } = new();
    public IReadOnlyList<ProjectColorOption> ProjectColors { get; } =
    [
        new("灰色", ProjectColorPolicy.DefaultColor),
        new("红色", ProjectColorPolicy.Red),
        new("橙色", ProjectColorPolicy.Orange),
        new("绿色", ProjectColorPolicy.Green),
        new("蓝色", ProjectColorPolicy.Blue),
        new("紫色", ProjectColorPolicy.Purple),
        new("粉色", ProjectColorPolicy.Pink)
    ];

    /// <summary>窗口加载完成：初始化数据库、加载采集源设置，并在读取器就绪时读取当天消息。</summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded 在首帧绘制前触发。先让出 Dispatcher，确保窗口和加载状态能够立即显示。
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);
        await InitializeAndRefreshAsync(seedIfEmpty: true);
        // 必须在实时监听启动前恢复开关，避免首批消息使用错误状态。
        await LoadModelSettingsAsync();
        await TodoFeature.StartAsync();
        ApplyBootstrapRange();
        await Task.Run(() => _readerService.TryRestoreProtectedKeyAsync(CancellationToken.None));
        await LoadCaptureSourceSettingsAsync();
        if (_readerService.IsInitialized)
        {
            WeChatMessageStatusText = "微信读取器已就绪；打开“微信消息”页时加载当天消息。";
        }

        await StartLiveListenerAsync();
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
        // 自动路径只尝试 DPAPI 缓存，不自动启动 KeyProbe/Hook。
        if (!_readerService.IsInitialized && _readerService.IsAvailable)
        {
            ApplyBootstrapRange();
            SummaryText = "正在从受保护缓存恢复微信 DB Key...";
            var initSuccess = await _readerService.TryRestoreProtectedKeyAsync(CancellationToken.None);
            if (initSuccess)
            {
                SummaryText = "本地数据库初始化成功，重新加载采集源...";
                await LoadCaptureSourceSettingsAsync();
            }
            else
            {
                SummaryText = $"部分失败：微信未初始化（{_readerService.LastError ?? "请点击“自动提取 Key”"}），其他采集源将继续运行。";
            }
        }

        var result = await RunCaptureOnceAsync(CancellationToken.None);
        UpdateCaptureSourceResults(result.Sources);
        SummaryText = FormatCaptureSummary("本次采集", result);
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

            SummaryText = $"原生读取器初始化成功（微信 PID {result.TargetProcessId}），Key 已用 Windows 当前用户凭据加密保存。";
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

    }

    /// <summary>启动微信实时监听循环。</summary>
    private async void StartWeChatListenerButton_Click(object sender, RoutedEventArgs e)
    {
        await StartLiveListenerAsync();
    }

    private async Task StartLiveListenerAsync()
    {
        if (_liveCaptureTask is { IsCompleted: false })
        {
            ListenerStatusText = "微信监听已在运行";
            return;
        }

        ApplyBootstrapRange();
        if (_readerService.IsAvailable && !_readerService.IsInitialized)
        {
            await Task.Run(() => _readerService.TryRestoreProtectedKeyAsync(CancellationToken.None));
        }

        var liveCaptureCts = new CancellationTokenSource();
        _liveCaptureCts = liveCaptureCts;
        // SQLite、目录枚举、快照和消息解析包含同步工作；整个循环必须从 UI 线程移出。
        _liveCaptureTask = Task.Run(() => RunLiveCaptureLoopAsync(liveCaptureCts.Token));
        ListenerStatusText = _readerService.IsInitialized
            ? "监听已启动：前台空闲 15 秒检查一次，托盘后台空闲 2 分钟检查一次"
            : $"监听已启动（部分失败）：微信未初始化；{_readerService.LastError ?? "请点击“自动提取 Key”"}。其他采集源继续运行。";
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

    /// <summary>最小化主窗口时隐藏任务栏窗口，继续在系统托盘运行。</summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            _isBackgroundMode = true;
            Hide();
            return;
        }

        _isBackgroundMode = false;
        _windowStateBeforeMinimize = WindowState;
    }

    /// <summary>从托盘恢复并激活主窗口。</summary>
    private void ShowMainWindowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowMainWindowFromTray);
            return;
        }

        Show();
        _isBackgroundMode = false;
        WindowState = _windowStateBeforeMinimize;
        Activate();
        WakeLiveCaptureLoop();
    }

    /// <summary>通过托盘菜单退出应用。</summary>
    private void ExitFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ExitFromTray);
            return;
        }

        _isExitRequested = true;
        Close();
    }

    /// <summary>切换开机自动启动；失败时保持原状态并提示原因。</summary>
    private void ToggleAutoStart()
    {
        var result = _autoStartService.SetEnabled(!_autoStartMenuItem.Checked);
        _autoStartMenuItem.Checked = result.IsEnabled;
        if (result.Success)
        {
            return;
        }

        // 托盘场景下主窗口处于隐藏状态，这里不使用窗口所有者，避免提示框被隐藏窗口影响。
        MessageBox.Show(
            result.ErrorMessage ?? "开机自动启动设置失败。",
            "开机自动启动",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>标题栏关闭按钮隐藏到托盘；显式退出时停止监听并释放资源。</summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterCleanup)
        {
            return;
        }

        e.Cancel = true;
        if (!_isExitRequested)
        {
            _isBackgroundMode = true;
            Hide();
            return;
        }

        _isClosingAfterCleanup = true;
        _trayIcon.Visible = false;
        _desktopCalendarWindow?.Shutdown();
        _desktopCalendarWindow = null;

        try
        {
            await TodoFeature.StopAsync();
            await StopLiveCaptureAsync();
            await _readerService.DisposeAsync();
        }
        finally
        {
            System.Windows.Application.Current.SessionEnding -= Application_SessionEnding;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            _ownedTrayIcon?.Dispose();
            Closing -= MainWindow_Closing;
            Close();
        }
    }

    private void Application_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _isExitRequested = true;
    }

    private static System.Drawing.Icon? LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>初始化数据库并刷新；seedIfEmpty 控制首次空库时是否播种示例。</summary>
    private async Task InitializeAndRefreshAsync(bool seedIfEmpty)
    {
        await Task.Run(() => _databaseInitializer.InitializeAsync(CancellationToken.None));
        await LoadUserAliasesAsync();
        await LoadFollowedChatsAsync();
        await LoadFollowedProjectsAsync();
        if (seedIfEmpty)
        {
            // 空库时播种示例，便于首次体验
            var existingMessages = await Task.Run(
                () => _messageRepository.GetRecentAsync(1, CancellationToken.None));
            if (existingMessages.Count == 0)
            {
                await Task.Run(SeedSampleMessagesAsync);
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
        var dashboardTask = CreateDashboardSnapshotAsync(
            ReadSelectedDashboardDimension(),
            DashboardChartType);
        var pendingTodosTask = Task.Run(() => _todoRepository.GetPendingAsync(CancellationToken.None));
        var managedProjectsTask = Task.Run(() => _followedProjectRepository.GetAllAsync(CancellationToken.None));
        var messageCountTask = Task.Run(() => _messageRepository.GetMessageCountAsync(CancellationToken.None));
        var dataDirectoryTask = Task.Run(WeChatDataDirectoryLocator.Locate);
        var shihuatongReadyTask = Task.Run(() => ShihuatongLocalDatabaseCaptureAdapter.IsProcessRunning);
        await Task.WhenAll(
            dashboardTask,
            pendingTodosTask,
            managedProjectsTask,
            messageCountTask,
            dataDirectoryTask,
            shihuatongReadyTask);

        var dashboard = dashboardTask.Result;
        var recentMessages = dashboard.RecentMessages;
        var pendingTodos = pendingTodosTask.Result;
        var managedProjectNames = managedProjectsTask.Result.ToDictionary(project => project.Id, project => project.ProjectName);
        // 采集源：微信本地数据库（基于 wechat-local-reader 解密）
        var localDatabaseSource = CaptureAdapterFactory.CreateWeChatLocalDatabaseSource(_readerService);
        var localDatabaseConfigPath = CaptureAdapterFactory.GetWeChatLocalDatabaseConfigPath();
        // 微信数据目录（db_storage），用于初始化本地数据库采集源
        var dataDir = dataDirectoryTask.Result;
        // 计算本地数据库采集源的状态文案
        var localDbStatus = _readerService.IsInitialized ? "已就绪"
            : _readerService.IsAvailable ? (_readerService.LastError ?? "等待初始化")
            : "未安装";
        var localDbDetail = _readerService.IsInitialized
            ? $"配置: {localDatabaseConfigPath} | 历史范围: {_readerService.BootstrapRange}"
            : dataDir is not null
                ? $"数据目录已检测: {dataDir} | 历史范围: {_readerService.BootstrapRange}，点击\"初始化本地库\"按钮完成设置"
                : _readerService.IsAvailable
                    ? "未检测到微信数据目录，请确认微信正在运行"
                    : localDatabaseSource.Location;

        // 项目汇总：按项目分组统计待办数量与高优先级数量
        Replace(ProjectSummaries, pendingTodos
            .GroupBy(todo => todo.ManagedProjectId is { } projectId && managedProjectNames.TryGetValue(projectId, out var projectName)
                ? projectName
                : "未指定项目")
            .Select(group => new ProjectSummaryRow(
                group.Key,
                group.Count(),
                group.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1))));

        // 采集诊断列表：展示各采集源的可用性与状态
        var shihuatongLocalDatabaseReady = shihuatongReadyTask.Result;
        var diagnosticRows = new[]
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
            new DiagnosticRow(
                "Shihuatong.LocalDatabase",
                shihuatongLocalDatabaseReady ? "已就绪" : "等待石化通运行",
                shihuatongLocalDatabaseReady ? DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "-",
                "只读获取进程内数据库密钥并读取本地加密消息库，不使用可见窗口"),
            new DiagnosticRow("DingTalk.JsonlDirectory", "可用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), Path.Combine(_captureInboxPath, "DingTalk")),
            new DiagnosticRow("WeChat.WindowText", "已启用", DateTimeOffset.Now.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), "UIA+OCR 读取可见微信窗口，实时采集，窗口最小化后不可用"),
            new DiagnosticRow("WindowsNotificationAdapter", "未启用", "-", "待接入 Windows 通知监听")
        };
        foreach (var row in diagnosticRows)
        {
            _captureSourceBaseDetails[row.Adapter] = row.Detail;
        }
        Replace(Diagnostics, diagnosticRows.Select(ApplyLatestCaptureSourceResult));
        Diagnostics.Remove(Diagnostics.First(row => row.Adapter == "WeChat.WindowText"));

        // 顶部摘要统计：今日消息数、@我数、待办理数、高优先级待办数
        TodayMessageCount = recentMessages.Count(message => message.SentAt.LocalDateTime.Date == DateTime.Today);
        MentionCount = recentMessages.Count(message => message.IsMentionMe);
        PendingTodoCount = pendingTodos.Count;
        HighPriorityTodoCount = pendingTodos.Count(todo => todo.Priority is PriorityLevel.P0 or PriorityLevel.P1);

        // 刷新项目看板图表和消息流分页（强制重新计算总数）
        ApplyDashboardSnapshot(dashboard);
        await RefreshSelectedFeatureAsync();
        SummaryText = $"消息 {messageCountTask.Result} | @我 {MentionCount} | 待办理 {PendingTodoCount} | 高优先级 {HighPriorityTodoCount}";
    }

    private Task RefreshSelectedFeatureAsync() => MainContentTabs.SelectedIndex switch
    {
        0 or 1 => TodoFeature.Workbench.RefreshAsync(),
        2 => TodoFeature.MessageFeed.RefreshAsync(),
        8 => ScheduleOverview.RefreshAsync(),
        _ => Task.CompletedTask
    };

    /// <summary>项目看板维度切换：按项目、时间或群名重新统计。</summary>
    private async void DashboardDimensionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        await LoadProjectDashboardAsync();
    }
    /// <summary>项目看板图表类型切换：柱状、环形占比或趋势折线图。</summary>
    private async void DashboardChartTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DashboardChartType = ReadSelectedDashboardChartType();
        if (!IsLoaded)
        {
            return;
        }

        await LoadProjectDashboardAsync();
    }


    /// <summary>加载项目看板统计图，默认统计最近一批入库消息。</summary>
    private async Task LoadProjectDashboardAsync()
    {
        var dimension = ReadSelectedDashboardDimension();
        var snapshot = await CreateDashboardSnapshotAsync(dimension, DashboardChartType);
        ApplyDashboardSnapshot(snapshot);
    }

    private Task<DashboardSnapshot> CreateDashboardSnapshotAsync(string dimension, string chartType) =>
        Task.Run(async () =>
        {
            var messages = await _messageRepository.GetRecentAsync(DashboardMessageSampleLimit, CancellationToken.None);
            var rows = BuildDashboardRows(messages, dimension);
            if (dimension == "time" && chartType == "line")
            {
                // 时间聚合默认按最近日期优先；折线图需要按时间正序连接数据点。
                rows = rows.Reverse().ToArray();
            }

            var dimensionText = dimension switch
            {
                "time" => "时间",
                "chat" => "群名",
                _ => "项目"
            };
            var chartTypeText = chartType switch
            {
                "pie" => "环形占比图",
                "line" => "趋势折线图",
                _ => "横向柱状图"
            };
            var status = rows.Count == 0
                ? $"暂无可统计消息。当前维度：按{dimensionText}。"
                : $"按{dimensionText}统计最近 {messages.Count} 条入库消息，使用{chartTypeText}显示前 {rows.Count} 项。";
            return new DashboardSnapshot(messages.Take(100).ToArray(), rows, status);
        });

    private void ApplyDashboardSnapshot(DashboardSnapshot snapshot)
    {
        Replace(DashboardChartRows, snapshot.Rows);
        DashboardStatusText = snapshot.StatusText;
    }

    private string ReadSelectedDashboardDimension()
    {
        if (DashboardDimensionComboBox?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag)
        {
            return tag;
        }

        return "project";
    }

    private string ReadSelectedDashboardChartType()
    {
        if (DashboardChartTypeComboBox?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag)
        {
            return tag;
        }

        return "bar";
    }

    private IReadOnlyList<DashboardChartRow> BuildDashboardRows(IReadOnlyList<Message> messages, string dimension)
    {
        if (messages.Count == 0)
        {
            return Array.Empty<DashboardChartRow>();
        }

        var aggregates = dimension switch
        {
            "time" => BuildTimeDashboardAggregates(messages),
            "chat" => BuildChatDashboardAggregates(messages),
            _ => BuildProjectDashboardAggregates(messages)
        };

        var materialized = aggregates.ToList();
        if (materialized.Count == 0)
        {
            return Array.Empty<DashboardChartRow>();
        }

        var maxCount = Math.Max(1, materialized.Max(row => row.Count));
        return materialized
            .Select(row => new DashboardChartRow(
                row.Label,
                row.Count,
                $"{row.Count} 条",
                row.Detail,
                Math.Round(row.Count * 100.0 / maxCount, 1)))
            .ToArray();
    }

    private IReadOnlyList<DashboardAggregate> BuildProjectDashboardAggregates(IReadOnlyList<Message> messages)
    {
        return messages
            .GroupBy(ResolveDashboardProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var chats = group
                    .Select(message => message.ChatName)
                    .Where(chatName => !string.IsNullOrWhiteSpace(chatName))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(chatName => chatName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var sampleChats = chats.Take(3).ToArray();
                var detail = chats.Length == 0
                    ? "未识别群名"
                    : $"覆盖 {chats.Length} 个群：{string.Join("、", sampleChats)}{(chats.Length > sampleChats.Length ? " 等" : "")}";
                return new DashboardAggregate(group.Key, group.Count(), detail);
            })
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    private IReadOnlyList<DashboardAggregate> BuildTimeDashboardAggregates(IReadOnlyList<Message> messages)
    {
        return messages
            .GroupBy(message => message.SentAt.LocalDateTime.Date)
            .OrderByDescending(group => group.Key)
            .Take(14)
            .Select(group =>
            {
                var mentionCount = group.Count(message => message.IsMentionMe);
                var chatCount = group.Select(message => message.ChatName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return new DashboardAggregate(
                    group.Key.ToString("MM-dd"),
                    group.Count(),
                    $"{group.Key:yyyy-MM-dd}，{chatCount} 个群，@我 {mentionCount} 条");
            })
            .ToArray();
    }

    private IReadOnlyList<DashboardAggregate> BuildChatDashboardAggregates(IReadOnlyList<Message> messages)
    {
        return messages
            .GroupBy(message => string.IsNullOrWhiteSpace(message.ChatName) ? "未命名群聊" : message.ChatName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var projectName = ResolveDashboardProjectName(group.First());
                var mentionCount = group.Count(message => message.IsMentionMe);
                return new DashboardAggregate(
                    group.Key,
                    group.Count(),
                    $"项目：{projectName}，@我 {mentionCount} 条");
            })
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    private string ResolveDashboardProjectName(Message message)
    {
        var followedProjectName = MatchFollowedProject(message);
        if (!string.IsNullOrWhiteSpace(followedProjectName))
        {
            return followedProjectName;
        }

        return CreateProjectClassifier().Classify(message).ProjectName;
    }

    /// <summary>
    /// 加载当天微信消息分页。
    /// ensureInitialized=true 时若读取器未初始化则仅尝试恢复 DPAPI 密文缓存，
    /// 不会自动启动 KeyProbe；缓存不可用时明确提示用户重新提取 Key。
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

            SummaryText = "正在从受保护缓存恢复微信 DB Key...";
            var initialized = await Task.Run(
                () => _readerService.TryRestoreProtectedKeyAsync(CancellationToken.None));
            if (!initialized)
            {
                var error = _readerService.LastError ?? "未知错误";
                WeChatMessageStatusText = $"微信未初始化：{error}";
                SummaryText = $"微信未初始化：{error}";
                return;
            }

            await LoadCaptureSourceSettingsAsync();
        }

        _wechatMessagePageSize = ReadSelectedWeChatMessagePageSize();
        var safePage = Math.Max(1, pageNumber);
        WeChatMessageStatusText = $"正在读取今天微信消息第 {safePage} 页，每页 {_wechatMessagePageSize} 条...";
        var readOptions = new WeChatLocalMessageReadOptions(DateTime.Today, safePage, _wechatMessagePageSize);
        var page = await Task.Run(
            () => _readerService.ReadMessagesAsync(readOptions, CancellationToken.None));
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
        _wechatMessagesLoaded = true;

        // 将本页读取到的微信消息同步入库：去重持久化，并为 @我 的消息自动创建待办。
        // 复用采集管线的"去重 -> 分类 -> 评分 -> 建待办"流程，SourceMessageKey 去重避免重复建待办。
        // 处理的是本地读取器原始消息（未经过关注群过滤），与采集管线行为保持一致。
        // 通过 _captureSemaphore 串行化，避免与实时监听循环并发写入导致重复入库/重复建待办。
        int syncedTodos = 0;
        int syncedPersisted = 0;
        if (page.Messages.Count > 0)
        {
            var syncPipeline = CreateCapturePipeline();
            var syncResult = await Task.Run(async () =>
            {
                await _captureSemaphore.WaitAsync(CancellationToken.None);
                try
                {
                    return await syncPipeline.ProcessAsync(page.Messages, CancellationToken.None);
                }
                finally
                {
                    _captureSemaphore.Release();
                }
            });
            syncedPersisted = syncResult.PersistedCount;
            syncedTodos = syncResult.CreatedTodoCount;
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
        var persistedMessages = await _messageRepository.GetBySourceKeysAsync(
            filteredMessages
                .Select(message => new MessageIdentity(message.Source, message.SourceMessageKey))
                .ToArray(),
            CancellationToken.None);
        var persistedMessageLookup = persistedMessages.ToDictionary(
            message => (message.Source, message.SourceMessageKey),
            message => message.Id);

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
                MessageId = persistedMessageLookup.GetValueOrDefault((message.Source, message.SourceMessageKey)),
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
        var rows = await Task.Run(async () =>
        {
            await _settingsRepository.MigrateKindAsync(
                source: "WeChat",
                oldKind: "WeChatLocalCommand",
                newKind: CaptureSourceKind.WeChatLocalDatabase.ToString(),
                newDisplayName: "微信本地数据库",
                newLocation: "原生 C#（只读本地数据库）",
                CancellationToken.None);
            var savedSettings = await _settingsRepository.GetAllAsync(CancellationToken.None);
            var defaultSources = CaptureAdapterFactory.CreateDefaultLiveSources(_captureInboxPath, _readerService);
            return defaultSources.Select(source =>
            {
                var saved = savedSettings.FirstOrDefault(setting =>
                    setting.Source == source.Source && setting.Kind == source.Kind.ToString());
                return new CaptureSourceSettingRow
                {
                    Source = source.Source,
                    DisplayName = source.DisplayName,
                    Kind = source.Kind.ToString(),
                    Location = source.Location,
                    IsEnabled = saved?.IsEnabled ?? source.IsEnabled
                };
            }).ToArray();
        });

        Replace(CaptureSourceSettings, rows);
    }

    /// <summary>
    /// 加载用户别名：若库为空则播种默认别名集合。
    /// 同时更新当前生效的 @我 别名集合与 UI 列表。
    /// </summary>
    private async Task LoadUserAliasesAsync()
    {
        var aliases = await Task.Run(async () =>
        {
            var storedAliases = await _aliasRepository.GetAllAsync(CancellationToken.None);
            if (storedAliases.Count > 0)
            {
                return storedAliases;
            }

            var seeded = new List<UserAlias>();
            foreach (var alias in DefaultMentionAliases.All)
            {
                seeded.Add(await _aliasRepository.SaveAsync(alias, CancellationToken.None));
            }
            return (IReadOnlyList<UserAlias>)seeded;
        });

        _currentAliases = aliases.Select(alias => alias.Alias).ToArray();
        Replace(Aliases, aliases.Select(alias => new UserAliasRow(alias.Id, alias.Alias)));
    }

    /// <summary>
    /// 加载关注群列表：同时更新当前生效的关注群名称集合与 UI 列表。
    /// </summary>
    private async Task LoadFollowedChatsAsync()
    {
        var snapshot = await Task.Run(async () =>
        {
            var chatsTask = _followedChatRepository.GetAllAsync(CancellationToken.None);
            var modeTask = _followedChatRepository.GetFilterModeAsync(CancellationToken.None);
            await Task.WhenAll(chatsTask, modeTask);
            return (Chats: chatsTask.Result, Mode: modeTask.Result);
        });
        var chats = snapshot.Chats;
        _currentFollowedChats = chats.Select(c => c.ChatName).ToArray();
        _followedChatFilterMode = snapshot.Mode;
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
        await TodoFeature.MessageFeed.RefreshAsync();
        SummaryText = newMode == FollowedChatFilterMode.Exclude
            ? "已切换为排除模式：将不显示列表中的群消息。"
            : "已切换为只显示模式：将仅显示列表中的群消息。";
    }

    /// <summary>加载项目及其关键字，并为旧项目补充以项目名为关键字的兼容项。</summary>
    private async Task LoadFollowedProjectsAsync()
    {
        var snapshot = await Task.Run(async () =>
        {
            var projects = await _followedProjectRepository.GetAllAsync(CancellationToken.None);
            var keywords = await _followedProjectKeywordRepository.GetAllAsync(CancellationToken.None);
            var addedDefaultKeyword = false;
            foreach (var project in projects.Where(project => keywords.All(keyword => keyword.ProjectId != project.Id)))
            {
                await _followedProjectKeywordRepository.SaveAsync(project.Id, project.ProjectName, CancellationToken.None);
                addedDefaultKeyword = true;
            }
            if (addedDefaultKeyword)
            {
                keywords = await _followedProjectKeywordRepository.GetAllAsync(CancellationToken.None);
            }
            return (Projects: projects, Keywords: keywords);
        });
        var projects = snapshot.Projects;
        var keywords = snapshot.Keywords;

        _currentFollowedProjects = projects.Select(project => project.ProjectName).ToArray();
        _currentFollowedProjectKeywords = keywords;
        Replace(FollowedProjects, projects.Select(project => new FollowedProjectRow(project.Id, project.ProjectName, project.Color)));
        Replace(FollowedProjectKeywords, keywords.Select(keyword => new FollowedProjectKeywordRow(keyword.Id, keyword.ProjectId, keyword.ProjectName, keyword.Keyword)));
    }

    /// <summary>添加项目，并将项目名称作为初始关键字以保持既有匹配方式。</summary>
    private async void AddFollowedProjectButton_Click(object sender, RoutedEventArgs e)
    {
        var projectName = NewFollowedProjectTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(projectName))
        {
            SummaryText = "请输入要添加的项目名称。";
            return;
        }

        var color = NewFollowedProjectColorComboBox?.SelectedItem is ProjectColorOption selectedColor
            ? selectedColor.Color
            : ProjectColorPolicy.DefaultColor;
        var project = await _followedProjectRepository.SaveAsync(projectName, color, CancellationToken.None);
        await _followedProjectKeywordRepository.SaveAsync(project.Id, projectName, CancellationToken.None);
        if (NewFollowedProjectTextBox is not null) NewFollowedProjectTextBox.Text = "";
        await LoadFollowedProjectsAsync();
        ProjectKeywordProjectComboBox.SelectedValue = project.Id;
        await LoadProjectDashboardAsync();
        SummaryText = $"已添加项目「{projectName}」，可继续为它添加多个关键字。";
    }

    private async void FollowedProjectColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            comboBox.DataContext is not FollowedProjectRow row ||
            comboBox.SelectedItem is not ProjectColorOption selectedColor ||
            string.Equals(row.Color, selectedColor.Color, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var originalColor = row.Color;
        var selectedKeywordProjectId = ProjectKeywordProjectComboBox?.SelectedValue is long projectId
            ? projectId
            : (long?)null;
        comboBox.IsEnabled = false;
        try
        {
            var outcome = await _followedProjectRepository.UpdateColorAsync(
                row.Id,
                selectedColor.Color,
                CancellationToken.None);
            if (outcome == UpdateFollowedProjectColorOutcome.Missing)
            {
                await LoadFollowedProjectsAsync();
                if (selectedKeywordProjectId.HasValue && ProjectKeywordProjectComboBox is { } missingProjectComboBox)
                {
                    missingProjectComboBox.SelectedValue = selectedKeywordProjectId.Value;
                }
                SummaryText = $"项目「{row.ProjectName}」已不存在，颜色未保存，项目列表已刷新。";
                return;
            }

            await LoadFollowedProjectsAsync();
            if (selectedKeywordProjectId.HasValue && ProjectKeywordProjectComboBox is { } keywordProjectComboBox)
            {
                keywordProjectComboBox.SelectedValue = selectedKeywordProjectId.Value;
            }
            await Task.WhenAll(
                TodoFeature.Workbench.RefreshAsync(),
                ScheduleOverview.RefreshAsync());
            SummaryText = outcome == UpdateFollowedProjectColorOutcome.Updated
                ? $"已将项目「{row.ProjectName}」的颜色更新为{selectedColor.Name}。"
                : $"项目「{row.ProjectName}」的颜色未变化。";
        }
        catch (Exception)
        {
            comboBox.SelectedValue = originalColor;
            SummaryText = $"项目「{row.ProjectName}」颜色保存失败，已恢复原颜色。";
        }
        finally
        {
            comboBox.IsEnabled = true;
        }
    }

    /// <summary>删除项目以及它关联的所有关键字。</summary>
    private async void DeleteFollowedProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (FollowedProjectsGrid?.SelectedItem is not FollowedProjectRow row)
        {
            SummaryText = "请先选中要删除的项目。";
            return;
        }

        await _followedProjectKeywordRepository.DeleteByProjectIdAsync(row.Id, CancellationToken.None);
        await _followedProjectRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadFollowedProjectsAsync();
        await TodoFeature.Workbench.RefreshAsync();
        await ScheduleOverview.RefreshAsync();
        await LoadProjectDashboardAsync();
        SummaryText = $"已删除项目「{row.ProjectName}」及其关键字。";
    }

    /// <summary>向选中项目追加关键字。</summary>
    private async void AddFollowedProjectKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectKeywordProjectComboBox?.SelectedItem is not FollowedProjectRow project)
        {
            SummaryText = "请先选择要添加关键字的项目。";
            return;
        }
        var keyword = NewFollowedProjectKeywordTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SummaryText = "请输入项目关键字。";
            return;
        }

        await _followedProjectKeywordRepository.SaveAsync(project.Id, keyword, CancellationToken.None);
        if (NewFollowedProjectKeywordTextBox is not null) NewFollowedProjectKeywordTextBox.Text = "";
        await LoadFollowedProjectsAsync();
        ProjectKeywordProjectComboBox.SelectedValue = project.Id;
        await LoadProjectDashboardAsync();
        SummaryText = $"已为「{project.ProjectName}」添加关键字「{keyword}」。";
    }

    /// <summary>删除选中的关键字；每个项目至少保留一个匹配关键字。</summary>
    private async void DeleteFollowedProjectKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (FollowedProjectKeywordsGrid?.SelectedItem is not FollowedProjectKeywordRow row)
        {
            SummaryText = "请先选中要删除的关键字。";
            return;
        }
        if (_currentFollowedProjectKeywords.Count(keyword => keyword.ProjectId == row.ProjectId) <= 1)
        {
            SummaryText = "每个项目至少保留一个关键字；如不再需要，请删除该项目。";
            return;
        }

        await _followedProjectKeywordRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadFollowedProjectsAsync();
        ProjectKeywordProjectComboBox.SelectedValue = row.ProjectId;
        await LoadProjectDashboardAsync();
        SummaryText = $"已删除项目「{row.ProjectName}」的关键字「{row.Keyword}」。";
    }

    /// <summary>按最长关键字优先匹配群名和消息正文，返回归属项目名称。</summary>
    private string MatchFollowedProject(Message message)
    {
        var match = FollowedProjectMatcher.Match(message, _currentFollowedProjectKeywords);
        return match?.ProjectName ?? "";
    }

    /// <summary>在监听启动前读取模型分析开关，读取失败时安全保持关闭。</summary>
    private async Task LoadModelSettingsAsync()
    {
        try
        {
            _analyzeMessageContent = await _modelSettingsRepository.GetAnalyzeMessageContentAsync(
                CancellationToken.None);
            AnalyzeMessageContentCheckBox.IsChecked = _analyzeMessageContent;
        }
        catch (Exception exception)
        {
            _analyzeMessageContent = false;
            AnalyzeMessageContentCheckBox.IsChecked = false;
            ModelConfigStatusText = "模型分析开关读取失败，本次保持关闭。";
            System.Diagnostics.Debug.WriteLine(
                $"Model settings load failed: {exception.Message}");
        }
    }

    /// <summary>勾选后立即更新内存开关并异步持久化，保存失败时恢复之前的状态。</summary>
    private async void AnalyzeMessageContentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var previousEnabled = _analyzeMessageContent;
        var enabled = AnalyzeMessageContentCheckBox.IsChecked == true;
        // 先更新内存，后续消息从这一刻起按新状态处理。
        _analyzeMessageContent = enabled;
        AnalyzeMessageContentCheckBox.IsEnabled = false;
        try
        {
            await _modelSettingsRepository.SetAnalyzeMessageContentAsync(
                enabled,
                CancellationToken.None);
            ModelConfigStatusText = enabled
                ? "分析消息内容已开启并保存，立即生效；大模型将筛选之后新入库的 @我 消息并生成待办分析。"
                : "分析消息内容已关闭并保存，立即生效；@我 消息仍按原逻辑自动创建待办。";
        }
        catch (Exception exception)
        {
            // UI 与后台采集同时回滚，避免界面状态和实际行为不一致。
            _analyzeMessageContent = previousEnabled;
            AnalyzeMessageContentCheckBox.IsChecked = previousEnabled;
            ModelConfigStatusText =
                $"分析开关保存失败，已恢复原状态：{exception.Message}";
        }
        finally
        {
            AnalyzeMessageContentCheckBox.IsEnabled = true;
        }
    }

    /// <summary>加密保存 DeepSeek API Key；当前进程继续使用启动时配置。</summary>
    private void SaveModelConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = DeepSeekApiKeyPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ModelConfigStatusText = "请输入 DeepSeek API Key；空内容不会覆盖已保存密钥。";
            return;
        }

        try
        {
            _deepSeekApiKeyStore.Save(apiKey);
            // 保存后立刻清除掩码输入，避免密钥继续停留在界面控件中。
            DeepSeekApiKeyPasswordBox.Clear();
            ModelConfigStatusText = "DeepSeek API Key 已加密保存，重启应用后生效。";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // 存储错误信息不包含密钥；保存失败时保留输入以便用户重试。
            ModelConfigStatusText = exception.Message;
        }
    }

    /// <summary>清除本机密钥，重启后回退到环境变量配置（如有）。</summary>
    private void ClearModelConfigButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _deepSeekApiKeyStore.Clear();
            DeepSeekApiKeyPasswordBox.Clear();
            ModelConfigStatusText = "本机 DeepSeek API Key 已清除，重启后生效；届时将使用环境变量配置（如有）。";
        }
        catch (InvalidOperationException exception)
        {
            ModelConfigStatusText = exception.Message;
        }
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
        Interlocked.Increment(ref _capturePipelineVersion);
        WakeLiveCaptureLoop();
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
                windowTextSnapshotProvider: null,
                weChatLocalDatabaseReader: _readerService.NativeReader),
            new SqliteMessageProcessingUnitOfWork(_databasePath),
            _offsetRepository,
            new MentionDetector(_currentAliases),
            CreateProjectClassifier(),

            new UrgencyRanker(
                priorityContacts: new[] { "王经理", "赵经理" },
                priorityProjectIds: new[] { 1L, 2L }),
            managedProjectMatcher: message => FollowedProjectMatcher.Match(
                message,
                _currentFollowedProjectKeywords)?.ProjectId,
            messageRepository: _messageRepository,
            analyzeMessageContent: () => _analyzeMessageContent,
            // 单次 Agent 调用同时判断创建必要性并生成优先级和说明，不影响消息流评分。
            analyzeMentionAsync: _messageActionModel.AnalyzeMentionAsync);
    }

    /// <summary>
    /// 执行一次采集：通过信号量串行化避免与实时监听循环重叠，
    /// 确保数据库已初始化、采集收件箱目录存在，然后运行管线并返回结果。
    /// </summary>
    private async Task<CaptureRunResult> RunCaptureOnceAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken);
        Directory.CreateDirectory(_captureInboxPath);
        return await RunCaptureOnceAsync(CreateCapturePipeline(), cancellationToken);
    }

    private async Task<CaptureRunResult> RunCaptureOnceAsync(
        MessageCapturePipeline pipeline,
        CancellationToken cancellationToken)
    {
        await _captureSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await pipeline.RunOnceAsync(cancellationToken);
        }
        finally
        {
            _captureSemaphore.Release();
        }
    }

    /// <summary>
    /// 实时采集后台循环：复用采集管线，并根据前台/托盘状态及本轮是否有消息自动调节频率。
    /// 捕获异常后继续循环；恢复窗口或保存采集源设置时可立即唤醒下一轮。
    /// </summary>
    private async Task RunLiveCaptureLoopAsync(CancellationToken cancellationToken)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken);
        Directory.CreateDirectory(_captureInboxPath);
        while (_liveCaptureWakeSignal.Wait(0)) { }
        var pipelineVersion = Volatile.Read(ref _capturePipelineVersion);
        var pipeline = CreateCapturePipeline();

        while (!cancellationToken.IsCancellationRequested)
        {
            var nextDelay = _isBackgroundMode
                ? BackgroundIdleCaptureInterval
                : ForegroundIdleCaptureInterval;
            try
            {
                var currentPipelineVersion = Volatile.Read(ref _capturePipelineVersion);
                if (currentPipelineVersion != pipelineVersion)
                {
                    pipeline = CreateCapturePipeline();
                    pipelineVersion = currentPipelineVersion;
                }

                var result = await RunCaptureOnceAsync(pipeline, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateCaptureSourceResults(result.Sources);
                    ListenerStatusText = FormatListenerStatus(result);
                    SummaryText = FormatCaptureSummary("监听采集", result);
                });

                if (result.PersistedCount > 0 || result.CreatedTodoCount > 0)
                {
                    await RefreshOnUiThreadAsync();
                }

                nextDelay = ResolveLiveCaptureDelay(result);
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
                await _liveCaptureWakeSignal.WaitAsync(nextDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

        }
    }

    private void UpdateCaptureSourceResults(IReadOnlyList<CaptureSourceRunResult> sourceResults)
    {
        _latestCaptureSourceResults = sourceResults.ToDictionary(result => result.AdapterName, StringComparer.Ordinal);
        foreach (var source in sourceResults.Where(source =>
                     source.Status == CaptureSourceRunStatus.Succeeded ||
                     (source.Status == CaptureSourceRunStatus.Empty &&
                      !_captureSourceLastSuccessAt.ContainsKey(source.AdapterName))))
        {
            _captureSourceLastSuccessAt[source.AdapterName] = source.CompletedAt;
        }

        for (var index = 0; index < Diagnostics.Count; index++)
        {
            var updated = ApplyLatestCaptureSourceResult(Diagnostics[index]);
            if (!Equals(updated, Diagnostics[index]))
            {
                Diagnostics[index] = updated;
            }
        }
    }

    private TimeSpan ResolveLiveCaptureDelay(CaptureRunResult result)
    {
        var hasActivity = result.CapturedCount > 0 || result.PersistedCount > 0 || result.CreatedTodoCount > 0;
        if (_isBackgroundMode)
        {
            return hasActivity ? BackgroundActiveCaptureInterval : BackgroundIdleCaptureInterval;
        }

        return hasActivity ? ForegroundActiveCaptureInterval : ForegroundIdleCaptureInterval;
    }

    private void WakeLiveCaptureLoop()
    {
        if (_liveCaptureTask is not { IsCompleted: false } || _liveCaptureWakeSignal.CurrentCount > 0)
        {
            return;
        }

        try { _liveCaptureWakeSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private DiagnosticRow ApplyLatestCaptureSourceResult(DiagnosticRow row)
    {
        if (!_latestCaptureSourceResults.TryGetValue(row.Adapter, out var result))
        {
            return row;
        }

        var lastSuccess = _captureSourceLastSuccessAt.TryGetValue(row.Adapter, out var succeededAt)
            ? succeededAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : "-";
        var baseDetail = _captureSourceBaseDetails.TryGetValue(row.Adapter, out var detail)
            ? detail
            : row.Detail;
        return result.Status switch
        {
            CaptureSourceRunStatus.Failed => row with
            {
                Status = "异常",
                LastSuccessAt = lastSuccess,
                Detail = $"{result.Stage}/{result.ErrorCode}：{result.SanitizedError}"
            },
            CaptureSourceRunStatus.Skipped => row with
            {
                Status = "已跳过",
                LastSuccessAt = lastSuccess,
                Detail = result.SanitizedError ?? baseDetail
            },
            CaptureSourceRunStatus.Empty => row with
            {
                Status = "正常（无新消息）",
                LastSuccessAt = lastSuccess,
                Detail = $"本轮采集 0 条；{baseDetail}"
            },
            _ => row with
            {
                Status = "正常",
                LastSuccessAt = lastSuccess,
                Detail = $"本轮采集 {result.CapturedCount} 条、入库 {result.PersistedCount} 条；{baseDetail}"
            }
        };
    }

    private static string FormatListenerStatus(CaptureRunResult result)
    {
        var failed = result.Sources.Where(source => source.Status == CaptureSourceRunStatus.Failed).ToArray();
        if (result.AllSourcesFailed)
        {
            return $"监听失败：所有启用采集源均异常；{FormatSourceFailures(failed)}";
        }

        if (failed.Length > 0)
        {
            return $"监听部分失败：{FormatSourceFailures(failed)}；其他采集源继续运行";
        }

        return $"监听运行中，最近采集 {result.CapturedCount} 条，入库 {result.PersistedCount} 条，待办 {result.CreatedTodoCount} 条";
    }

    private static string FormatCaptureSummary(string prefix, CaptureRunResult result)
    {
        var failed = result.Sources.Where(source => source.Status == CaptureSourceRunStatus.Failed).ToArray();
        var counts = $"{prefix} {result.CapturedCount} 条，入库 {result.PersistedCount} 条，创建待办 {result.CreatedTodoCount} 条";
        return failed.Length == 0 ? counts : $"{counts}；部分失败：{FormatSourceFailures(failed)}";
    }

    private static string FormatSourceFailures(IReadOnlyList<CaptureSourceRunResult> failed) =>
        string.Join("；", failed.Select(source =>
            $"{source.AdapterName} {source.Stage}/{source.ErrorCode}：{source.SanitizedError}"));

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
        return new WindowsUiAutomationSnapshotProvider(new SystemWindowsAutomationReader());
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
            IgnoreWindowTitleContains = new[] { "IM消息看板", "微信项目消息看板", "WeChat Dashboard" }
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

    /// <summary>将内部来源标识转换为界面显示名称。</summary>

    /// <summary>Formats the source group name and rejects legacy window-capture status text.</summary>
    private static string FormatSourceChatName(Message message)
    {
        var chatName = message.ChatName.Trim();
        if (string.IsNullOrWhiteSpace(chatName))
        {
            return "\u7FA4\u540D\u8BC6\u522B\u5931\u8D25";
        }

        if (!message.SourceMessageKey.Contains(":window:", StringComparison.OrdinalIgnoreCase))
        {
            return chatName;
        }

        var hasMessageCount = chatName.Any(char.IsDigit) && chatName.Contains("\u6761", StringComparison.Ordinal);
        var isStatusText =
            chatName.StartsWith("\u5FAE\u4FE1\u76D1\u542C", StringComparison.OrdinalIgnoreCase) ||
            ((chatName.Contains("\u91C7\u96C6", StringComparison.OrdinalIgnoreCase) ||
              chatName.Contains("\u5165\u5E93", StringComparison.OrdinalIgnoreCase) ||
              chatName.Contains("\u5F85\u529E", StringComparison.OrdinalIgnoreCase)) &&
             hasMessageCount) ||
            (chatName.Contains("\u5F53\u524D\u7B2C", StringComparison.OrdinalIgnoreCase) &&
             chatName.Contains("\u9875", StringComparison.OrdinalIgnoreCase));

        if (isStatusText)
        {
            return "\u7FA4\u540D\u8BC6\u522B\u5931\u8D25";
        }

        if (!chatName.EndsWith("\u6761", StringComparison.Ordinal))
        {
            return chatName;
        }

        var suffixStart = chatName.Length - 2;
        while (suffixStart >= 0 &&
               (char.IsDigit(chatName[suffixStart]) ||
                char.IsWhiteSpace(chatName[suffixStart]) ||
                chatName[suffixStart] is ',' or '\uFF0C'))
        {
            suffixStart--;
        }

        return suffixStart < chatName.Length - 2
            ? chatName[..(suffixStart + 1)].TrimEnd()
            : chatName;
    }
    private static string FormatMessageSource(string source)
    {
        var value = source.Trim();
        return value.ToUpperInvariant() switch
        {
            "WECHAT" => "微信",
            "SHIHUATONG" => "石化通",
            "FEISHU" => "飞书",
            "DINGTALK" => "钉钉",
            "SAMPLE" => "示例",
            "" => "未知",
            _ => value
        };
    }

    private ProjectClassifier CreateProjectClassifier()
    {
        var configuredRules = _currentFollowedProjectKeywords.SelectMany(keyword => new[]
        {
            new ProjectRule(keyword.ProjectId, keyword.ProjectName, ProjectRuleType.ChatName, keyword.Keyword, 180),
            new ProjectRule(keyword.ProjectId, keyword.ProjectName, ProjectRuleType.Keyword, keyword.Keyword, 170)
        });
        var defaultRules = new[]
        {
            new ProjectRule(1, "CRM升级", ProjectRuleType.ChatName, "CRM项目群", 100),
            new ProjectRule(2, "支付平台", ProjectRuleType.Keyword, "支付", 80),
            new ProjectRule(3, "数据中台", ProjectRuleType.Keyword, "数据", 70)
        };
        return new ProjectClassifier(configuredRules.Concat(defaultRules));
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

/// <summary>微信消息列表行数据：用于 UI 绑定展示单条微信原生消息。</summary>
public sealed record WeChatMessageRow(string Content, string ChatName, string SenderName);

/// <summary>支持富文本高亮的微信消息行。</summary>
public sealed class HighlightableWeChatMessageRow : INotifyPropertyChanged
{
    private long _messageId;
    private string _content = "";
    private string _chatName = "";
    private string _senderName = "";
    private string _sentAt = "";
    private IEnumerable<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment> _contentSegments = Array.Empty<WechatDashboard.Application.Mentions.MessageHighlighter.TextSegment>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public long MessageId
    {
        get => _messageId;
        set { _messageId = value; OnPropertyChanged(); }
    }

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

/// <summary>项目看板统计图行数据。</summary>
public sealed record DashboardChartRow(string Label, int Count, string CountText, string Detail, double Percent);

internal sealed record DashboardAggregate(string Label, int Count, string Detail);

internal sealed record DashboardSnapshot(
    IReadOnlyList<Message> RecentMessages,
    IReadOnlyList<DashboardChartRow> Rows,
    string StatusText);

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

/// <summary>关注项目行数据：用于 UI 展示与删除操作。</summary>
public sealed record FollowedProjectRow(long Id, string ProjectName, string Color);
public sealed record FollowedProjectKeywordRow(long Id, long ProjectId, string ProjectName, string Keyword);
