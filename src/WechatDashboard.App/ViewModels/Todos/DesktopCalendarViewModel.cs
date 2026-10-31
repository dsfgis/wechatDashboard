using System.Collections.ObjectModel;
using System.Globalization;
using WechatDashboard.App.Presentation;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

public sealed class DesktopCalendarViewModel : ObservableObject
{
    private static readonly CultureInfo ChineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    private readonly ITodoRepository _todos;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly Func<long, Task> _openTodo;
    private readonly Func<Task> _onTodoCompleted;
    private readonly IFollowedProjectRepository? _projects;
    private readonly IMessageRepository? _messages;
    private IReadOnlyDictionary<DateTime, IReadOnlyList<DesktopCalendarTodoItemViewModel>> _todosByDate =
        new Dictionary<DateTime, IReadOnlyList<DesktopCalendarTodoItemViewModel>>();
    private DateTime _displayMonth;
    private DateTime _selectedDate;
    private string _statusText = "正在加载日历待办...";

    public DesktopCalendarViewModel(
        ITodoRepository todos,
        IClock clock,
        TimeZoneInfo timeZone,
        Func<long, Task> openTodo,
        Func<Task>? onTodoCompleted = null,
        IFollowedProjectRepository? projects = null,
        IMessageRepository? messages = null)
    {
        _todos = todos;
        _clock = clock;
        _timeZone = timeZone;
        _openTodo = openTodo;
        _onTodoCompleted = onTodoCompleted ?? (() => Task.CompletedTask);
        _projects = projects;
        _messages = messages;
        var today = LocalDate(clock.Now);
        _displayMonth = new DateTime(today.Year, today.Month, 1);
        _selectedDate = today;
        PreviousMonthCommand = new RelayCommand(_ => MoveMonth(-1));
        NextMonthCommand = new RelayCommand(_ => MoveMonth(1));
        TodayCommand = new RelayCommand(_ => GoToToday());
        SelectDateCommand = new RelayCommand(SelectDate);
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        OpenTodoCommand = new AsyncRelayCommand(OpenTodoAsync, parameter => parameter is long id && id > 0);
        CompleteTodoCommand = new AsyncRelayCommand(
            CompleteTodoAsync,
            parameter => parameter is DesktopCalendarTodoItemViewModel { IsCompleted: false });
        BuildCalendarDays();
    }

    public ObservableCollection<DesktopCalendarDayViewModel> Days { get; } = new();
    public ObservableCollection<DesktopCalendarTodoItemViewModel> SelectedTodos { get; } = new();
    public RelayCommand PreviousMonthCommand { get; }
    public RelayCommand NextMonthCommand { get; }
    public RelayCommand TodayCommand { get; }
    public RelayCommand SelectDateCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand OpenTodoCommand { get; }
    public AsyncRelayCommand CompleteTodoCommand { get; }

