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
    private readonly IFollowedProjectRepository _projectRepository;
    private readonly TodoDueBucketPolicy _bucketPolicy;
    private readonly IClock _clock;
    private readonly ITodoDialogService _dialogs;
    private readonly Func<long, Task> _navigateToMessage;
    private readonly Func<Task> _onTodoCreated;
    private string _statusText = "正在加载待办...";
    private int _activeCount;
    // 刷新会重建优先级分组，折叠状态按层级记住，避免每分钟自动刷新把用户折叠的分组重新展开。
    private readonly HashSet<PriorityLevel> _collapsedPriorityLevels = new();

    public TodoWorkbenchViewModel(
        ITodoRepository todos,
        IMessageRepository messages,
        IReminderRepository reminders,
        TodoApplicationService todoService,
        ReminderApplicationService reminderService,
        IFollowedProjectRepository projectRepository,
        TodoDueBucketPolicy bucketPolicy,
        IClock clock,
        ITodoDialogService dialogs,
        Func<long, Task> navigateToMessage,
        Func<Task> onTodoCreated)
    {
        _todos = todos;
        _messages = messages;
        _reminders = reminders;
        _todoService = todoService;
        _reminderService = reminderService;
        _projectRepository = projectRepository;
        _bucketPolicy = bucketPolicy;
        _clock = clock;
        _dialogs = dialogs;
        _navigateToMessage = navigateToMessage;
        _onTodoCreated = onTodoCreated;
        AddTodoCommand = new AsyncRelayCommand(_ => AddTodoAsync());
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        OpenDetailCommand = new AsyncRelayCommand(OpenDetailAsync, parameter => parameter is TodoListItemViewModel);
        CompleteCommand = new AsyncRelayCommand(CompleteAsync, parameter => parameter is TodoListItemViewModel);
        TogglePinCommand = new AsyncRelayCommand(TogglePinAsync, parameter => parameter is TodoListItemViewModel);
        SelectAllCommand = new RelayCommand(_ => ToggleSelectAll(), _ => ActiveCount > 0);
        SelectCategoryCommand = new RelayCommand(SelectCategory, CanSelectCategory);
        CompleteSelectedCommand = new AsyncRelayCommand(_ => CompleteSelectedAsync(), _ => SelectedCount > 0);
        ClearCompletedCommand = new AsyncRelayCommand(_ => ClearCompletedAsync(), _ => Completed.Count > 0);
    }

    public ObservableCollection<TodoListItemViewModel> Pinned { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Overdue { get; } = new();
    public ObservableCollection<TodoListItemViewModel> DueToday { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Upcoming { get; } = new();
    public ObservableCollection<TodoListItemViewModel> NoDueDate { get; } = new();

    /// <summary>
    /// 按当前配置的优先级层数展示全部活动待办（默认 P0–P3，可在“优先级设置”中改为 2–6 级）；
    /// 已办记录不进入优先级分组。
    /// </summary>
    public ObservableCollection<PriorityGroupViewModel> PriorityGroups { get; } = new();
    public ObservableCollection<TodoListItemViewModel> Completed { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand AddTodoCommand { get; }
    public AsyncRelayCommand OpenDetailCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand TogglePinCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectCategoryCommand { get; }
    public AsyncRelayCommand CompleteSelectedCommand { get; }
    public AsyncRelayCommand ClearCompletedCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>活动待办去重数量。置顶待办同时出现在置顶组和所属截止时间组，不能按各集合长度累加。</summary>
    public int ActiveCount => _activeCount;
    public int SelectedCount => ActiveRows().Count(row => row.IsSelected);
    public int OverdueCount => Overdue.Count;
    public string SelectAllButtonText => ActiveCount > 0 && SelectedCount == ActiveCount ? "取消全选" : "全选";

    public async Task RefreshAsync()
    {
        // Refreshes rebuild the row view models. Preserve the current UI selection by
        // stable Todo id so timer/reminder refreshes do not look like a scroll reset.
        var selectedIds = ActiveRows().Where(row => row.IsSelected).Select(row => row.Id).ToHashSet();
        var snapshot = await Task.Run(async () =>
        {
            var activeTask = _todos.GetActiveAsync(CancellationToken.None);
            var completedTask = _todos.GetCompletedAsync(CancellationToken.None);
            await Task.WhenAll(activeTask, completedTask);
            var activeItems = activeTask.Result;
            var completedItems = completedTask.Result;
            var itemRows = await CreateRowsAsync(activeItems.Concat(completedItems));
            return (Active: activeItems, Rows: itemRows);
        });
        var active = snapshot.Active;
        var rows = snapshot.Rows;
        var activeIds = active.Select(item => item.Id).ToHashSet();
        var activeRows = rows.Where(row => activeIds.Contains(row.Todo.Id)).ToArray();
        foreach (var row in activeRows.Where(row => selectedIds.Contains(row.Id)))
        {
            row.IsSelected = true;
        }

        _activeCount = activeRows.Length;
        Replace(Pinned, activeRows.Where(row => row.IsPinned).OrderByDescending(row => row.Todo.UpdatedAt));
        // 置顶待办同时保留在其截止时间分组中（置顶组是第二展示维度），组内置顶项排在最前，避免“置顶后从逾期组消失”。
        Replace(Overdue, activeRows.Where(row => row.Bucket == TodoDueBucket.Overdue).OrderByDescending(row => row.IsPinned));
        Replace(DueToday, activeRows.Where(row => row.Bucket == TodoDueBucket.DueToday).OrderByDescending(row => row.IsPinned));
        Replace(Upcoming, activeRows.Where(row => row.Bucket == TodoDueBucket.Upcoming).OrderByDescending(row => row.IsPinned));
        Replace(NoDueDate, activeRows.Where(row => row.Bucket == TodoDueBucket.NoDueDate).OrderByDescending(row => row.IsPinned));

        // 优先级是独立查看维度，包含置顶活动待办，但不读取已办记录。
        // 分组数量与颜色跟随“优先级设置”，超出当前层数的历史待办按最低一级归组。
        var palette = PrioritySettings.Current;
        var groups = palette.Levels
            .Select(definition => new PriorityGroupViewModel(
                definition.Level,
                definition.Code,
                definition.Color,
                !_collapsedPriorityLevels.Contains(definition.Level),
                OnPriorityGroupExpandedChanged))
            .ToArray();
        foreach (var group in groups)
        {
            Replace(group.Items, activeRows.Where(row => palette.Normalize(row.Todo.Priority) == group.Level));
        }

        ReplaceGroups(groups);
        Replace(Completed, rows.Where(row => !activeIds.Contains(row.Todo.Id))
            .OrderByDescending(row => row.Todo.CompletedAt ?? row.Todo.UpdatedAt));
        StatusText = $"活动待办 {ActiveCount} 条（去重）：已置顶 {Pinned.Count} 条（同时计入所属截止时间分组）、已逾期 {Overdue.Count} 条、今日到期 {DueToday.Count} 条、后续到期 {Upcoming.Count} 条、无截止时间 {NoDueDate.Count} 条。";
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
            _todos,
            _projectRepository,
            _reminderService,
            _reminders,
            _clock,
            async () => await RefreshAsync(),
            _navigateToMessage,
            _dialogs);
        await viewModel.LoadProjectsAsync();
        await _dialogs.ShowAsync(viewModel);
    }

    private async Task AddTodoAsync()
    {
        var viewModel = new TodoCreateViewModel(
            _todoService,
            _projectRepository,
            _clock,
            _dialogs,
            _onTodoCreated);
        await viewModel.LoadProjectsAsync();
        await _dialogs.ShowCreateAsync(viewModel);
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
                row.Todo.UpdatedAt,
                GroupId: null,
                ManagedProjectId: row.Todo.ManagedProjectId),
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

    private static bool CanSelectCategory(object? parameter) =>
        parameter is IEnumerable<TodoListItemViewModel> rows && rows.Any();

    private void SelectCategory(object? parameter)
    {
        if (parameter is not IEnumerable<TodoListItemViewModel> rows)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.IsSelected = true;
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
        var todoIds = todos.Select(item => item.Id).ToArray();
        var projectsTask = _projectRepository.GetAllAsync(CancellationToken.None);
        var messagesTask = _messages.GetByIdsAsync(messageIds, CancellationToken.None);
        // 一条待办可能被多个群的多条消息共同触发，这里批量取回全部来源消息用于原文展示。
        var sourceMessagesTask = _todos.GetSourceMessagesForTodosAsync(todoIds, CancellationToken.None);
        var remindersTask = _reminders.GetForTodosAsync(todoIds, CancellationToken.None);
        await Task.WhenAll(projectsTask, messagesTask, sourceMessagesTask, remindersTask);
        var projectLookup = projectsTask.Result.Where(item => item.IsActive).ToDictionary(item => item.Id);
        var messageLookup = messagesTask.Result.ToDictionary(item => item.Id);
        var sourceMessagesLookup = sourceMessagesTask.Result;
        var reminderLookup = remindersTask.Result;
        var rows = new List<TodoListItemViewModel>(todos.Length);
        foreach (var todo in todos)
        {
            var reminders = reminderLookup.GetValueOrDefault(todo.Id) ?? Array.Empty<TodoReminder>();
            var currentReminder = reminders.LastOrDefault(item => item.Status is ReminderStatus.Scheduled or ReminderStatus.Dispatching or ReminderStatus.Delivered);
            messageLookup.TryGetValue(todo.SourceMessageId ?? 0, out var message);
            var sourceMessages = sourceMessagesLookup.GetValueOrDefault(todo.Id);
            // 关联表缺失时回退到主来源消息，保证历史数据仍能显示原文。
            if (sourceMessages is null or { Count: 0 })
            {
                sourceMessages = message is null ? Array.Empty<Message>() : new[] { message };
            }

            var project = todo.ManagedProjectId is { } projectId && projectLookup.TryGetValue(projectId, out var resolvedProject)
                ? resolvedProject
                : null;
            rows.Add(new TodoListItemViewModel(
                todo,
                message,
                currentReminder,
                _bucketPolicy.Classify(todo.DueAt, _clock.Now),
                project?.ProjectName ?? "未指定项目",
                project?.Color ?? ProjectColorPolicy.DefaultColor,
                UpdateSelectionState,
                sourceMessages));
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

    /// <summary>整体替换优先级分组集合；分组对象在每次刷新时按当前层数与颜色重建。</summary>
    private void ReplaceGroups(IEnumerable<PriorityGroupViewModel> groups)
    {
        PriorityGroups.Clear();
        foreach (var group in groups)
        {
            PriorityGroups.Add(group);
        }
    }

    /// <summary>记录用户对优先级分组的折叠操作，供下一次刷新还原。</summary>
    private void OnPriorityGroupExpandedChanged(PriorityLevel level, bool isExpanded)
    {
        if (isExpanded)
        {
            _collapsedPriorityLevels.Remove(level);
        }
        else
        {
            _collapsedPriorityLevels.Add(level);
        }
    }

    /// <summary>全部活动待办行；置顶行会同时存在于置顶组和截止时间组，故按引用去重后再参与选择与统计。</summary>
    private IEnumerable<TodoListItemViewModel> ActiveRows() =>
        Pinned.Concat(Overdue).Concat(DueToday).Concat(Upcoming).Concat(NoDueDate).Distinct();

    private void UpdateSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectAllButtonText));
        CompleteSelectedCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        SelectCategoryCommand.RaiseCanExecuteChanged();
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
        string projectName,
        string projectColor,
        Action selectionChanged,
        IReadOnlyList<Message>? sourceMessages = null)
    {
        Todo = todo;
        SourceMessage = sourceMessage;
        SourceMessages = sourceMessages is { Count: > 0 } messages
            ? messages
            : sourceMessage is null ? Array.Empty<Message>() : new[] { sourceMessage };
        Reminder = reminder;
        Bucket = bucket;
        ProjectName = projectName;
        ProjectColor = projectColor;
        _selectionChanged = selectionChanged;
    }

    public TodoItem Todo { get; }
    public Message? SourceMessage { get; }

    /// <summary>触发该待办的全部来源消息，主来源在前；跨群重复提醒会有多条。</summary>
    public IReadOnlyList<Message> SourceMessages { get; }

    /// <summary>来源消息条数，用于列表角标提示存在多群提醒。</summary>
    public int SourceMessageCount => SourceMessages.Count;

    /// <summary>多于一条来源消息时显示的角标文本；单条来源返回空串。</summary>
    public string SourceMessageBadge => SourceMessageCount > 1 ? $"{SourceMessageCount} 条来源" : string.Empty;

    public bool HasMultipleSourceMessages => SourceMessageCount > 1;

    public TodoReminder? Reminder { get; }
    public TodoDueBucket Bucket { get; }
    public string ProjectName { get; }
    public string ProjectColor { get; }
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
    /// <summary>优先级代码文本，按当前配置层数归一化（超出层数的历史值显示为最低一级）。</summary>
    public string Priority => PrioritySettings.Current.CodeFor(Todo.Priority);

    /// <summary>
    /// 与消息流、桌面日历共用的优先级色板。已办理记录同样按优先级着色，
    /// 不再置灰，避免同一优先级在不同视图出现两种颜色。
    /// </summary>
    public string PriorityColor => PriorityColorPolicy.For(Todo.Priority);

    public string Status => Todo.Status.ToString();
    public string Title => Todo.Title;
    public string DueAt => Todo.DueAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string ReminderAt => Reminder?.ScheduledAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string Source => Todo.SourceMessageId is null
        ? "手动添加"
        : SourceMessage is null ? "来源消息丢失" : FormatSource(SourceMessage.Source);
    public string SourceChatName => Todo.SourceMessageId is null
        ? "无来源消息"
        : SourceMessage?.ChatName ?? "原始消息已不存在";
    public string SenderName => SourceMessage?.SenderName ?? "-";
    public string MessageContent => SourceMessage?.Content ?? Todo.Description ?? Todo.Title;
    public string SentAt => SourceMessage?.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string CompletedAt => Todo.CompletedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-";

    private static string FormatSource(string source) => source switch
    {
        "WeChat" => "微信",
        "Shihuatong" => "石化通",
        "SinopecMail" => "石化邮",
        "Feishu" => "飞书",
        "DingTalk" => "钉钉",
        "Sample" => "示例",
        _ => source
    };
}
