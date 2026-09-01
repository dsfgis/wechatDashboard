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

public sealed class TodoDetailViewModel : ObservableObject
{
    private readonly TodoApplicationService _todoService;
    private readonly IFollowedProjectRepository _projectRepository;
    private readonly ReminderApplicationService _reminderService;
    private readonly IReminderRepository _reminders;
    private readonly IClock _clock;
    private readonly Func<Task> _refreshWorkbench;
    private readonly Func<long, Task> _navigateToMessage;
    private readonly ITodoDialogService _dialogs;
    private TodoItem _todo;
    private TodoReminder? _currentReminder;
    private string _title;
    private string? _description;
    private PriorityLevel _priority;
    private TodoStatus _status;
    private DateTime? _dueDate;
    private int _dueHour = 23;
    private int _dueMinute = 59;
    private DateTime? _reminderDate;
    private int _reminderHour;
    private int _reminderMinute;
    private string _statusText = "";
    private TodoProjectOption _selectedProject = TodoProjectOption.Unassigned;

    public TodoDetailViewModel(
        TodoListItemViewModel row,
        TodoApplicationService todoService,
        IFollowedProjectRepository projectRepository,
        ReminderApplicationService reminderService,
        IReminderRepository reminders,
        IClock clock,
        Func<Task> refreshWorkbench,
        Func<long, Task> navigateToMessage,
        ITodoDialogService dialogs)
    {
        _todo = row.Todo;
        _currentReminder = row.Reminder;
        _todoService = todoService;
        _projectRepository = projectRepository;
        _reminderService = reminderService;
        _reminders = reminders;
        _clock = clock;
        _refreshWorkbench = refreshWorkbench;
        _navigateToMessage = navigateToMessage;
        _dialogs = dialogs;
        _title = _todo.Title;
        _description = _todo.Description;
        _priority = _todo.Priority;
        _status = _todo.Status;
        SetDueSelection(_todo.DueAt);
        var reminderSelection = _currentReminder?.ScheduledAt.LocalDateTime ?? GetDefaultReminderTime(_clock.Now);
        _reminderDate = reminderSelection.Date;
        _reminderHour = reminderSelection.Hour;
        _reminderMinute = reminderSelection.Minute;
        Source = row.Source;
        SourceChatName = row.SourceChatName;
        SenderName = row.SenderName;
        SentAt = row.SentAt;
        OriginalContent = row.SourceMessage?.Content ?? (row.Todo.SourceMessageId is null
            ? row.Todo.Description ?? row.Todo.Title
            : "原始消息已不存在");
        ProjectName = row.ProjectName;
        ProjectColor = row.ProjectColor;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        CompleteCommand = new AsyncRelayCommand(_ => CompleteAsync(), _ => CanComplete);
        ScheduleReminderCommand = new AsyncRelayCommand(_ => ScheduleReminderAsync());
        SnoozeTenMinutesCommand = new AsyncRelayCommand(_ => SnoozeAsync(TimeSpan.FromMinutes(10)));
        SnoozeThirtyMinutesCommand = new AsyncRelayCommand(_ => SnoozeAsync(TimeSpan.FromMinutes(30)));
        SnoozeOneHourCommand = new AsyncRelayCommand(_ => SnoozeAsync(TimeSpan.FromHours(1)));
        SnoozeTomorrowCommand = new AsyncRelayCommand(_ => SnoozeTomorrowAsync());
        NavigateToSourceCommand = new AsyncRelayCommand(_ => NavigateAsync(), _ => SourceMessageId.HasValue);
    }

    public event EventHandler? RequestClose;
    public IReadOnlyList<PriorityLevel> Priorities { get; } = Enum.GetValues<PriorityLevel>();
    public IReadOnlyList<TodoStatus> Statuses { get; } = Enum.GetValues<TodoStatus>();
    public ObservableCollection<TodoProjectOption> ProjectOptions { get; } = new();
    public IReadOnlyList<int> DueHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> DueMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public IReadOnlyList<int> ReminderHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> ReminderMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand ScheduleReminderCommand { get; }
    public AsyncRelayCommand SnoozeTenMinutesCommand { get; }
    public AsyncRelayCommand SnoozeThirtyMinutesCommand { get; }
    public AsyncRelayCommand SnoozeOneHourCommand { get; }
    public AsyncRelayCommand SnoozeTomorrowCommand { get; }
    public AsyncRelayCommand NavigateToSourceCommand { get; }
    public long? SourceMessageId => _todo.SourceMessageId;
    public string Source { get; }
    public string SourceChatName { get; }
    public string SenderName { get; }
    public string SentAt { get; }
    public string OriginalContent { get; }
    public string ProjectName { get; private set; }
    public string ProjectColor { get; private set; }
    public bool IsProjectEditable => Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting;
    public bool CanComplete => Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting;