    public DateTime DisplayMonth => _displayMonth;
    public DateTime SelectedDate => _selectedDate;
    public string MonthTitle => _displayMonth.ToString("yyyy年M月", ChineseCulture);
    public string SelectedDateTitle => _selectedDate.ToString("yyyy年M月d日 dddd", ChineseCulture);
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task RefreshAsync()
    {
        var snapshot = await Task.Run(async () =>
        {
            var activeTask = _todos.GetActiveAsync(CancellationToken.None);
            var completedTask = _todos.GetCompletedAsync(CancellationToken.None);
            var projectsTask = _projects?.GetAllAsync(CancellationToken.None)
                ?? Task.FromResult<IReadOnlyList<FollowedProject>>(Array.Empty<FollowedProject>());
            await Task.WhenAll(activeTask, completedTask, projectsTask);
            var allItems = activeTask.Result.Concat(completedTask.Result).ToArray();
            var projectLookup = projectsTask.Result.Where(item => item.IsActive).ToDictionary(item => item.Id);
            // 展示时间取消息发送时间（SentAt），而不是待办的到期时间；
            // 手动创建、无来源消息的待办才回退到 DueAt。
            var sentAtByMessageId = await LoadSentAtLookupAsync(allItems);
            var byDate = allItems
                .Where(todo => todo.DueAt.HasValue)
                .Select(todo => new
                {
                    Date = LocalDate(todo.DueAt!.Value),
                    Item = CreateTodoItem(todo, projectLookup, sentAtByMessageId)
                })
                .GroupBy(entry => entry.Date)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<DesktopCalendarTodoItemViewModel>)group
                        .Select(entry => entry.Item)
                        // 排序契约：第一关键字是“是否完成”（未完成在前、已办在后），
                        // 第二关键字是时间。日期格子里的两行预览（PreviewTodos 只取前两条）
                        // 因此始终优先展示当天还没办的事项；已办事项靠后但仍保留在
                        // “当日日程”列表中，不会从日历消失。
                        // 时间取展示时间（DisplayAt：有来源消息时为消息发送时间，
                        // 否则回退到截止时间），保证右栏时间列自上而下单调递增；
                        // DueAt 与 Id 仅用于同一时刻的稳定排序，优先级不再参与排序。
                        .OrderBy(item => item.IsCompleted)
                        .ThenBy(item => item.DisplayAt)
                        .ThenBy(item => item.DueAt)
                        .ThenBy(item => item.Id)
                        .ToArray());
            return (AllCount: allItems.Length, ByDate: byDate);
        });
        _todosByDate = snapshot.ByDate;

        BuildCalendarDays();
        var scheduledCount = _todosByDate.Values.Sum(items => items.Count);
        var unscheduledCount = snapshot.AllCount - scheduledCount;
        StatusText = unscheduledCount == 0
            ? $"共加载 {scheduledCount} 条有截止日期的待办。"
            : $"共加载 {scheduledCount} 条日历待办，另有 {unscheduledCount} 条未设置截止日期。";
    }

    public void MoveMonth(int monthDelta)
    {
        var targetMonth = _displayMonth.AddMonths(monthDelta);
        _displayMonth = new DateTime(targetMonth.Year, targetMonth.Month, 1);
        _selectedDate = new DateTime(
            _displayMonth.Year,
            _displayMonth.Month,
            Math.Min(_selectedDate.Day, DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month)));
        NotifyCalendarHeaderChanged();
        BuildCalendarDays();
    }

    public void SelectDate(DateTime date)
    {
        _selectedDate = date.Date;
        if (_selectedDate.Year != _displayMonth.Year || _selectedDate.Month != _displayMonth.Month)
        {
            _displayMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);
        }

        NotifyCalendarHeaderChanged();
        BuildCalendarDays();
    }

    public async Task OpenTodoAsync(long todoId)
    {
        await _openTodo(todoId);
        await RefreshAsync();
    }

    public async Task CompleteTodoAsync(long todoId)
    {
        if (todoId <= 0)
        {
            return;
        }

        var updated = await _todos.MarkSelectedCompletedAsync(
            new[] { todoId },
            _clock.Now,
            CancellationToken.None);
        await Task.WhenAll(RefreshAsync(), _onTodoCompleted());
        StatusText = updated == 1
            ? "待办已完成。"
            : "待办已被办理或不存在，日历已刷新。";
    }

    private void SelectDate(object? parameter)
    {
        if (parameter is DateTime date)
        {
            SelectDate(date);
        }
    }

    private async Task OpenTodoAsync(object? parameter)
    {
        if (parameter is long todoId)
        {
            await OpenTodoAsync(todoId);
        }
    }

    private async Task CompleteTodoAsync(object? parameter)
    {
        if (parameter is DesktopCalendarTodoItemViewModel todo)
        {
            await CompleteTodoAsync(todo.Id);
        }
    }

    private void GoToToday()
    {
        var today = LocalDate(_clock.Now);
        _displayMonth = new DateTime(today.Year, today.Month, 1);
        _selectedDate = today;
        NotifyCalendarHeaderChanged();
        BuildCalendarDays();
    }

    private void BuildCalendarDays()
    {
        var firstDay = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        var mondayOffset = ((int)firstDay.DayOfWeek + 6) % 7;
        var gridStart = firstDay.AddDays(-mondayOffset);
        var today = LocalDate(_clock.Now);
        Days.Clear();
        for (var index = 0; index < 42; index++)
        {
            var date = gridStart.AddDays(index);
            var todos = _todosByDate.GetValueOrDefault(date) ?? Array.Empty<DesktopCalendarTodoItemViewModel>();
            Days.Add(new DesktopCalendarDayViewModel(
                date,
                date.Month == _displayMonth.Month,
                date == today,
                date == _selectedDate,
                todos));
        }

        SelectedTodos.Clear();
        foreach (var todo in _todosByDate.GetValueOrDefault(_selectedDate) ?? Array.Empty<DesktopCalendarTodoItemViewModel>())
        {
            SelectedTodos.Add(todo);
        }

        OnPropertyChanged(nameof(SelectedDateTitle));
    }

    private void NotifyCalendarHeaderChanged()
    {
        OnPropertyChanged(nameof(DisplayMonth));
        OnPropertyChanged(nameof(SelectedDate));
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(SelectedDateTitle));
    }

    private DateTime LocalDate(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _timeZone).Date;

    private async Task<IReadOnlyDictionary<long, DateTimeOffset>> LoadSentAtLookupAsync(
        IReadOnlyCollection<TodoItem> todoItems)
    {
        if (_messages is null)
        {
            return new Dictionary<long, DateTimeOffset>();
        }

        var messageIds = todoItems
            .Where(todo => todo.SourceMessageId is > 0)
            .Select(todo => todo.SourceMessageId!.Value)
            .Distinct()
            .ToArray();
        if (messageIds.Length == 0)
        {
            return new Dictionary<long, DateTimeOffset>();
        }

        var messages = await _messages.GetByIdsAsync(messageIds, CancellationToken.None);
        return messages.ToDictionary(message => message.Id, message => message.SentAt);
    }

    private DesktopCalendarTodoItemViewModel CreateTodoItem(
        TodoItem todo,
        IReadOnlyDictionary<long, FollowedProject> projectLookup,
        IReadOnlyDictionary<long, DateTimeOffset>? sentAtLookup = null)
    {
        var localDueAt = TimeZoneInfo.ConvertTime(todo.DueAt!.Value, _timeZone);
        var displayAt = todo.SourceMessageId is { } messageId
            && sentAtLookup is not null
            && sentAtLookup.TryGetValue(messageId, out var sentAt)
            ? TimeZoneInfo.ConvertTime(sentAt, _timeZone)
            : localDueAt;
        var project = todo.ManagedProjectId is { } projectId && projectLookup.TryGetValue(projectId, out var resolved)
            ? resolved
            : null;
        return new DesktopCalendarTodoItemViewModel(
            todo.Id,
            todo.Title,
            todo.Priority,
            localDueAt,
            displayAt,
            todo.Status is TodoStatus.Done or TodoStatus.Ignored,
            project?.ProjectName ?? "未指定项目",
            project?.Color ?? ProjectColorPolicy.DefaultColor);
    }
}

