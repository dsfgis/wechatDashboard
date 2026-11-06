using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Briefing;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Classification;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.TodoRules;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Application.Weekly;
using WechatDashboard.App.Services;
using WechatDashboard.App.ViewModels.Settings;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.App.Views.Todos;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Agent;
using WechatDashboard.Infrastructure.Capture;
using WechatDashboard.Infrastructure.Persistence;
using WechatDashboard.Infrastructure.Weekly;

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
    // 用户教给 Agent 的自动待办排除规则（命中即只入库不建待办）；
    // 采集线程、模型提示词和界面共用同一份内存快照，任一处修改立即全员可见。
    private readonly TodoRuleStore _todoRuleStore;
    private readonly SqliteUserAliasRepository _aliasRepository;
    private readonly SqliteFollowedChatRepository _followedChatRepository;
    private readonly SqliteFollowedProjectRepository _followedProjectRepository;
    private readonly SqliteFollowedProjectKeywordRepository _followedProjectKeywordRepository;
    // 简报事项的长期项目记忆；只经用户确认后写入，不参与待办状态流转。
    private readonly SqliteProjectMemoryRepository _projectMemoryRepository;
    // 项目成员（直接周报归集；邮箱只匹配发件人，不用于自动发信）
    private readonly SqliteProjectMemberRepository _projectMemberRepository;
    // 优先级层数/颜色配置（app_settings 中的一段 JSON）
    private readonly SqlitePrioritySettingsRepository _prioritySettingsRepository;
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
    // 当前生效的项目成员集合（用于周报归集与直接发件人匹配，不用于发信）
    private IReadOnlyList<ProjectMember> _currentProjectMembers = Array.Empty<ProjectMember>();
    // 微信本地读取器服务
    private readonly WeChatLocalReaderService _readerService;
    // DeepSeek 密钥仅在启动时读取，界面保存后于下次重启生效。
    private readonly ProtectedDeepSeekApiKeyStore _deepSeekApiKeyStore;
    private readonly OpenAiCompatibleMessageActionModel _messageActionModel;
    // 后台采集直接读取这两个内存开关，复选框无需等待重启或重建管线。
    // 总闸决定是否调用模型；范围开关决定是否把未 @我 的聊天消息也交给模型。
    private volatile bool _analyzeMessageContent;
    private volatile bool _analyzeAllMessages;
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
    // 当前项目记忆快照；状态和终态显示筛选均基于该快照在本地投影。
    private IReadOnlyList<ProjectMemoryItem> _projectMemoryItems = Array.Empty<ProjectMemoryItem>();
    // 建议所属项目用于阻止切换项目后误应用上一窗口的建议。
    private long? _memoryProposalProjectId;
    // 填充项目下拉时暂停选择事件，避免一次初始化触发多次数据库读取。
    private bool _isLoadingMemoryProjects;

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
    // 模型配置状态只描述保存情况，不包含或回显 API Key。
    private string _modelConfigStatusText = "尚未保存本机 DeepSeek API Key。";
    public TodoFeatureCoordinator TodoFeature { get; }
    public DesktopCalendarViewModel ScheduleOverview { get; }
    /// <summary>“优先级设置”页视图模型：层数 2–6 与每级颜色。</summary>
    public PrioritySettingsViewModel PriorityLevelSettings { get; }

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
        _todoRuleStore = new TodoRuleStore(new SqliteTodoRuleRepository(_databasePath));
        _aliasRepository = new SqliteUserAliasRepository(_databasePath);
        _followedChatRepository = new SqliteFollowedChatRepository(_databasePath);
        _followedProjectRepository = new SqliteFollowedProjectRepository(_databasePath);
        _followedProjectKeywordRepository = new SqliteFollowedProjectKeywordRepository(_databasePath);
        // 与简报服务共用同一仓储，确保模型只看到当前项目的稳定事项 ID。
        _projectMemoryRepository = new SqliteProjectMemoryRepository(_databasePath);
        _projectMemberRepository = new SqliteProjectMemberRepository(_databasePath);
        _prioritySettingsRepository = new SqlitePrioritySettingsRepository(_databasePath);
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

        _messageActionModel = OpenAiCompatibleMessageActionModel.FromSavedApiKeyOrEnvironment(
            savedDeepSeekApiKey,
            // 每次分析时读取当前规则快照，界面新增规则后无需重启即可影响提示词。
            todoRules: () => _todoRuleStore.Current);
        TodoFeature = new TodoFeatureCoordinator(
            _databasePath,
            _messageActionModel,
            () => _currentAliases,
            () => _currentFollowedChats,
            () => _followedChatFilterMode,
            // 待办详情里的“此类不再建待办”与模型配置页共用同一份规则快照。
            _todoRuleStore,
            tabIndex =>
            {
                MainContentTabs.SelectedIndex = tabIndex;
                SidebarNavigation.SelectedItem = SidebarNavigation.Items
                    .OfType<ListBoxItem>()
                    .FirstOrDefault(item => item.Tag?.ToString() == tabIndex.ToString());
            },
            // 新建待办同样要出现在日历里，不能只刷当前选中的 Tab。
            async () => await Task.WhenAll(RefreshAsync(), ScheduleOverview.RefreshAsync()),
            // 详情改状态后同步刷新日程概览与桌面日历；两者共用 ScheduleOverview 实例。
            refreshCalendar: () => ScheduleOverview.RefreshAsync());
        ScheduleOverview = new DesktopCalendarViewModel(
            _todoRepository,
            SystemClock.Instance,
            TimeZoneInfo.Local,
            TodoFeature.Workbench.OpenTodoAsync,
            TodoFeature.RefreshAsync,
            _followedProjectRepository,
            _messageRepository,
            new SqliteReminderRepository(_databasePath));

        DatabasePath = _databasePath;
        CaptureInboxPath = _captureInboxPath;
        // 优先级设置页与全局色板共享同一仓储：保存后立即影响待办、消息流与日程。
        PriorityLevelSettings = new PrioritySettingsViewModel(
            _prioritySettingsRepository,
            OnPrioritySettingsAppliedAsync);
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

    // 采集诊断列表（UI 绑定）
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = new();

    // 窗口快照列表（UI 绑定）
    public ObservableCollection<WindowSnapshotRow> WindowSnapshots { get; } = new();

    // 采集源设置列表（UI 绑定）
    public ObservableCollection<CaptureSourceSettingRow> CaptureSourceSettings { get; } = new();

    // 用户别名列表（UI 绑定）
    public ObservableCollection<UserAliasRow> Aliases { get; } = new();

    // 自动待办规则列表（UI 绑定）
    public ObservableCollection<TodoRuleRow> TodoRules { get; } = new();

    // 关注群列表（UI 绑定）
    public ObservableCollection<FollowedChatRow> FollowedChats { get; } = new();

    // 关注项目列表（UI 绑定）
    public ObservableCollection<FollowedProjectRow> FollowedProjects { get; } = new();
    // 项目关键字列表（UI 绑定）
    public ObservableCollection<FollowedProjectKeywordRow> FollowedProjectKeywords { get; } = new();
    // 关注项目树：项目节点下挂各自关键字，供“关注设置”可折叠层级列表绑定
    public ObservableCollection<FollowedProjectTreeRow> FollowedProjectTree { get; } = new();
    // 本次简报产生的建议；只有勾选并点击“应用选中”的行才会入库。
    public ObservableCollection<ProjectMemoryProposalRow> MemoryProposals { get; } = new();
    // 当前项目及筛选条件下可见的长期事项。
    public ObservableCollection<ProjectMemoryItemRow> MemoryItems { get; } = new();
    // 选中事项的不可变来源快照与真实状态时间线。
    public ObservableCollection<ProjectMemorySourceRow> MemorySources { get; } = new();
    public ObservableCollection<ProjectMemoryHistoryRow> MemoryHistory { get; } = new();
    // 建议状态下拉使用中文显示，但绑定值仍保留领域枚举。
    public IReadOnlyList<ProjectMemoryStatusOption> MemoryStatusOptions { get; } =
    [
        new(ProjectMemoryStatus.Open, "未开始"),
        new(ProjectMemoryStatus.InProgress, "进行中"),
        new(ProjectMemoryStatus.Blocked, "阻塞"),
        new(ProjectMemoryStatus.Shelved, "已搁置"),
        new(ProjectMemoryStatus.Completed, "已完成"),
        new(ProjectMemoryStatus.Discarded, "已废弃")
    ];
    // 项目颜色不再由用户单独选择：状态决定颜色（见 ProjectStageColorPolicy），
    // 因此界面上没有独立的项目颜色列表，避免出现“状态一个色、色板另一个色”。

    /// <summary>
    /// 项目生命周期状态选项：在建 / 运维 / 移交，顺序即下拉框与排序顺序。
    /// 颜色由状态唯一决定（在建=红、运维=蓝、移交=灰），因此每个选项自带圆点颜色，
    /// 界面上不再有独立的颜色下拉框。
    /// </summary>
    public IReadOnlyList<ProjectStageOption> ProjectStageOptions { get; } =
    [
        new(ProjectStage.Construction, "在建", ProjectStageColorPolicy.GetColor(ProjectStage.Construction)),
        new(ProjectStage.Operation, "运维", ProjectStageColorPolicy.GetColor(ProjectStage.Operation)),
        new(ProjectStage.Handover, "移交", ProjectStageColorPolicy.GetColor(ProjectStage.Handover))
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
        // 规则同样要在首轮采集前恢复，否则启动瞬间的 @我 消息仍会按旧规则建待办。
        await LoadTodoRulesAsync();
        await TodoFeature.StartAsync();
        ApplyBootstrapRange();
        await Task.Run(() => _readerService.TryRestoreProtectedKeyAsync(CancellationToken.None));
        await LoadCaptureSourceSettingsAsync();
        LoadMailCredentialState();
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
        // 优先级层数与颜色必须先于任何待办/消息渲染载入，否则首帧会用默认色板。
        await PriorityLevelSettings.LoadAsync(CancellationToken.None);
        await LoadUserAliasesAsync();
        await LoadFollowedChatsAsync();
        await LoadFollowedProjectsAsync();
        // 项目下拉先填好，打开“项目看板”即可看到当前状态与排序，不必先点生成。
        await ReloadProjectPickersAsync();
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
        var recentMessagesTask = Task.Run(() => _messageRepository.GetRecentAsync(DashboardMessageSampleLimit, CancellationToken.None));
        var pendingTodosTask = Task.Run(() => _todoRepository.GetPendingAsync(CancellationToken.None));
        var managedProjectsTask = Task.Run(() => _followedProjectRepository.GetAllAsync(CancellationToken.None));
        var messageCountTask = Task.Run(() => _messageRepository.GetMessageCountAsync(CancellationToken.None));
        var dataDirectoryTask = Task.Run(WeChatDataDirectoryLocator.Locate);
        var shihuatongReadyTask = Task.Run(() => ShihuatongLocalDatabaseCaptureAdapter.IsProcessRunning);
        await Task.WhenAll(
            recentMessagesTask,
            pendingTodosTask,
            managedProjectsTask,
            messageCountTask,
            dataDirectoryTask,
            shihuatongReadyTask);

        var recentMessages = recentMessagesTask.Result;
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
        HighPriorityTodoCount = pendingTodos.Count(todo => PrioritySettings.Current.IsHighPriority(todo.Priority));

        // 刷新当前页签的分页文本与汇总数据（强制重新计算总数）
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

    /// <summary>
    /// 生成项目简报：跨微信、石化通、石化邮按时间窗口取数，
    /// 复用关注项目关键词过滤，模型总结；模型不可用或失败时回退本地统计摘要。
    /// </summary>
    private async void GenerateProjectBriefingButton_Click(object sender, RoutedEventArgs e)
    {
        if (GenerateBriefingButton is null)
        {
            return;
        }

        try
        {
            // 生成前重读项目：状态切换后排序与“状态 · 项目名”文案必须立刻生效。
            var activeProjects = await LoadBriefingProjectsAsync();
            if (activeProjects.Count == 0)
            {
                BriefingStatusText.Text = "暂无启用的关注项目。请先在“关注设置”中添加项目。";
                BriefingResultTextBox.Clear();
                // 项目被删除后，旧建议不能再跨项目应用。
                MemoryProposals.Clear();
                _memoryProposalProjectId = null;
                ProjectBriefingExpander.IsExpanded = true;
                return;
            }

            if (BriefingProjectComboBox.SelectedItem is not FollowedProject project)
            {
                BriefingStatusText.Text = "请选择要生成简报的项目。";
                return;
            }

            if (BriefingWindowComboBox.SelectedItem is not ComboBoxItem windowItem
                || !int.TryParse(windowItem.Tag?.ToString(), out var days)
                || days < 1)
            {
                days = 3;
            }

            // “近 N 天”从 N 天前的本地零点开始，到当前时刻为止。
            var to = DateTimeOffset.Now;
            var from = to.LocalDateTime.Date.AddDays(-(days - 1));

            ProjectBriefingExpander.IsExpanded = true;
            BriefingStatusText.Text = $"正在生成「{project.ProjectName}」的跨来源简报…";
            // 生成期间清掉上一批建议并禁止并发应用，避免新旧项目或基准状态混用。
            MemoryProposals.Clear();
            _memoryProposalProjectId = null;
            MemoryProposalStatusText.Text = "正在生成项目记忆建议…";
            GenerateBriefingButton.IsEnabled = false;
            ApplyMemoryProposalsButton.IsEnabled = false;
            try
            {
                var keywords = await _followedProjectKeywordRepository.GetAllAsync(CancellationToken.None);
                var service = new ProjectBriefingService(
                    _messageRepository,
                    _messageActionModel,
                    _projectMemoryRepository);
                var outcome = await Task.Run(() => service.GenerateAsync(
                    project.Id,
                    project.ProjectName,
                    keywords,
                    from,
                    to,
                    CancellationToken.None));

                BriefingResultTextBox.Text = FormatProjectBriefing(outcome.Briefing);
                BriefingStatusText.Text = outcome.MatchedCount == 0
                    ? $"时间窗口（近 {days} 天）内没有匹配「{project.ProjectName}」关键词的消息。"
                    : outcome.FromModel
                        ? $"已生成模型简报：匹配 {outcome.MatchedCount} 条消息，送入模型 {outcome.SentToModelCount} 条。"
                        // 回退原因来自服务层，便于区分未配置、超时、截断与校验失败。
                        : $"模型不可用，已回退本地统计摘要（{outcome.MatchedCount} 条消息）。原因：{outcome.FallbackReason ?? "未知"}";

                // 所有建议默认勾选，但这里只更新内存集合，不直接写数据库。
                Replace(MemoryProposals, outcome.MemoryProposals.Select(proposal => new ProjectMemoryProposalRow(proposal)));
                _memoryProposalProjectId = project.Id;
                var newCount = outcome.MemoryProposals.Count(proposal => proposal.ItemId is null);
                var changeCount = outcome.MemoryProposals.Count - newCount;
                var proposalSource = outcome.FromModel
                    ? "模型建议"
                    : $"本地规则建议（{outcome.FallbackReason ?? "模型不可用或结果未通过校验"}）";
                MemoryProposalStatusText.Text = outcome.MemoryProposals.Count == 0
                    ? $"{proposalSource}：本次没有需要确认的新增事项或状态变化。"
                    : $"{proposalSource}：新增 {newCount} 项，状态建议 {changeCount} 项；已默认勾选，可修改建议状态后应用。";
            }
            finally
            {
                GenerateBriefingButton.IsEnabled = true;
                ApplyMemoryProposalsButton.IsEnabled = true;
            }
        }
        catch (Exception exception)
        {
            BriefingStatusText.Text = $"生成简报失败：{exception.Message}";
            MemoryProposalStatusText.Text = "本次未生成可应用的项目记忆建议。";
            MemoryProposals.Clear();
            _memoryProposalProjectId = null;
            GenerateBriefingButton.IsEnabled = true;
            ApplyMemoryProposalsButton.IsEnabled = true;
        }
    }

    /// <summary>把简报结果格式化为带分段标题的可读文本。</summary>
    private static string FormatProjectBriefing(ProjectBriefing briefing)
    {
        var sections = new List<string> { $"【总体进展】\n{briefing.Summary}" };
        if (briefing.Highlights.Count > 0)
        {
            sections.Add($"【关键进展】\n{string.Join("\n", briefing.Highlights.Select(item => $"· {item}"))}");
        }

        if (briefing.Risks.Count > 0)
        {
            sections.Add($"【风险与阻断】\n{string.Join("\n", briefing.Risks.Select(item => $"· {item}"))}");
        }

        if (briefing.Actions.Count > 0)
        {
            sections.Add($"【需要我行动】\n{string.Join("\n", briefing.Actions.Select(item => $"· {item}"))}");
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// 批量应用用户选中的建议。服务层会重新读取来源消息，仓储会再次校验基准状态，
    /// 因此确认窗口过期时不会静默覆盖其他窗口已经写入的新状态。
    /// </summary>
    private async void ApplyMemoryProposalsButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedRows = MemoryProposals.Where(row => row.IsSelected).ToArray();
        if (selectedRows.Length == 0)
        {
            MemoryProposalStatusText.Text = "请至少勾选一条项目记忆建议。";
            return;
        }

        if (_memoryProposalProjectId is not long projectId
            || BriefingProjectComboBox.SelectedItem is not FollowedProject briefingProject
            || briefingProject.Id != projectId)
        {
            MemoryProposalStatusText.Text = "简报项目已切换，请重新生成简报后再应用建议。";
            return;
        }

        ApplyMemoryProposalsButton.IsEnabled = false;
        GenerateBriefingButton.IsEnabled = false;
        try
        {
            await _databaseInitializer.InitializeAsync(CancellationToken.None);
            var service = new ProjectMemoryService(_projectMemoryRepository, _messageRepository);
            var proposals = selectedRows.Select(row => row.ToProposal()).ToArray();
            var result = await Task.Run(() => service.ApplySelectedAsync(
                projectId,
                proposals,
                CancellationToken.None));

            // 已提交行从确认窗口移除；未勾选行继续保留，便于用户分批确认。
            foreach (var row in selectedRows)
            {
                MemoryProposals.Remove(row);
            }

            MemoryProposalStatusText.Text =
                $"已应用：新增 {result.Created} 项，更新 {result.Updated} 项，跳过 {result.Skipped} 项；剩余 {MemoryProposals.Count} 条未应用建议。";
            _memoryProposalProjectId = MemoryProposals.Count == 0 ? null : projectId;

            // 应用成功后立即刷新长期记忆，并展开来源与状态时间线区域。
            await LoadMemoryProjectsAsync(projectId);
            ProjectMemoryExpander.IsExpanded = true;
        }
        catch (ProjectMemoryConcurrencyException exception)
        {
            MemoryProposalStatusText.Text = $"建议已过期：{exception.Message} 请重新生成简报。";
        }
        catch (Exception exception)
        {
            MemoryProposalStatusText.Text = $"应用项目记忆失败：{exception.Message}";
        }
        finally
        {
            ApplyMemoryProposalsButton.IsEnabled = true;
            GenerateBriefingButton.IsEnabled = true;
        }
    }

    /// <summary>首次展开时填充项目下拉；再次展开时只刷新当前项目。</summary>
    private async void ProjectMemoryExpander_Expanded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (MemoryProjectComboBox.ItemsSource is null)
            {
                var preferredProjectId = (BriefingProjectComboBox.SelectedItem as FollowedProject)?.Id;
                await LoadMemoryProjectsAsync(preferredProjectId);
            }
            else
            {
                await RefreshProjectMemoryAsync();
            }
        }
        catch (Exception exception)
        {
            ProjectMemoryStatusText.Text = $"加载项目记忆失败：{exception.Message}";
        }
    }

    /// <summary>刷新项目下拉并尽量保留原选择，随后读取所选项目的全部事项。</summary>
    private async Task LoadMemoryProjectsAsync(long? preferredProjectId = null)
    {
        await _databaseInitializer.InitializeAsync(CancellationToken.None);
        var projects = await _followedProjectRepository.GetAllAsync(CancellationToken.None);
        var activeProjects = projects.Where(project => project.IsActive).ToArray();
        var selectedProjectId = preferredProjectId
            ?? (MemoryProjectComboBox.SelectedItem as FollowedProject)?.Id;

        // 赋值期间屏蔽 SelectionChanged，统一在本方法末尾执行一次读取。
        _isLoadingMemoryProjects = true;
        try
        {
            MemoryProjectComboBox.ItemsSource = activeProjects;
            MemoryProjectComboBox.SelectedItem = activeProjects.FirstOrDefault(project => project.Id == selectedProjectId)
                ?? activeProjects.FirstOrDefault();
        }
        finally
        {
            _isLoadingMemoryProjects = false;
        }

        if (activeProjects.Length == 0)
        {
            _projectMemoryItems = Array.Empty<ProjectMemoryItem>();
            MemoryItems.Clear();
            ClearMemoryDetails();
            ProjectMemoryStatusText.Text = "暂无启用的关注项目。";
            return;
        }

        await RefreshProjectMemoryAsync();
    }

    /// <summary>切换项目后立即刷新事项，避免界面继续展示上一项目的数据。</summary>
    private async void MemoryProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingMemoryProjects || !IsLoaded)
        {
            return;
        }

        try
        {
            await RefreshProjectMemoryAsync();
        }
        catch (Exception exception)
        {
            ProjectMemoryStatusText.Text = $"加载项目记忆失败：{exception.Message}";
        }
    }

    /// <summary>手动刷新当前项目，保留现有状态和终态显示筛选。</summary>
    private async void RefreshProjectMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadMemoryProjectsAsync((MemoryProjectComboBox.SelectedItem as FollowedProject)?.Id);
        }
        catch (Exception exception)
        {
            ProjectMemoryStatusText.Text = $"刷新项目记忆失败：{exception.Message}";
        }
    }

    /// <summary>后台读取事项快照；若等待期间用户切换项目，则丢弃过期结果。</summary>
    private async Task RefreshProjectMemoryAsync()
    {
        if (MemoryProjectComboBox.SelectedItem is not FollowedProject project)
        {
            _projectMemoryItems = Array.Empty<ProjectMemoryItem>();
            ApplyMemoryFilters();
            return;
        }

        ProjectMemoryStatusText.Text = $"正在加载「{project.ProjectName}」的项目记忆…";
        var service = new ProjectMemoryService(_projectMemoryRepository, _messageRepository);
        var items = await Task.Run(() => service.GetByProjectAsync(project.Id, CancellationToken.None));
        if ((MemoryProjectComboBox.SelectedItem as FollowedProject)?.Id != project.Id)
        {
            return;
        }

        _projectMemoryItems = items;
        ApplyMemoryFilters();
    }

    /// <summary>状态筛选和终态折叠都只改变界面投影，不修改事项本身。</summary>
    private void MemoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyMemoryFilters();
        }
    }

    /// <summary>按所选状态和“显示终态”开关重新填充事项表格。</summary>
    private void ApplyMemoryFilters()
    {
        ProjectMemoryStatus? selectedStatus = null;
        if (MemoryStatusFilterComboBox?.SelectedItem is ComboBoxItem statusItem
            && Enum.TryParse(statusItem.Tag?.ToString(), out ProjectMemoryStatus parsedStatus))
        {
            selectedStatus = parsedStatus;
        }

        var showTerminal = ShowTerminalMemoryCheckBox?.IsChecked == true;
        var visibleItems = _projectMemoryItems.Where(item =>
                (selectedStatus is null || item.Status == selectedStatus)
                && (showTerminal || !ProjectMemoryDisplay.IsTerminal(item.Status)))
            .Select(item => new ProjectMemoryItemRow(
                item.Id,
                ProjectMemoryDisplay.Category(item.Category),
                item.Title,
                ProjectMemoryDisplay.Status(item.Status),
                item.UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm"),
                item.LastStatusReason ?? "—"))
            .ToArray();
        Replace(MemoryItems, visibleItems);
        ClearMemoryDetails();
        ProjectMemoryStatusText.Text = _projectMemoryItems.Count == 0
            ? "当前项目还没有已确认的记忆事项。"
            : $"共 {_projectMemoryItems.Count} 项，当前显示 {visibleItems.Length} 项；终态默认{(showTerminal ? "显示" : "折叠")}。";
    }

    /// <summary>读取选中事项的来源快照和状态历史，并防止迟到的结果覆盖新选择。</summary>
    private async void ProjectMemoryItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectMemoryItemsGrid.SelectedItem is not ProjectMemoryItemRow row
            || MemoryProjectComboBox.SelectedItem is not FollowedProject project)
        {
            ClearMemoryDetails();
            return;
        }

        try
        {
            var service = new ProjectMemoryService(_projectMemoryRepository, _messageRepository);
            var details = await Task.Run(() => service.GetDetailsAsync(project.Id, row.Id, CancellationToken.None));
            if ((ProjectMemoryItemsGrid.SelectedItem as ProjectMemoryItemRow)?.Id != row.Id
                || (MemoryProjectComboBox.SelectedItem as FollowedProject)?.Id != project.Id)
            {
                return;
            }

            if (details is null)
            {
                ClearMemoryDetails();
                ProjectMemoryStatusText.Text = "该事项已删除，请刷新项目记忆。";
                return;
            }

            Replace(MemorySources, details.Sources.Select(source => new ProjectMemorySourceRow(
                source.Source,
                $"{source.ChatName} / {source.SenderName}",
                source.ContentSnapshot,
                source.SentAt.LocalDateTime.ToString("MM-dd HH:mm"))));
            Replace(MemoryHistory, details.History.Select(history => new ProjectMemoryHistoryRow(
                history.ChangedAt.LocalDateTime.ToString("MM-dd HH:mm"),
                $"{ProjectMemoryDisplay.Status(history.PreviousStatus)} → {ProjectMemoryDisplay.Status(history.NewStatus)}",
                history.Reason)));
            ProjectMemoryStatusText.Text = $"事项「{details.Item.Title}」：{details.Sources.Count} 条来源，{details.History.Count} 次状态记录。";
        }
        catch (Exception exception)
        {
            ClearMemoryDetails();
            ProjectMemoryStatusText.Text = $"读取事项详情失败：{exception.Message}";
        }
    }

    /// <summary>切换事项或筛选条件时清空详情，避免来源证据错配。</summary>
    private void ClearMemoryDetails()
    {
        MemorySources.Clear();
        MemoryHistory.Clear();
    }

    /// <summary>选择现有周报模板；只读取模板副本，绝不把生成内容写回原文件。</summary>
    private void BrowseWeeklyReportTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择项目周报模板",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            WeeklyReportTemplatePathTextBox.Text = dialog.FileName;
        }
    }

    /// <summary>
    /// 生成项目周报 Excel：汇总成员直接周报、项目消息和待办。
    /// 这里只编排取数、确认与导出，不创建邮件草稿，也不调用任何邮件发送能力。
    /// </summary>
    private async void GenerateWeeklyReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (GenerateWeeklyReportButton is null) return;
        WeeklyReportExpander.IsExpanded = true;
        GenerateWeeklyReportButton.IsEnabled = false;
        try
        {
            // 生成前重读项目：状态切换后排序与“状态 · 项目名”文案必须立刻生效。
            var activeProjects = await LoadWeeklyReportProjectsAsync();
            if (activeProjects.Count == 0)
            {
                WeeklyReportStatusText.Text = "暂无启用的关注项目，请先在“关注设置”中添加项目。";
                WeeklyReportResultTextBox.Clear();
                return;
            }

            if (WeeklyReportProjectComboBox.SelectedItem is not FollowedProject project)
            {
                WeeklyReportStatusText.Text = "请选择要生成周报的项目。";
                return;
            }

            SetDefaultWeeklyReportDates();
            if (WeeklyReportFromDatePicker.SelectedDate is not DateTime fromDate
                || WeeklyReportToDatePicker.SelectedDate is not DateTime toDate)
            {
                WeeklyReportStatusText.Text = "请选择完整的周报开始和结束日期。";
                return;
            }
            if (toDate.Date < fromDate.Date)
            {
                WeeklyReportStatusText.Text = "周报结束日期不得早于开始日期。";
                return;
            }

            var templatePath = WeeklyReportTemplatePathTextBox.Text.Trim();
            if (!File.Exists(templatePath))
            {
                WeeklyReportStatusText.Text = "周报模板不存在，请重新选择有效的 .xlsx 文件。";
                return;
            }

            // 日期选择按本地自然日处理，结束时刻覆盖所选日期的最后一Tick。
            var from = new DateTimeOffset(fromDate.Date);
            var to = new DateTimeOffset(toDate.Date.AddDays(1).AddTicks(-1));
            var reportDate = DateTimeOffset.Now;
            var safeProjectName = MakeSafeFilePart(project.ProjectName);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存项目周报 Excel",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"【{safeProjectName}】项目周报{reportDate:yyyyMMdd}.xlsx",
                InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(templatePath))
            };
            if (dialog.ShowDialog(this) != true)
            {
                WeeklyReportStatusText.Text = "已取消周报生成，未写出文件。";
                return;
            }

            var outputPath = dialog.FileName;
            if (string.Equals(
                    Path.GetFullPath(templatePath),
                    Path.GetFullPath(outputPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                WeeklyReportStatusText.Text = "输出文件不能覆盖周报模板，请选择另一个文件路径。";
                return;
            }

            WeeklyReportStatusText.Text = $"正在归集并总结「{project.ProjectName}」周报素材…";
            WeeklyReportResultTextBox.Text = "仅生成 Excel，不发送邮件。";
            var outcome = await Task.Run(async () =>
            {
                // 首次处理智能管线项目时只补缺失姓名，既有成员配置与周报开关不覆盖。
                await EnsureDefaultWeeklyReportMembersAsync(project);
                var keywords = await _followedProjectKeywordRepository.GetAllAsync(CancellationToken.None);
                var allMembers = await _projectMemberRepository.GetAllAsync(CancellationToken.None);
                var projectMembers = allMembers.Where(member => member.ProjectId == project.Id).ToArray();
                var service = new WeeklyReportService(
                    new WeeklyReportCollector(_messageRepository),
                    _todoRepository,
                    _messageActionModel);
                return await service.GenerateAsync(
                    project.Id,
                    project.ProjectName,
                    projectMembers,
                    keywords,
                    from,
                    to,
                    reportDate,
                    _currentAliases,
                    CancellationToken.None);
            });

            var diagnostics = BuildWeeklyReportDiagnostics(outcome);
            WeeklyReportResultTextBox.Text = diagnostics.Text;
            if (diagnostics.NeedsConfirmation)
            {
                var answer = MessageBox.Show(
                    this,
                    diagnostics.Text + "\n\n检测到缺失素材、路由排除或模型回退，是否仍继续导出 Excel？",
                    "周报导出确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    WeeklyReportStatusText.Text = "已取消导出，未写出文件。";
                    return;
                }
            }

            WeeklyReportStatusText.Text = "正在写入模板副本并校验 Excel…";
            await Task.Run(() => new OpenXmlWeeklyReportExporter().Export(
                outcome.Draft,
                templatePath,
                outputPath));
            WeeklyReportStatusText.Text = $"周报已生成：{outputPath}；请检查 Excel 后人工发送邮件。";
            WeeklyReportResultTextBox.Text = diagnostics.Text
                + $"\n输出文件：{outputPath}\n邮件：系统未发送，由用户人工发送。";
        }
        catch (Exception exception)
        {
            WeeklyReportStatusText.Text = $"生成周报失败：{exception.Message}";
        }
        finally
        {
            GenerateWeeklyReportButton.IsEnabled = true;
        }
    }

    /// <summary>默认选择最近一个已经完整结束的周一至周五，避免把本周未发生内容写入周报。</summary>
    private void SetDefaultWeeklyReportDates()
    {
        if (WeeklyReportFromDatePicker.SelectedDate.HasValue && WeeklyReportToDatePicker.SelectedDate.HasValue)
        {
            return;
        }

        var today = DateTime.Today;
        var daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        // 周六、周日时本周工作日已经完整结束，应选本周；周一至周五仍选上一完整周。
        var isCompletedWeekend = today.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var lastMonday = today.AddDays(-daysFromMonday - (isCompletedWeekend ? 0 : 7));
        WeeklyReportFromDatePicker.SelectedDate = lastMonday;
        WeeklyReportToDatePicker.SelectedDate = lastMonday.AddDays(4);
    }

    /// <summary>智能管线项目只补齐四个默认成员的缺失姓名，保存仓储负责保持同名成员已有字段。</summary>
    private async Task EnsureDefaultWeeklyReportMembersAsync(FollowedProject project)
    {
        if (!string.Equals(project.ProjectName, "智能化管线管理系统", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var defaultNames = new[] { "沈佳奇", "张欣宇", "张彬", "赵岑东" };
        var members = await _projectMemberRepository.GetAllAsync(CancellationToken.None);
        var existingNames = members
            .Where(member => member.ProjectId == project.Id)
            .Select(member => member.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in defaultNames.Where(name => !existingNames.Contains(name)))
        {
            // 新成员使用仓储默认的IsReporting=true；空身份字段留待用户在设置页补齐。
            await _projectMemberRepository.SaveAsync(
                project.Id,
                name,
                null,
                null,
                (string?)null,
                CancellationToken.None);
        }
    }

    /// <summary>把采用条数、身份缺口、排除路由和模型回退整理成导出前确认文本。</summary>
    private static (string Text, bool NeedsConfirmation) BuildWeeklyReportDiagnostics(WeeklyReportOutcome outcome)
    {
        var lines = new List<string>
        {
            $"项目：{outcome.Draft.Metadata.ProjectName}",
            $"周期：{outcome.Draft.Metadata.From:yyyy-MM-dd} 至 {outcome.Draft.Metadata.To:yyyy-MM-dd}",
            $"项目消息：{outcome.Diagnostics.ProjectMessageCount} 条；项目待办：{outcome.TodoCount} 条。"
        };
        if (outcome.Diagnostics.Members.Count == 0)
        {
            lines.Add("成员周报：当前项目没有启用周报的成员。");
        }
        else
        {
            lines.AddRange(outcome.Diagnostics.Members.Select(member =>
                $"成员 {member.MemberName}：采用 {member.AdoptedCount} 条"
                + (member.HasDirectIdentity ? "。" : "；缺少 wxid/邮箱，无法可靠归集私聊。")));
        }

        lines.Add($"路由排除：Unknown {outcome.Diagnostics.UnknownRouteExcluded} 条，群聊/非直接消息 {outcome.Diagnostics.NonPrivateExcluded} 条。");
        lines.Add(outcome.FromModel
            ? "总结方式：大模型结构化总结，来源 ID 校验已通过。"
            : $"总结方式：模型不可用，已回退本地有依据摘要（{outcome.FallbackReason ?? "未知原因"}）。");
        var needsConfirmation = !outcome.FromModel
            || outcome.Diagnostics.Members.Count == 0
            || outcome.Diagnostics.Members.Any(member => member.AdoptedCount == 0 || !member.HasDirectIdentity)
            || outcome.Diagnostics.UnknownRouteExcluded > 0
            || outcome.Diagnostics.NonPrivateExcluded > 0;
        return (string.Join("\n", lines), needsConfirmation);
    }

    /// <summary>把项目名转换为安全文件名片段，避免用户配置中的非法字符阻断保存。</summary>
    private static string MakeSafeFilePart(string value)
    {
        var parts = value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries);
        var result = string.Join("_", parts).Trim();
        return string.IsNullOrWhiteSpace(result) ? "项目" : result;
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
            // 石化邮两条链路都出现在设置列表里：客户端本地缓存优先，IMAP 直接收信备用。
            var defaultSources = CaptureAdapterFactory.ApplyMailSourcePolicy(
                CaptureAdapterFactory.CreateDefaultLiveSources(_captureInboxPath, _readerService));
            var archiveUsable = defaultSources.Any(source =>
                source.Kind == CaptureSourceKind.SinopecMailArchive && source.IsEnabled);
            // 旧版本从不保存缓存行、并把 IMAP 强写为启用；库里没有缓存行说明是升级后的第一次加载，
            // 此时缓存可用就默认关掉必失败的 IMAP，避免每轮采集都报一条登录失败。
            var legacyMailSettings = !savedSettings.Any(setting =>
                setting.Kind == CaptureSourceKind.SinopecMailArchive.ToString());
            return defaultSources.Select(source =>
            {
                var saved = savedSettings.FirstOrDefault(setting =>
                    setting.Source == source.Source && setting.Kind == source.Kind.ToString());
                var legacyImap = legacyMailSettings && archiveUsable
                                 && source.Kind == CaptureSourceKind.SinopecMail;
                return new CaptureSourceSettingRow
                {
                    Source = source.Source,
                    DisplayName = source.DisplayName,
                    Kind = source.Kind.ToString(),
                    Location = source.Location,
                    // 策略已给出“缓存是否存在 / 是否已保存账号”的默认值，用户保存过的开关优先，
                    // 但不能把不可用的源重新打开，否则每轮采集都会产生失败记录。
                    IsEnabled = source.IsEnabled && (saved?.IsEnabled ?? true) && !legacyImap
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
            // 状态与颜色已合并：历史项目里独立选择的颜色会在这里归一到状态色，只写不一致的行。
            projects = await NormalizeStageColorsAsync(projects);
            var keywords = await _followedProjectKeywordRepository.GetAllAsync(CancellationToken.None);
            var members = await _projectMemberRepository.GetAllAsync(CancellationToken.None);
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
            return (Projects: projects, Keywords: keywords, Members: members);
        });
        var projects = snapshot.Projects;
        var keywords = snapshot.Keywords;
        var members = snapshot.Members;

        _currentFollowedProjects = projects.Select(project => project.ProjectName).ToArray();
        _currentFollowedProjectKeywords = keywords;
        _currentProjectMembers = members;
        var projectRows = projects
            .Select(project => new FollowedProjectRow(project.Id, project.ProjectName, project.Color))
            .ToArray();
        var keywordRows = keywords
            .Select(keyword => new FollowedProjectKeywordRow(keyword.Id, keyword.ProjectId, keyword.ProjectName, keyword.Keyword))
            .ToArray();
        var memberRows = members
            .Select(member => new ProjectMemberRow(
                member.Id,
                member.ProjectId,
                member.DisplayName,
                member.Alias,
                member.Email,
                member.WeChatUserId,
                member.IsReporting))
            .ToArray();
        Replace(FollowedProjects, projectRows);
        Replace(FollowedProjectKeywords, keywordRows);
        // 同一份数据再投影成树：每个项目节点持有自己的关键字，界面只渲染一层可折叠列表。
        // 树节点直接取仓储对象，保留状态字段供节点内的状态下拉绑定。
        Replace(
            FollowedProjectTree,
            projects.Select(project =>
            {
                var projectKeywords = keywordRows
                    .Where(keyword => keyword.ProjectId == project.Id)
                    .ToArray();
                var projectMembers = memberRows
                    .Where(member => member.ProjectId == project.Id)
                    .ToArray();
                return new FollowedProjectTreeRow(
                    project.Id,
                    project.ProjectName,
                    project.Color,
                    project.Stage,
                    $"{projectKeywords.Length} 个关键字",
                    $"{projectMembers.Length} 位成员",
                    projectKeywords,
                    projectMembers);
            }));
    }

    /// <summary>
    /// 把历史项目独立选择的颜色归一到状态色（在建=红、运维=蓝、移交=灰）。
    /// 只对不一致的行写库，全部一致时不做任何写入；归一后重新读取，保证投影用的是最新颜色。
    /// </summary>
    private async Task<IReadOnlyList<FollowedProject>> NormalizeStageColorsAsync(
        IReadOnlyList<FollowedProject> projects)
    {
        var mismatched = projects
            .Where(project => !string.Equals(
                project.Color,
                ProjectStageColorPolicy.GetColor(project.Stage),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (mismatched.Length == 0)
        {
            return projects;
        }

        foreach (var project in mismatched)
        {
            try
            {
                await _followedProjectRepository.UpdateStageAndColorAsync(
                    project.Id,
                    project.Stage,
                    ProjectStageColorPolicy.GetColor(project.Stage),
                    CancellationToken.None);
            }
            catch (Exception)
            {
                // 归一化属于显示层的兼容处理，失败不应阻塞关注设置页加载；界面上圆点仍按状态色渲染。
            }
        }

        return await _followedProjectRepository.GetAllAsync(CancellationToken.None);
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

        // 状态决定颜色：新建下拉里选中的状态同时决定落库颜色，默认在建（红色）。
        var stage = NewFollowedProjectStageComboBox?.SelectedItem is ProjectStageOption selectedStage
            ? selectedStage.Value
            : ProjectStage.Construction;
        var color = ProjectStageColorPolicy.GetColor(stage);
        var project = await _followedProjectRepository.SaveAsync(projectName, color, CancellationToken.None);
        await _followedProjectKeywordRepository.SaveAsync(project.Id, projectName, CancellationToken.None);
        // SaveAsync 对已软删除的同名项目只做重新激活并保留旧颜色，这里统一把状态与颜色写成一致值。
        await _followedProjectRepository.UpdateStageAndColorAsync(
            project.Id,
            stage,
            color,
            CancellationToken.None);
        if (NewFollowedProjectTextBox is not null) NewFollowedProjectTextBox.Text = "";
        await LoadFollowedProjectsAsync();
        await ReloadProjectPickersAsync();
        SummaryText = $"已添加项目「{projectName}」（{ProjectStagePolicy.GetDisplayName(stage)}），可继续为它添加多个关键字。";
    }

    /// <summary>
    /// 项目状态切换：在建 / 运维 / 移交。状态即颜色，因此一次写入同时落库状态与颜色。
    /// 保存后刷新项目列表，使项目树与周报、简报的项目下拉框按新状态重新排序。
    /// </summary>
    private async void FollowedProjectStageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            comboBox.DataContext is not FollowedProjectTreeRow row ||
            comboBox.SelectedItem is not ProjectStageOption selectedStage)
        {
            return;
        }

        if (row.Stage == selectedStage.Value)
        {
            return;
        }

        var originalStage = row.Stage;
        var targetColor = ProjectStageColorPolicy.GetColor(selectedStage.Value);
        comboBox.IsEnabled = false;
        try
        {
            var outcome = await _followedProjectRepository.UpdateStageAndColorAsync(
                row.Id,
                selectedStage.Value,
                targetColor,
                CancellationToken.None);
            if (outcome == UpdateFollowedProjectStageOutcome.Missing)
            {
                await LoadFollowedProjectsAsync();
                await ReloadProjectPickersAsync();
                SummaryText = $"项目「{row.ProjectName}」已不存在，状态未保存，项目列表已刷新。";
                return;
            }

            await LoadFollowedProjectsAsync();
            await ReloadProjectPickersAsync();
            await Task.WhenAll(
                TodoFeature.Workbench.RefreshAsync(),
                ScheduleOverview.RefreshAsync());
            SummaryText = outcome == UpdateFollowedProjectStageOutcome.Updated
                ? $"已将项目「{row.ProjectName}」的状态更新为{selectedStage.Name}，颜色同步为{DescribeColor(selectedStage.Value)}。"
                : $"项目「{row.ProjectName}」的状态未变化。";
        }
        catch (Exception)
        {
            comboBox.SelectedValue = originalStage;
            SummaryText = $"项目「{row.ProjectName}」状态保存失败，已恢复原状态。";
        }
        finally
        {
            comboBox.IsEnabled = true;
        }
    }

    /// <summary>状态对应颜色的中文名，仅用于状态栏文案。</summary>
    private static string DescribeColor(ProjectStage stage) => ProjectStageColorPolicy.GetColor(stage) switch
    {
        ProjectColorPolicy.Red => "红色",
        ProjectColorPolicy.Blue => "蓝色",
        _ => "灰色"
    };

    /// <summary>项目状态变化后同步刷新周报与简报的项目下拉，避免下拉里仍是旧排序与旧状态文案。</summary>
    private Task ReloadProjectPickersAsync() =>
        Task.WhenAll(
            LoadWeeklyReportProjectsAsync(),
            LoadBriefingProjectsAsync());

    /// <summary>
    /// 读取启用状态的关注项目，并按“在建 → 运维 → 移交”重排。
    /// 排序与状态文案都以数据库最新值为准，不复用界面上已渲染的旧对象。
    /// </summary>
    private async Task<IReadOnlyList<FollowedProject>> GetActiveProjectsAsync()
    {
        await _databaseInitializer.InitializeAsync(CancellationToken.None);
        var projects = await _followedProjectRepository.GetAllAsync(CancellationToken.None);
        // 仓储已按状态排序，这里再排一次以保证排序规则不依赖 SQL 实现。
        return projects
            .Where(project => project.IsActive)
            .OrderBy(project => ProjectStagePolicy.GetOrder(project.Stage))
            .ThenBy(project => project.CreatedAt)
            .ThenBy(project => project.Id)
            .ToArray();
    }

    /// <summary>刷新周报的项目下拉，并尽量保留用户上次选择。</summary>
    private async Task<IReadOnlyList<FollowedProject>> LoadWeeklyReportProjectsAsync()
    {
        var activeProjects = await GetActiveProjectsAsync();
        ApplyProjectsToPicker(WeeklyReportProjectComboBox, activeProjects);
        return activeProjects;
    }

    /// <summary>刷新简报的项目下拉，并尽量保留用户上次选择。</summary>
    private async Task<IReadOnlyList<FollowedProject>> LoadBriefingProjectsAsync()
    {
        var activeProjects = await GetActiveProjectsAsync();
        ApplyProjectsToPicker(BriefingProjectComboBox, activeProjects);
        return activeProjects;
    }

    /// <summary>
    /// 把项目列表写入下拉框：保留仍存在的原选择，否则落到排序后的第一个；
    /// 没有可用项目时清空数据源，避免下拉里残留已删除或已停用的旧项目。
    /// </summary>
    private static void ApplyProjectsToPicker(ComboBox picker, IReadOnlyList<FollowedProject> activeProjects)
    {
        if (picker is null)
        {
            return;
        }

        var previousProjectId = (picker.SelectedItem as FollowedProject)?.Id;
        if (activeProjects.Count == 0)
        {
            picker.ItemsSource = null;
            picker.SelectedItem = null;
            return;
        }

        picker.ItemsSource = activeProjects;
        picker.SelectedItem = activeProjects.FirstOrDefault(project => project.Id == previousProjectId)
            ?? activeProjects[0];
    }

    private async void DeleteFollowedProjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FollowedProjectTreeRow row })
        {
            SummaryText = "请选择要删除的项目。";
            return;
        }

        await _followedProjectKeywordRepository.DeleteByProjectIdAsync(row.Id, CancellationToken.None);
        await _projectMemberRepository.DeleteByProjectIdAsync(row.Id, CancellationToken.None);
        await _followedProjectRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadFollowedProjectsAsync();
        // 删除后周报/简报下拉不能继续保留这个项目。
        await ReloadProjectPickersAsync();
        await TodoFeature.Workbench.RefreshAsync();
        await ScheduleOverview.RefreshAsync();
        SummaryText = $"已删除项目「{row.ProjectName}」及其关键字。";
    }

    /// <summary>在项目树节点内直接追加关键字；按钮 Tag 指向同节点的输入框。</summary>
    private async void AddInlineKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FollowedProjectTreeRow project } button)
        {
            return;
        }

        var input = button.Tag as TextBox;
        var keyword = input?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SummaryText = "请输入项目关键字。";
            input?.Focus();
            return;
        }

        await _followedProjectKeywordRepository.SaveAsync(project.Id, keyword, CancellationToken.None);
        if (input is not null) input.Text = "";
        await LoadFollowedProjectsAsync();
        SummaryText = $"已为「{project.ProjectName}」添加关键字「{keyword}」。";
    }

    /// <summary>删除项目节点下的单个关键字；每个项目至少保留一个匹配关键字。</summary>
    private async void DeleteFollowedProjectKeywordButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FollowedProjectKeywordRow row })
        {
            SummaryText = "请选择要删除的关键字。";
            return;
        }
        if (_currentFollowedProjectKeywords.Count(keyword => keyword.ProjectId == row.ProjectId) <= 1)
        {
            SummaryText = "每个项目至少保留一个关键字；如不再需要，请删除该项目。";
            return;
        }

        await _followedProjectKeywordRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadFollowedProjectsAsync();
        SummaryText = $"已删除项目「{row.ProjectName}」的关键字「{row.Keyword}」。";
    }

    /// <summary>追加项目成员；wxid和邮箱只用于识别直接发来的周报，别名仅用于显示与诊断。</summary>
    private async void AddInlineMemberButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FollowedProjectTreeRow project } button)
        {
            return;
        }

        // 按钮Tag指向同节点输入行，固定顺序为姓名、别名、邮箱、微信wxid。
        var panel = button.Tag as StackPanel;
        var inputs = panel?.Children.OfType<TextBox>().ToArray() ?? Array.Empty<TextBox>();
        var displayName = inputs.Length > 0 ? inputs[0].Text.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            SummaryText = "请输入成员姓名。";
            if (inputs.Length > 0)
            {
                inputs[0].Focus();
            }

            return;
        }

        var alias = inputs.Length > 1 && inputs[1].Text.Trim() is { Length: > 0 } aliasText ? aliasText : null;
        var email = inputs.Length > 2 && inputs[2].Text.Trim() is { Length: > 0 } emailText ? emailText : null;
        var weChatUserId = inputs.Length > 3 && inputs[3].Text.Trim() is { Length: > 0 } weChatIdText
            ? weChatIdText
            : null;
        await _projectMemberRepository.SaveAsync(
            project.Id,
            displayName,
            alias,
            email,
            weChatUserId,
            CancellationToken.None);
        foreach (var input in inputs)
        {
            input.Text = "";
        }

        await LoadFollowedProjectsAsync();
        SummaryText = $"已为「{project.ProjectName}」添加成员「{displayName}」。"
            + (weChatUserId is null && email is null ? "请补充wxid或邮箱后才能归集其直接周报。" : "");
    }

    /// <summary>切换成员是否参与周报，只更新该开关，不覆盖wxid、邮箱或其他成员配置。</summary>
    private async void ProjectMemberReportingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ProjectMemberRow row } checkBox)
        {
            return;
        }

        await _projectMemberRepository.SetReportingAsync(row.Id, checkBox.IsChecked == true, CancellationToken.None);
        await LoadFollowedProjectsAsync();
        SummaryText = $"已{(checkBox.IsChecked == true ? "启用" : "停用")}成员「{row.DisplayName}」的周报归集。";
    }

    /// <summary>删除项目节点下的单个成员。</summary>
    private async void DeleteProjectMemberButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProjectMemberRow row })
        {
            SummaryText = "请选择要删除的成员。";
            return;
        }

        await _projectMemberRepository.DeleteAsync(row.Id, CancellationToken.None);
        await LoadFollowedProjectsAsync();
        SummaryText = $"已删除成员「{row.DisplayName}」。";
    }

    /// <summary>按最长关键字优先匹配群名和消息正文，返回归属项目名称。</summary>
    private string MatchFollowedProject(Message message)
    {
        var match = FollowedProjectMatcher.Match(message, _currentFollowedProjectKeywords);
        return match?.ProjectName ?? "";
    }

    /// <summary>在监听启动前读取模型分析开关，读取失败时安全保持关闭。</summary>
    /// <summary>
    /// 优先级设置保存成功后的统一刷新入口：色板已替换，
    /// 这里重绘待办分组、消息流正文颜色、日程卡片与顶部“高优先级”统计。
    /// </summary>
    private async Task OnPrioritySettingsAppliedAsync()
    {
        await TodoFeature.RefreshAsync();
        await ScheduleOverview.RefreshAsync();
        await RefreshAsync();
    }

    private async Task LoadModelSettingsAsync()
    {
        try
        {
            _analyzeMessageContent = await _modelSettingsRepository.GetAnalyzeMessageContentAsync(
                CancellationToken.None);
            _analyzeAllMessages = await _modelSettingsRepository.GetAnalyzeAllMessagesAsync(
                CancellationToken.None);
            AnalyzeMessageContentCheckBox.IsChecked = _analyzeMessageContent;
            AnalyzeAllMessagesCheckBox.IsChecked = _analyzeAllMessages;
            // 主开关关闭时全量开关不可点击，但保留已保存的勾选状态，重新开启后立即恢复。
            AnalyzeAllMessagesCheckBox.IsEnabled = _analyzeMessageContent;
        }
        catch (Exception exception)
        {
            _analyzeMessageContent = false;
            _analyzeAllMessages = false;
            AnalyzeMessageContentCheckBox.IsChecked = false;
            AnalyzeAllMessagesCheckBox.IsChecked = false;
            AnalyzeAllMessagesCheckBox.IsEnabled = false;
            ModelConfigStatusText = "模型分析开关读取失败，本次保持关闭。";
            System.Diagnostics.Debug.WriteLine(
                $"Model settings load failed: {exception.Message}");
        }
    }

    /// <summary>
    /// 切换到“模型配置”页时用内存快照刷新规则列表：
    /// 详情页新增的规则不会重新读库，但列表必须立刻显示出来。
    /// </summary>
    private void MainContentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl tabs
            || tabs.SelectedItem is not TabItem selected
            || selected.Header?.ToString() != "模型配置")
        {
            return;
        }

        ApplyTodoRules(_todoRuleStore.Current);
    }

    /// <summary>读取自动待办规则：失败时按无规则处理，不影响其他设置与采集。</summary>
    private async Task LoadTodoRulesAsync()
    {
        try
        {
            var rules = await _todoRuleStore.LoadAsync(CancellationToken.None);
            ApplyTodoRules(rules);
            TodoRuleStatusText.Text = rules.Count == 0
                ? "尚未设置规则。添加关键词后，命中该关键词的消息只入库、不自动建待办。"
                : $"已加载 {rules.Count} 条规则，其中 {rules.Count(rule => rule.IsEnabled)} 条启用。";
        }
        catch (Exception exception)
        {
            // 读取失败时保留内存快照，避免已生效的规则被清空。
            ApplyTodoRules(_todoRuleStore.Current);
            TodoRuleStatusText.Text = $"规则读取失败，继续使用上次快照：{exception.Message}";
            System.Diagnostics.Debug.WriteLine($"Todo rules load failed: {exception.Message}");
        }
    }

    /// <summary>把当前规则快照同步到界面列表；快照由 TodoRuleStore 统一维护。</summary>
    private void ApplyTodoRules(IReadOnlyList<TodoRule> rules)
    {
        Replace(TodoRules, rules.Select(rule => new TodoRuleRow(
            rule.Id,
            rule.Text,
            rule.IsEnabled ? "启用" : "已停用")));
    }

    /// <summary>添加规则：空白、过短或过长的输入由服务层拒绝并回显原因。</summary>
    private async void AddTodoRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var text = NewTodoRuleTextBox.Text;
        try
        {
            var normalized = TodoRuleMatcher.Normalize(text);
            await _todoRuleStore.AddAsync(normalized, CancellationToken.None);
            NewTodoRuleTextBox.Text = string.Empty;
            await LoadTodoRulesAsync();
            // 整句长文本几乎不会逐字出现在消息里，这里提示改成短关键词，避免规则形同虚设。
            TodoRuleStatusText.Text = TodoRuleMatcher.Tokenize(normalized).Count == 1 && normalized.Length > 12
                ? $"已记住「{normalized}」，但整句很难在消息中原样出现；建议改填短关键词（例如“验证码”）。"
                : $"已记住「{normalized}」：之后命中该关键词的消息只入库，不再自动建待办。";
        }
        catch (ArgumentException exception)
        {
            // 校验信息直接面向用户，不含内部细节。
            TodoRuleStatusText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            TodoRuleStatusText.Text = $"规则保存失败：{exception.Message}";
        }
    }

    /// <summary>切换选中规则的启用状态；停用后保留配置，可随时恢复。</summary>
    private async void ToggleTodoRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (TodoRulesGrid?.SelectedItem is not TodoRuleRow row)
        {
            TodoRuleStatusText.Text = "请先选中要启用或停用的规则。";
            return;
        }

        var current = _todoRuleStore.Current.FirstOrDefault(rule => rule.Id == row.Id);
        var enabled = current is null || !current.IsEnabled;
        try
        {
            await _todoRuleStore.SetEnabledAsync(row.Id, enabled, CancellationToken.None);
            await LoadTodoRulesAsync();
            TodoRuleStatusText.Text = enabled
                ? $"已启用规则「{row.Text}」。"
                : $"已停用规则「{row.Text}」；已建成的待办不受影响。";
        }
        catch (Exception exception)
        {
            TodoRuleStatusText.Text = $"规则状态保存失败：{exception.Message}";
        }
    }

    /// <summary>删除选中规则；已建立的待办不会被删除。</summary>
    private async void DeleteTodoRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (TodoRulesGrid?.SelectedItem is not TodoRuleRow row)
        {
            TodoRuleStatusText.Text = "请先选中要删除的规则。";
            return;
        }

        try
        {
            await _todoRuleStore.DeleteAsync(row.Id, CancellationToken.None);
            await LoadTodoRulesAsync();
            TodoRuleStatusText.Text = $"已删除规则「{row.Text}」；之后同类消息恢复自动建待办。";
        }
        catch (Exception exception)
        {
            TodoRuleStatusText.Text = $"规则删除失败：{exception.Message}";
        }
    }

    /// <summary>勾选总闸后立即更新内存开关并异步持久化，保存失败时恢复之前的状态。</summary>
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
            // 关闭总闸时全量开关保留勾选但不可操作，重新开启后无需再点一次。
            AnalyzeAllMessagesCheckBox.IsEnabled = enabled;
            ModelConfigStatusText = enabled
                ? $"分析消息内容已开启并保存，立即生效；当前分析范围：{(_analyzeAllMessages ? "所有消息" : "@我 / @所有人及邮件")}。"
                : "分析消息内容已关闭并保存，立即生效；全量分析同时停用，@我 消息仍按原逻辑自动创建待办。";
        }
        catch (Exception exception)
        {
            // UI 与后台采集同时回滚，避免界面状态和实际行为不一致。
            _analyzeMessageContent = previousEnabled;
            AnalyzeMessageContentCheckBox.IsChecked = previousEnabled;
            AnalyzeAllMessagesCheckBox.IsEnabled = previousEnabled;
            ModelConfigStatusText =
                $"分析开关保存失败，已恢复原状态：{exception.Message}";
        }
        finally
        {
            AnalyzeMessageContentCheckBox.IsEnabled = true;
        }
    }

    /// <summary>
    /// 勾选“分析所有消息”后立即扩大新消息的判定范围并异步持久化。
    /// 它依赖总闸；未 @我 的消息只有模型明确判定需要行动才建待办，模型故障时不建。
    /// </summary>
    private async void AnalyzeAllMessagesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_analyzeMessageContent)
        {
            // 正常情况下控件已禁用；这里防御程序化触发，避免产生“看起来已开启”的错觉。
            AnalyzeAllMessagesCheckBox.IsChecked = _analyzeAllMessages;
            AnalyzeAllMessagesCheckBox.IsEnabled = false;
            ModelConfigStatusText = "请先勾选“分析消息内容”，再开启“分析所有消息”。";
            return;
        }

        var previousEnabled = _analyzeAllMessages;
        var enabled = AnalyzeAllMessagesCheckBox.IsChecked == true;
        _analyzeAllMessages = enabled;
        AnalyzeAllMessagesCheckBox.IsEnabled = false;
        try
        {
            await _modelSettingsRepository.SetAnalyzeAllMessagesAsync(
                enabled,
                CancellationToken.None);
            ModelConfigStatusText = enabled
                ? "分析所有消息已开启并保存，立即生效；之后新采集的每条消息都会由大模型判断是否加入待办理，模型不可用时不会创建待办。API 调用量会明显增加。"
                : "分析所有消息已关闭并保存，立即生效；仅 @我 / @所有人 及邮件继续按原规则分析。";
        }
        catch (Exception exception)
        {
            _analyzeAllMessages = previousEnabled;
            AnalyzeAllMessagesCheckBox.IsChecked = previousEnabled;
            ModelConfigStatusText =
                $"全量分析开关保存失败，已恢复原状态：{exception.Message}";
        }
        finally
        {
            AnalyzeAllMessagesCheckBox.IsEnabled = _analyzeMessageContent;
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
    /// <summary>保存石化邮 IMAP 凭据：经 DPAPI 加密写入当前用户的密文文件，密码不回显也不落库。</summary>
    private void SaveSinopecMailCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        var account = MailAccountTextBox.Text.Trim();
        var password = MailPasswordBox.Password;
        if (account.Length == 0 || password.Length == 0)
        {
            MailCredentialStatusText.Text = "账号和密码都不能为空。";
            return;
        }

        try
        {
            new ProtectedImapCredentialStore().Save(new ImapCredential(account, password));
            MailPasswordBox.Clear();
            MailCredentialStatusText.Text = $"已保存：{account}（密文存储）";
            SummaryText = "石化邮账号已保存；勾选“石化邮（IMAP）”并保存设置后开始采集";
        }
        catch (Exception exception)
        {
            MailCredentialStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    /// <summary>清除本机保存的石化邮凭据。</summary>
    private void ClearSinopecMailCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new ProtectedImapCredentialStore().Clear();
            MailPasswordBox.Clear();
            MailCredentialStatusText.Text = "已清除石化邮账号";
        }
        catch (Exception exception)
        {
            MailCredentialStatusText.Text = $"清除失败：{exception.Message}";
        }
    }

    /// <summary>回显已保存的石化邮账号；密码永不回显。</summary>
    private void LoadMailCredentialState()
    {
        var credential = new ProtectedImapCredentialStore().Load();
        MailAccountTextBox.Text = credential?.Account ?? string.Empty;
        MailCredentialStatusText.Text = credential is null
            ? "尚未保存账号密码"
            : $"已保存：{credential.Account}（密文存储）";
    }

    private async void SaveCaptureSourceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTimeOffset.Now;
        // 保存时不再强制石化邮的开关：本地缓存与 IMAP 都由用户勾选，策略只负责剔除不可用的源。
        var settings = CaptureAdapterFactory
            .ApplyMailSourcePolicy(CaptureSourceSettings.Select(row => new CaptureSourceDefinition(
                Source: row.Source,
                DisplayName: row.DisplayName,
                Kind: Enum.TryParse<CaptureSourceKind>(row.Kind, out var kind) ? kind : CaptureSourceKind.JsonlDirectory,
                Location: row.Location,
                IsEnabled: row.IsEnabled)))
            .Select(source => new CaptureSourceSettings(
                Id: 0,
                Source: source.Source,
                DisplayName: source.DisplayName,
                Kind: source.Kind.ToString(),
                Location: source.Location,
                IsEnabled: source.IsEnabled,
                CreatedAt: now,
                UpdatedAt: now))
            .ToList();

        await _settingsRepository.DeleteAllAsync(CancellationToken.None);
        await _settingsRepository.SaveAllAsync(settings, CancellationToken.None);
        // 保存后用数据库真实内容重建列表，让界面与实际生效的采集源保持一致。
        await LoadCaptureSourceSettingsAsync();
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
        var mergedSources = defaultSources.Select(source =>
        {
            var saved = CaptureSourceSettings.FirstOrDefault(row =>
                row.Source == source.Source && row.Kind == source.Kind.ToString());
            return saved is null ? source : source with { IsEnabled = saved.IsEnabled };
        });
        // 石化邮收口：缓存可用时优先读客户端本地缓存，未保存账号时不启用 IMAP。
        var effectiveSources = CaptureAdapterFactory.ApplyMailSourcePolicy(mergedSources);

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
            analyzeMentionAsync: _messageActionModel.AnalyzeMentionAsync,
            // 跨群去重：第二次提醒同一件事时只追加来源消息，不再新建待办。
            detectDuplicateAsync: new TodoDuplicateDetector(_todoRepository, _messageActionModel).DetectAsync,
            // 用户规则优先于模型结论：命中后消息照常入库，但不进入自动待办。
            todoRules: () => _todoRuleStore.Current,
            // 全量分析开关由后台采集循环实时读取，切换后无需重建管线或重启。
            analyzeAllMessages: () => _analyzeAllMessages);
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
            "SINOPECMAIL" => "石化邮",
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

