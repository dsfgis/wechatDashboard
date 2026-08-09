using System.Windows.Threading;
using WechatDashboard.App.ViewModels.Messages;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.Application.Capture;
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
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;

    public TodoFeatureCoordinator(
        string databasePath,
        Func<IReadOnlyList<string>> aliases,
        Func<IReadOnlyList<string>> followedChats,
        Func<FollowedChatFilterMode> filterMode,
        Action<int> selectTab)
    {
        _selectTab = selectTab;
        var clock = SystemClock.Instance;
        var todoRepository = new SqliteTodoRepository(databasePath);
        var messageRepository = new SqliteMessageRepository(databasePath);
        var reminderRepository = new SqliteReminderRepository(databasePath);
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
            new TodoDueBucketPolicy(TimeZoneInfo.Local),
            clock,
            dialogs,
            NavigateToMessageAsync);
        MessageFeed = new MessageFeedViewModel(
            messageRepository,
            todoService,
            aliases,
            followedChats,
            filterMode,
            ShowTodoAsync);
        var publisher = new InAppReminderNotificationPublisher(reminderService, clock, Workbench.RefreshAsync);
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
