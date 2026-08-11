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
        TogglePinCommand = new AsyncRelayCommand(TogglePinAsync, parameter => parameter is TodoListItemViewModel);
        SelectAllCommand = new RelayCommand(_ => ToggleSelectAll(), _ => ActiveCount > 0);
        CompleteSelectedCommand = new AsyncRelayCommand(_ => CompleteSelectedAsync(), _ => SelectedCount > 0);
        ClearCompletedCommand = new AsyncRelayCommand(_ => ClearCompletedAsync(), _ => Completed.Count > 0);
    }

    public ObservableCollection<TodoListItemViewModel> Pinned { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Overdue { get; } = new();
    public ObservableCollection<TodoListItemViewModel> DueToday { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Upcoming { get; } = new();
    public ObservableCollection<TodoListItemViewModel> NoDueDate { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Completed { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand OpenDetailCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand TogglePinCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public AsyncRelayCommand CompleteSelectedCommand { get; }
    public AsyncRelayCommand ClearCompletedCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int ActiveCount => Pinned.Count + Overdue.Count + DueToday.Count + Upcoming.Count + NoDueDate.Count;
    public int SelectedCount => ActiveRows().Count(row => row.IsSelected);
    public int OverdueCount => Pinned.Count(row => row.Bucket == TodoDueBucket.Overdue) + Overdue.Count;
    public string SelectAllButtonText => ActiveCount > 0 && SelectedCount == ActiveCount ? "取消全选" : "全选";

    public async Task RefreshAsync()
    {
        var active = await _todos.GetActiveAsync(CancellationToken.None);
        var completed = await _todos.GetCompletedAsync(CancellationToken.None);
        var rows = await CreateRowsAsync(active.Concat(completed));
        var activeIds = active.Select(item => item.Id).ToHashSet();
        var activeRows = rows.Where(row => activeIds.Contains(row.Todo.Id)).ToArray();
        Replace(Pinned, activeRows.Where(row => row.IsPinned).OrderByDescending(row => row.Todo.UpdatedAt));
        Replace(Overdue, activeRows.Where(row => !row.IsPinned && row.Bucket == TodoDueBucket.Overdue));
        Replace(DueToday, activeRows.Where(row => !row.IsPinned && row.Bucket == TodoDueBucket.DueToday));
        Replace(Upcoming, activeRows.Where(row => !row.IsPinned && row.Bucket == TodoDueBucket.Upcoming));
        Replace(NoDueDate, activeRows.Where(row => !row.IsPinned && row.Bucket == TodoDueBucket.NoDueDate));
        Replace(Completed, rows.Where(row => !activeIds.Contains(row.Todo.Id))
            .OrderByDescending(row => row.Todo.CompletedAt ?? row.Todo.UpdatedAt));
        StatusText = $"活动待办 {ActiveCount} 条，其中已置顶 {Pinned.Count} 条、已逾期 {OverdueCount} 条。";
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(OverdueCount));
        UpdateSelectionState();
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

    private async Task TogglePinAsync(object? parameter)
    {
        if (parameter is not TodoListItemViewModel row)
        {
            return;
        }

        var isPinned = !row.IsPinned;
        var updated = await _todos.SetPinnedAsync(row.Id, isPinned, _clock.Now, CancellationToken.None);
        if (!updated)
        {
            _dialogs.ShowError("待办已被其他操作修改，请刷新后重试。", isPinned ? "置顶失败" : "取消置顶失败");
            await RefreshAsync();
            return;
        }

        await RefreshAsync();
        StatusText = isPinned ? "待办已置顶。" : "已取消置顶。";
    }

    private void ToggleSelectAll()
    {
        var shouldSelect = SelectedCount != ActiveCount;
        foreach (var row in ActiveRows())
        {
            row.IsSelected = shouldSelect;
        }

        UpdateSelectionState();
    }

    private async Task CompleteSelectedAsync()
    {
        var selectedIds = ActiveRows().Where(row => row.IsSelected).Select(row => row.Id).Distinct().ToArray();
        if (selectedIds.Length == 0 ||
            !_dialogs.Confirm($"确定将勾选的 {selectedIds.Length} 条待办移动到已办吗？未触发的提醒将同时取消。", "勾选项转为已办"))
        {
            return;
        }

        var updated = await _todos.MarkSelectedCompletedAsync(selectedIds, _clock.Now, CancellationToken.None);
        await RefreshAsync();
        StatusText = updated == 0
            ? "没有勾选项被移动，列表可能已被其他操作更新。"
            : $"已将勾选的 {updated} 条待办移动到已办。";
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
            rows.Add(new TodoListItemViewModel(
                todo,
                message,
                currentReminder,
                _bucketPolicy.Classify(todo.DueAt, _clock.Now),
                UpdateSelectionState));
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

    private IEnumerable<TodoListItemViewModel> ActiveRows() =>
        Pinned.Concat(Overdue).Concat(DueToday).Concat(Upcoming).Concat(NoDueDate);

    private void UpdateSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectAllButtonText));
        CompleteSelectedCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
    }
}

public sealed class TodoListItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public TodoListItemViewModel(
        TodoItem todo,
        Message? sourceMessage,
        TodoReminder? reminder,
        TodoDueBucket bucket,
        Action selectionChanged)
    {
        Todo = todo;
        SourceMessage = sourceMessage;
        Reminder = reminder;
        Bucket = bucket;
        _selectionChanged = selectionChanged;
    }

    public TodoItem Todo { get; }
    public Message? SourceMessage { get; }
    public TodoReminder? Reminder { get; }
    public TodoDueBucket Bucket { get; }
    public long Id => Todo.Id;
    public bool IsPinned => Todo.IsPinned;
    public string PinButtonText => IsPinned ? "取消置顶" : "置顶";
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }
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
