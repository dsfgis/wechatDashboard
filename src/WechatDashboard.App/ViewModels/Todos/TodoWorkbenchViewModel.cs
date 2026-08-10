using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.App.Services;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

public sealed class TodoWorkbenchViewModel : ObservableObject
{
    private readonly ITodoRepository _todos;
    private readonly IMessageRepository _messages;
    private readonly IReminderRepository _reminders;
    private readonly TodoApplicationService _todoService;
    private readonly ReminderApplicationService _reminderService;
    private readonly TodoDueBucketPolicy _bucketPolicy;
    private readonly IClock _clock;
    private readonly ITodoDialogService _dialogs;
    private readonly Func<long, Task> _navigateToMessage;
    private string _statusText = "正在加载待办...";

    public TodoWorkbenchViewModel(
        ITodoRepository todos,
        IMessageRepository messages,
        IReminderRepository reminders,
        TodoApplicationService todoService,
        ReminderApplicationService reminderService,
        TodoDueBucketPolicy bucketPolicy,
        IClock clock,
        ITodoDialogService dialogs,
        Func<long, Task> navigateToMessage)
    {
        _todos = todos;
        _messages = messages;
        _reminders = reminders;
        _todoService = todoService;
        _reminderService = reminderService;
        _bucketPolicy = bucketPolicy;
        _clock = clock;
        _dialogs = dialogs;
        _navigateToMessage = navigateToMessage;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        OpenDetailCommand = new AsyncRelayCommand(OpenDetailAsync, parameter => parameter is TodoListItemViewModel);
        CompleteCommand = new AsyncRelayCommand(CompleteAsync, parameter => parameter is TodoListItemViewModel);
        CompleteAllCommand = new AsyncRelayCommand(_ => CompleteAllAsync(), _ => ActiveCount > 0);
        ClearCompletedCommand = new AsyncRelayCommand(_ => ClearCompletedAsync(), _ => Completed.Count > 0);
    }