    public TodoProjectOption SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value);
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public PriorityLevel Priority
    {
        get => _priority;
        set => SetProperty(ref _priority, value);
    }

    public TodoStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsProjectEditable));
                OnPropertyChanged(nameof(CanComplete));
                CompleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DateTime? DueDate
    {
        get => _dueDate;
        set => SetProperty(ref _dueDate, value);
    }

    public int DueHour
    {
        get => _dueHour;
        set => SetProperty(ref _dueHour, value);
    }

    public int DueMinute
    {
        get => _dueMinute;
        set => SetProperty(ref _dueMinute, value);
    }

    public DateTime? ReminderDate
    {
        get => _reminderDate;
        set => SetProperty(ref _reminderDate, value);
    }

    public int ReminderHour
    {
        get => _reminderHour;
        set => SetProperty(ref _reminderHour, value);
    }

    public int ReminderMinute
    {
        get => _reminderMinute;
        set => SetProperty(ref _reminderMinute, value);
    }

    public string CurrentReminderText => _currentReminder is null
        ? "未设置提醒"
        : $"{_currentReminder.ScheduledAt.LocalDateTime:yyyy-MM-dd HH:mm}（{_currentReminder.Status}）";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public async Task LoadProjectsAsync()
    {
        try
        {
            var projects = await _projectRepository.GetAllAsync(CancellationToken.None);
            ProjectOptions.Clear();
            ProjectOptions.Add(TodoProjectOption.Unassigned);
            foreach (var project in projects.Where(item => item.IsActive))
            {
                ProjectOptions.Add(new TodoProjectOption(project.Id, project.ProjectName, project.Color));
            }

            SelectedProject = ProjectOptions.FirstOrDefault(item => item.Id == _todo.ManagedProjectId) ?? TodoProjectOption.Unassigned;
        }
        catch (Exception)
        {
            ProjectOptions.Clear();
            ProjectOptions.Add(new TodoProjectOption(_todo.ManagedProjectId, ProjectName, ProjectColor));
            SelectedProject = ProjectOptions[0];
            _dialogs.ShowError("项目加载失败，请稍后重新打开详情。", "加载失败");
        }
    }

    private async Task SaveAsync()
    {
        if (!TryGetOptionalDueTime(out var dueAt))
        {
            _dialogs.ShowError("请选择有效的截止日期和时间，或清空日期。", "保存失败");
            return;
        }

        var updated = await _todoService.UpdateAsync(
            new UpdateTodoRequest(
                _todo.Id,
                Title,
                Description,
                _todo.ProjectId,
                Priority,
                dueAt,
                Status,
                _todo.UpdatedAt,
                GroupId: null,
                ManagedProjectId: SelectedProject.Id),
            CancellationToken.None);
        if (updated is null)
        {
            await LoadProjectsAsync();
            _dialogs.ShowError("待办已被其他操作修改，或所选项目已不存在。请重新选择后重试。", "保存冲突");
            return;
        }

        _todo = updated;
        ProjectName = SelectedProject.Name;
        ProjectColor = SelectedProject.Color;
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectColor));
        SetDueSelection(updated.DueAt);
        StatusText = "待办已保存。";
        await _refreshWorkbench();
    }

    public async Task CompleteAsync()
    {
        if (!CanComplete)
        {
            return;
        }

        var updated = await _todoService.UpdateAsync(
            new UpdateTodoRequest(
                _todo.Id,
                _todo.Title,
                _todo.Description,
                _todo.ProjectId,
                _todo.Priority,
                _todo.DueAt,
                TodoStatus.Done,
                _todo.UpdatedAt,
                GroupId: null,
                ManagedProjectId: _todo.ManagedProjectId),
            CancellationToken.None);
        if (updated is null)
        {
            _dialogs.ShowError("待办已被其他操作修改，请关闭详情并刷新后重试。", "完成失败");
            return;
        }

        _todo = updated;
        Status = updated.Status;
        StatusText = "待办已完成并转入已办。";
        await _refreshWorkbench();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private async Task ScheduleReminderAsync()
    {
        if (!TryGetReminderTime(out var reminderAt) || reminderAt <= _clock.Now)
        {
            _dialogs.ShowError("请选择晚于当前时间的有效提醒日期和时间。", "设置提醒失败");
            return;
        }

        _currentReminder = await _reminderService.ScheduleAsync(_todo.Id, reminderAt, CancellationToken.None);
        if (_currentReminder is null)
        {
            _dialogs.ShowError("已完成或已忽略的待办不能设置提醒。", "设置提醒失败");
            return;
        }

        SetReminderSelection(_currentReminder.ScheduledAt);
        StatusText = "提醒已设置。";
        OnPropertyChanged(nameof(CurrentReminderText));
        await _refreshWorkbench();
    }

    private async Task SnoozeAsync(TimeSpan delay)
    {
        await SnoozeUntilAsync(_clock.Now.Add(delay));
    }

    private async Task SnoozeTomorrowAsync()
    {
        var localTomorrow = _clock.Now.LocalDateTime.Date.AddDays(1).AddHours(9);
        await SnoozeUntilAsync(new DateTimeOffset(localTomorrow, TimeZoneInfo.Local.GetUtcOffset(localTomorrow)));
    }

    private async Task SnoozeUntilAsync(DateTimeOffset target)
    {
        await ReloadCurrentReminderAsync();
        if (_currentReminder is null)
        {
            _dialogs.ShowError("当前没有可延后的提醒，请先设置提醒。", "延后失败");
            return;
        }

        var result = await _reminderService.SnoozeAsync(_currentReminder.Id, target, CancellationToken.None);
        if (!result.Succeeded || result.Reminder is null)
        {
            _dialogs.ShowError(result.Error ?? "提醒状态已变化，请重试。", "延后失败");
            return;
        }

        _currentReminder = result.Reminder;
        SetReminderSelection(result.Reminder.ScheduledAt);
        StatusText = $"提醒已延后到 {result.Reminder.ScheduledAt.LocalDateTime:yyyy-MM-dd HH:mm}，截止时间未改变。";
        OnPropertyChanged(nameof(CurrentReminderText));
        await _refreshWorkbench();
    }

    private async Task ReloadCurrentReminderAsync()
    {
        var reminders = await _reminders.GetForTodoAsync(_todo.Id, CancellationToken.None);
        _currentReminder = reminders.LastOrDefault(item => item.Status is ReminderStatus.Scheduled or ReminderStatus.Dispatching or ReminderStatus.Delivered);
    }

    private async Task NavigateAsync()
    {
        if (SourceMessageId is not { } messageId)
        {
            return;
        }

        await _navigateToMessage(messageId);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private bool TryGetOptionalDueTime(out DateTimeOffset? result)
    {
        if (DueDate is not { } date)
        {
            result = null;
            return true;
        }

        if (DueHour is < 0 or > 23 || DueMinute is < 0 or > 59)
        {
            result = null;
            return false;
        }

        var localTime = DateTime.SpecifyKind(
            date.Date.AddHours(DueHour).AddMinutes(DueMinute),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localTime))
        {
            result = null;
            return false;
        }

        result = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        return true;
    }

    private bool TryGetReminderTime(out DateTimeOffset result)
    {
        if (ReminderDate is not { } date ||
            ReminderHour is < 0 or > 23 ||
            ReminderMinute is < 0 or > 59)
        {
            result = default;
            return false;
        }

        var localTime = DateTime.SpecifyKind(
            date.Date.AddHours(ReminderHour).AddMinutes(ReminderMinute),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localTime))
        {
            result = default;
            return false;
        }

        result = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        return true;
    }

    private void SetReminderSelection(DateTimeOffset value)
    {
        var localTime = value.LocalDateTime;
        ReminderDate = localTime.Date;
        ReminderHour = localTime.Hour;
        ReminderMinute = localTime.Minute;
    }

    private void SetDueSelection(DateTimeOffset? value)
    {
        if (value is not { } dueAt)
        {
            DueDate = null;
            DueHour = 23;
            DueMinute = 59;
            return;
        }

        var localTime = dueAt.LocalDateTime;
        DueDate = localTime.Date;
        DueHour = localTime.Hour;
        DueMinute = localTime.Minute;
    }

    private static DateTime GetDefaultReminderTime(DateTimeOffset now)
    {
        var localTime = now.LocalDateTime.AddMinutes(5);
        return new DateTime(
            localTime.Year,
            localTime.Month,
            localTime.Day,
            localTime.Hour,
            localTime.Minute / 5 * 5,
            0,
            DateTimeKind.Unspecified);
    }

}