public sealed record DesktopCalendarTodoItemViewModel(
    long Id,
    string Title,
    PriorityLevel Priority,
    DateTimeOffset DueAt,
    DateTimeOffset DisplayAt,
    bool IsCompleted,
    string ProjectName,
    string ProjectColor)
{
    /// <summary>优先展示消息发送时间；无来源消息时回退到到期时间。</summary>
    public string TimeText => DisplayAt.ToString("HH:mm");

    public string StatusText => IsCompleted ? "已办理" : PrioritySettings.Current.CodeFor(Priority);

    /// <summary>
    /// 与消息流一致的优先级色板。已办理项同样按优先级着色（仅额外加删除线），
    /// 因此这里始终返回该待办的真实优先级颜色。
    /// </summary>
    public string PriorityColor => PriorityColorPolicy.For(Priority);
}

public sealed class DesktopCalendarDayViewModel
{
    public DesktopCalendarDayViewModel(
        DateTime date,
        bool isCurrentMonth,
        bool isToday,
        bool isSelected,
        IReadOnlyList<DesktopCalendarTodoItemViewModel> todos)
    {
        Date = date;
        IsCurrentMonth = isCurrentMonth;
        IsToday = isToday;
        IsSelected = isSelected;
        Todos = todos;
        PreviewTodos = todos.Take(2).ToArray();
    }

    public DateTime Date { get; }
    public int DayNumber => Date.Day;
    public bool IsCurrentMonth { get; }
    public bool IsToday { get; }
    public bool IsSelected { get; }
    public IReadOnlyList<DesktopCalendarTodoItemViewModel> Todos { get; }
    public IReadOnlyList<DesktopCalendarTodoItemViewModel> PreviewTodos { get; }
    public int TodoCount => Todos.Count;
    public int MoreCount => Math.Max(0, Todos.Count - PreviewTodos.Count);
}
