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
        IFollowedProjectRepository? projects = null)
    {
        _todos = todos;
        _clock = clock;
        _timeZone = timeZone;
        _openTodo = openTodo;
        _onTodoCompleted = onTodoCompleted ?? (() => Task.CompletedTask);
        _projects = projects;
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
            var byDate = allItems
                .Where(todo => todo.DueAt.HasValue)
                .Select(todo => new
                {
                    Date = LocalDate(todo.DueAt!.Value),
                    Item = CreateTodoItem(todo, projectLookup)
                })
                .GroupBy(entry => entry.Date)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<DesktopCalendarTodoItemViewModel>)group
                        .Select(entry => entry.Item)
                        .OrderBy(item => item.IsCompleted)
                        .ThenBy(item => item.Priority)
                        .ThenBy(item => item.DueAt)
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
            ? "待办已完成并转入已办。"
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

    private DesktopCalendarTodoItemViewModel CreateTodoItem(
        TodoItem todo,
        IReadOnlyDictionary<long, FollowedProject> projectLookup)
    {
        var localDueAt = TimeZoneInfo.ConvertTime(todo.DueAt!.Value, _timeZone);
        var project = todo.ManagedProjectId is { } projectId && projectLookup.TryGetValue(projectId, out var resolved)
            ? resolved
            : null;
        return new DesktopCalendarTodoItemViewModel(
            todo.Id,
            todo.Title,
            todo.Priority,
            localDueAt,
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
    bool IsCompleted,
    string ProjectName,
    string ProjectColor)
{
    public string TimeText => DueAt.ToString("HH:mm");
    public string StatusText => IsCompleted ? "已办理" : Priority.ToString();
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
