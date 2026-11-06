using System.Windows.Threading;
using WechatDashboard.App.ViewModels.Messages;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.TodoRules;
using WechatDashboard.Infrastructure.Background;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.App.Services;

/// <summary>组合 Todo/消息功能并协调跨页面导航，不把业务流程放进 MainWindow。</summary>
public sealed class TodoFeatureCoordinator : IDisposable
{
    private readonly Action<int> _selectTab;
    private readonly DispatcherTimer _dueRefreshTimer;
    private readonly ReminderWorker _reminderWorker;
    private readonly ReminderNotificationQueue _reminderQueue;
    // 主窗口注入的共享日历视图模型刷新：日程概览和桌面日历共用同一实例。
    private readonly Func<Task>? _refreshCalendar;
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;

    public TodoFeatureCoordinator(
        string databasePath,
        IMessageActionModel messageActionModel,
        Func<IReadOnlyList<string>> aliases,
        Func<IReadOnlyList<string>> followedChats,
        Func<FollowedChatFilterMode> filterMode,
        TodoRuleStore todoRuleStore,
        Action<int> selectTab,
        Func<Task> onTodoCreated,
        Func<Task>? refreshCalendar = null)
    {
        _selectTab = selectTab;
        _refreshCalendar = refreshCalendar;
        var clock = SystemClock.Instance;
        var todoRepository = new SqliteTodoRepository(databasePath);
        var messageRepository = new SqliteMessageRepository(databasePath);
        var reminderRepository = new SqliteReminderRepository(databasePath);
        var projectRepository = new SqliteFollowedProjectRepository(databasePath);
        var unitOfWork = new SqliteTodoUnitOfWork(databasePath);
        var todoService = new TodoApplicationService(unitOfWork, clock);
        var reminderService = new ReminderApplicationService(unitOfWork, clock);
        var dialogs = new TodoDialogService();
        Workbench = new TodoWorkbenchViewModel(
            todoRepository,
            messageRepository,
            reminderRepository,
            todoService,
            reminderService,
            projectRepository,
            new TodoDueBucketPolicy(TimeZoneInfo.Local),
            clock,
            dialogs,
            NavigateToMessageAsync,
            onTodoCreated,
            todoRuleStore,
            // 工作台自身会刷新；这里补齐消息流与日历，保证状态改回活动后所有视图同步。
            refreshRelatedViews: () => Task.WhenAll(
                MessageFeed.RefreshAsync(),
                RefreshCalendarAsync()));
        MessageFeed = new MessageFeedViewModel(
            messageRepository,
            todoRepository,
            todoService,
            messageActionModel,
            aliases,
            followedChats,
            filterMode,
            ShowTodoAsync);
        // 提醒展示与处理解耦：队列负责右下角堆叠与顺序，处理器负责把用户选择写回仓储。
        var resolutionHandler = new ReminderResolutionHandler(
            reminderService,
            todoRepository,
            clock,
            // 提醒卡片上的“完成”也会改变待办状态，日历必须一起刷新。
            () => RefreshAllViewsAsync());
        // 详情按钮复用工作台现有详情流程，根据提醒关联的待办ID直接打开。
        _reminderQueue = new ReminderNotificationQueue(
            resolutionHandler.HandleAsync,
            item => Workbench.OpenTodoAsync(item.Reminder.TodoId));
        var publisher = new InAppReminderNotificationPublisher(_reminderQueue);
        _reminderWorker = new ReminderWorker(reminderRepository, publisher, clock);
        _dueRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _dueRefreshTimer.Tick += async (_, _) => await Workbench.RefreshAsync();
    }

    public TodoWorkbenchViewModel Workbench { get; }
    public MessageFeedViewModel MessageFeed { get; }

    public Task StartAsync()
    {
        _dueRefreshTimer.Start();
        _workerCancellation = new CancellationTokenSource();
        _workerTask = RunWorkerSafelyAsync(_workerCancellation.Token);
        return Task.CompletedTask;
    }

    public Task RefreshAsync() => Task.WhenAll(Workbench.RefreshAsync(), MessageFeed.RefreshAsync());

    /// <summary>刷新共享日历视图模型（主窗口日程概览与桌面日历窗口共用）。</summary>
    private Task RefreshCalendarAsync() => _refreshCalendar?.Invoke() ?? Task.CompletedTask;

    /// <summary>待办、消息流、日历三处一起刷新，用于会改变待办状态的入口。</summary>
    private Task RefreshAllViewsAsync() => Task.WhenAll(RefreshAsync(), RefreshCalendarAsync());

    public async Task StopAsync()
    {
        _dueRefreshTimer.Stop();
        // 退出时收起仍显示的提醒卡片，避免留下无人处理的孤立窗口。
        _reminderQueue.Dispose();
        if (_workerCancellation is null)
        {
            return;
        }

        await _workerCancellation.CancelAsync();
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _workerCancellation.Dispose();
        _workerCancellation = null;
        _workerTask = null;
    }

    public void Dispose()
    {
        _dueRefreshTimer.Stop();
        _workerCancellation?.Cancel();
        _workerCancellation?.Dispose();
        _reminderQueue.Dispose();
    }

    private async Task ShowTodoAsync(CreateTodoResult result)
    {
        if (result.Todo is null)
        {
            return;
        }

        await Workbench.RefreshAsync();
        _selectTab(0);
        await Workbench.OpenTodoAsync(result.Todo.Id);
    }

    private async Task NavigateToMessageAsync(long messageId)
    {
        // 先登记跳转目标：切页会触发消息流自动刷新，未登记时刷新结果会覆盖刚定位到的上下文
        MessageFeed.PrepareContextNavigation(messageId);
        _selectTab(2);
        await MessageFeed.ShowContextAsync(messageId);
    }

    private async Task RunWorkerSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _reminderWorker.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