/// <summary>建议状态下拉项：界面显示中文，提交时保留原始枚举值。</summary>
public sealed record ProjectMemoryStatusOption(ProjectMemoryStatus Value, string Name);

/// <summary>
/// 可编辑的简报建议行。默认勾选，但目标状态可以在确认前人工修正；
/// 原始建议保留基准状态和来源 ID，不会因界面编辑丢失并发校验信息。
/// </summary>
public sealed class ProjectMemoryProposalRow : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private ProjectMemoryStatus _proposedStatus;

    public ProjectMemoryProposalRow(ProjectMemoryProposal proposal)
    {
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
        _proposedStatus = proposal.ProposedStatus;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>未编辑的原始建议，包含稳定事项 ID、基准状态及证据白名单。</summary>
    public ProjectMemoryProposal Proposal { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public ProjectMemoryStatus ProposedStatus
    {
        get => _proposedStatus;
        set
        {
            _proposedStatus = value;
            OnPropertyChanged();
        }
    }

    // 以下只读属性直接投影领域建议，供表格列绑定。
    public string CategoryText => ProjectMemoryDisplay.Category(Proposal.Category);
    public string Title => Proposal.Title;
    public string BaseStatusText => ProjectMemoryDisplay.Status(Proposal.BaseStatus);
    public string Reason => Proposal.Reason;
    public int SourceCount => Proposal.SourceMessageIds.Count;

    /// <summary>仅替换用户可编辑的目标状态，其余证据和基准字段保持不变。</summary>
    public ProjectMemoryProposal ToProposal() => Proposal with { ProposedStatus = ProposedStatus };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>项目记忆列表的只读界面行。</summary>
public sealed record ProjectMemoryItemRow(
    long Id,
    string CategoryText,
    string Title,
    string StatusText,
    string UpdatedAtText,
    string LastStatusReason);

/// <summary>来源快照行；ParticipantText 同时保留会话和发送人上下文。</summary>
public sealed record ProjectMemorySourceRow(
    string Source,
    string ParticipantText,
    string ContentSnapshot,
    string SentAtText);

/// <summary>状态时间线行，创建记录的“前状态”显示为新事项。</summary>
public sealed record ProjectMemoryHistoryRow(string ChangedAtText, string ChangeText, string Reason);

/// <summary>项目记忆领域枚举的统一中文显示规则。</summary>
internal static class ProjectMemoryDisplay
{
    public static string Category(ProjectMemoryCategory category) => category switch
    {
        ProjectMemoryCategory.Progress => "进展",
        ProjectMemoryCategory.Risk => "风险",
        ProjectMemoryCategory.Action => "行动",
        _ => "未知"
    };

    /// <summary>可空状态用于新事项和创建历史，不伪造一个领域状态。</summary>
    public static string Status(ProjectMemoryStatus? status) => status switch
    {
        null => "新事项",
        ProjectMemoryStatus.Open => "未开始",
        ProjectMemoryStatus.InProgress => "进行中",
        ProjectMemoryStatus.Blocked => "阻塞",
        ProjectMemoryStatus.Shelved => "已搁置",
        ProjectMemoryStatus.Completed => "已完成",
        ProjectMemoryStatus.Discarded => "已废弃",
        _ => "未知"
    };

    /// <summary>完成和废弃共用终态折叠规则，但语义仍严格区分。</summary>
    public static bool IsTerminal(ProjectMemoryStatus status) =>
        status is ProjectMemoryStatus.Completed or ProjectMemoryStatus.Discarded;
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

/// <summary>自动待办规则行数据：规则原文与当前启用状态。</summary>
public sealed record TodoRuleRow(long Id, string Text, string State);

/// <summary>关注群行数据：用于 UI 展示与删除操作。</summary>
public sealed record FollowedChatRow(long Id, string ChatName);

/// <summary>关注项目行数据：用于 UI 展示与删除操作。</summary>
public sealed record FollowedProjectRow(long Id, string ProjectName, string Color);
public sealed record FollowedProjectKeywordRow(long Id, long ProjectId, string ProjectName, string Keyword);

/// <summary>项目成员行数据：用于 UI 展示与删除操作。</summary>
public sealed record ProjectMemberRow(
    long Id,
    long ProjectId,
    string DisplayName,
    string? Alias,
    string? Email,
    string? WeChatUserId,
    bool IsReporting)
{
    /// <summary>界面上把别名与邮箱拼成一行说明；缺项自动省略。</summary>
    public string Detail =>
        string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(Alias) ? null : $"别名：{Alias}",
            string.IsNullOrWhiteSpace(Email) ? null : Email
        }.Where(part => part is not null));
}

/// <summary>关注设置的层级节点：一个项目 + 它名下的全部关键字，供可折叠树绑定。</summary>
public sealed record FollowedProjectTreeRow(
    long Id,
    string ProjectName,
    string Color,
    ProjectStage Stage,
    string KeywordSummary,
    string MemberSummary,
    IReadOnlyList<FollowedProjectKeywordRow> Keywords,
    IReadOnlyList<ProjectMemberRow> Members)
{
    /// <summary>行首圆点颜色：始终取状态对应的固定颜色，历史遗留的自选颜色不会影响显示。</summary>
    public string StageColor => ProjectStageColorPolicy.GetColor(Stage);
}

/// <summary>项目状态下拉项：枚举值 + 中文显示名 + 该状态对应的固定颜色（下拉圆点）。</summary>
public sealed record ProjectStageOption(ProjectStage Value, string Name, string Color);
