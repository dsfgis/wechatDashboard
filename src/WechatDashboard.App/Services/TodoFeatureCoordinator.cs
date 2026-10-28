using System.Windows.Threading;
using WechatDashboard.App.ViewModels.Messages;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
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
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;

    public TodoFeatureCoordinator(
        string databasePath,
        IMessageActionModel messageActionModel,
        Func<IReadOnlyList<string>> aliases,
        Func<IReadOnlyList<string>> followedChats,
        Func<FollowedChatFilterMode> filterMode,
        Action<int> selectTab,
        Func<Task> onTodoCreated)
    {
        _selectTab = selectTab;
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
            onTodoCreated);
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
            Workbench.RefreshAsync);
        _reminderQueue = new ReminderNotificationQueue(resolutionHandler.HandleAsync);
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