    public ObservableCollection<TodoListItemViewModel> Overdue { get; } = new();
    public ObservableCollection<TodoListItemViewModel> DueToday { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Upcoming { get; } = new();
    public ObservableCollection<TodoListItemViewModel> NoDueDate { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Completed { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand OpenDetailCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand CompleteAllCommand { get; }
    public AsyncRelayCommand ClearCompletedCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int ActiveCount => Overdue.Count + DueToday.Count + Upcoming.Count + NoDueDate.Count;

    public async Task RefreshAsync()
    {
        var active = await _todos.GetActiveAsync(CancellationToken.None);
        var completed = await _todos.GetCompletedAsync(CancellationToken.None);
        var rows = await CreateRowsAsync(active.Concat(completed));
        var activeIds = active.Select(item => item.Id).ToHashSet();
        Replace(Overdue, rows.Where(row => activeIds.Contains(row.Todo.Id) && row.Bucket == TodoDueBucket.Overdue));
        Replace(DueToday, rows.Where(row => activeIds.Contains(row.Todo.Id) && row.Bucket == TodoDueBucket.DueToday));
        Replace(Upcoming, rows.Where(row => activeIds.Contains(row.Todo.Id) && row.Bucket == TodoDueBucket.Upcoming));
        Replace(NoDueDate, rows.Where(row => activeIds.Contains(row.Todo.Id) && row.Bucket == TodoDueBucket.NoDueDate));
        Replace(Completed, rows.Where(row => !activeIds.Contains(row.Todo.Id))
            .OrderByDescending(row => row.Todo.CompletedAt ?? row.Todo.UpdatedAt));
        StatusText = $"活动待办 {ActiveCount} 条，其中已逾期 {Overdue.Count} 条、今日到期 {DueToday.Count} 条。";
        OnPropertyChanged(nameof(ActiveCount));
        CompleteAllCommand.RaiseCanExecuteChanged();
        ClearCompletedCommand.RaiseCanExecuteChanged();
    }

    public async Task OpenTodoAsync(long todoId)
    {
        var todo = await _todos.GetByIdAsync(todoId, CancellationToken.None);
        if (todo is null)
        {
            _dialogs.ShowError("待办已不存在。", "打开失败");
            return;
        }

        var row = (await CreateRowsAsync(new[] { todo })).Single();
        await ShowDetailAsync(row);
    }

    private async Task OpenDetailAsync(object? parameter)
    {
        if (parameter is TodoListItemViewModel row)
        {
            await ShowDetailAsync(row);
        }
    }

    private async Task ShowDetailAsync(TodoListItemViewModel row)
    {
        var viewModel = new TodoDetailViewModel(
            row,
            _todoService,
            _reminderService,
            _reminders,
            _clock,
            async () => await RefreshAsync(),
            _navigateToMessage,
            _dialogs);
        await _dialogs.ShowAsync(viewModel);
    }

    private async Task CompleteAsync(object? parameter)
    {
        if (parameter is not TodoListItemViewModel row)
        {
            return;
        }

        var updated = await _todoService.UpdateAsync(
            new UpdateTodoRequest(
                row.Todo.Id,
                row.Todo.Title,
                row.Todo.Description,
                row.Todo.ProjectId,
                row.Todo.Priority,
                row.Todo.DueAt,
                TodoStatus.Done,
                row.Todo.UpdatedAt),
            CancellationToken.None);
        if (updated is null)
        {
            _dialogs.ShowError("待办已被其他操作修改，请刷新后重试。", "完成失败");
        }

        await RefreshAsync();
    }

    private async Task CompleteAllAsync()
    {
        var activeCount = ActiveCount;
        if (activeCount == 0 ||
            !_dialogs.Confirm($"确定将全部 {activeCount} 条活动待办移动到已办吗？未触发的提醒将同时取消。", "全部移动到已办"))
        {
            return;
        }

        var updated = await _todos.MarkAllCompletedAsync(_clock.Now, CancellationToken.None);
        await RefreshAsync();
        StatusText = updated == 0
            ? "没有可移动的活动待办，列表可能已被其他操作更新。"
            : $"已将 {updated} 条待办移动到已办。";
    }

    private async Task ClearCompletedAsync()
    {
        if (!_dialogs.Confirm($"确定永久删除全部 {Completed.Count} 条已办理记录吗？原始消息不会被删除。", "清空已办理"))
        {
            return;
        }

        await _todos.DeleteCompletedAsync(CancellationToken.None);
        await RefreshAsync();
    }

    private async Task<IReadOnlyList<TodoListItemViewModel>> CreateRowsAsync(IEnumerable<TodoItem> source)
    {
        var todos = source.ToArray();
        var messageIds = todos.Where(item => item.SourceMessageId.HasValue).Select(item => item.SourceMessageId!.Value).Distinct().ToArray();
        var messageLookup = (await _messages.GetByIdsAsync(messageIds, CancellationToken.None)).ToDictionary(item => item.Id);
        var rows = new List<TodoListItemViewModel>(todos.Length);
        foreach (var todo in todos)
        {
            var reminders = await _reminders.GetForTodoAsync(todo.Id, CancellationToken.None);
            var currentReminder = reminders.LastOrDefault(item => item.Status is ReminderStatus.Scheduled or ReminderStatus.Dispatching or ReminderStatus.Delivered);
            messageLookup.TryGetValue(todo.SourceMessageId ?? 0, out var message);
            rows.Add(new TodoListItemViewModel(todo, message, currentReminder, _bucketPolicy.Classify(todo.DueAt, _clock.Now)));
        }

        return rows;
    }

    private static void Replace(ObservableCollection<TodoListItemViewModel> target, IEnumerable<TodoListItemViewModel> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

public sealed class TodoListItemViewModel
{
    public TodoListItemViewModel(TodoItem todo, Message? sourceMessage, TodoReminder? reminder, TodoDueBucket bucket)
    {
        Todo = todo;
        SourceMessage = sourceMessage;
        Reminder = reminder;
        Bucket = bucket;
    }

    public TodoItem Todo { get; }
    public Message? SourceMessage { get; }
    public TodoReminder? Reminder { get; }
    public TodoDueBucket Bucket { get; }
    public long Id => Todo.Id;
    public string Priority => Todo.Priority.ToString();
    public string Status => Todo.Status.ToString();
    public string Title => Todo.Title;
    public string DueAt => Todo.DueAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string ReminderAt => Reminder?.ScheduledAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string Source => SourceMessage is null ? "-" : FormatSource(SourceMessage.Source);
    public string SourceChatName => SourceMessage?.ChatName ?? "无来源消息";
    public string SenderName => SourceMessage?.SenderName ?? "-";
    public string MessageContent => SourceMessage?.Content ?? Todo.Title;
    public string SentAt => SourceMessage?.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string CompletedAt => Todo.CompletedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";

    private static string FormatSource(string source) => source switch
    {
        "WeChat" => "微信",
        "Shihuatong" => "石化通",
        "Feishu" => "飞书",
        "DingTalk" => "钉钉",
        "Sample" => "示例",
        _ => source
    };
}
