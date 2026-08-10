using WechatDashboard.App.Presentation;
using WechatDashboard.App.Services;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

public sealed class TodoDetailViewModel : ObservableObject
{
    private readonly TodoApplicationService _todoService;
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
    private string _dueAtText;
    private DateTime? _reminderDate;
    private int _reminderHour;
    private int _reminderMinute;
    private string _statusText = "";

    public TodoDetailViewModel(
        TodoListItemViewModel row,
        TodoApplicationService todoService,
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
        _dueAtText = FormatTime(_todo.DueAt);
        var reminderSelection = _currentReminder?.ScheduledAt.LocalDateTime ?? GetDefaultReminderTime(_clock.Now);
        _reminderDate = reminderSelection.Date;
        _reminderHour = reminderSelection.Hour;
        _reminderMinute = reminderSelection.Minute;
        Source = row.Source;
        SourceChatName = row.SourceChatName;
        SenderName = row.SenderName;
        SentAt = row.SentAt;
        OriginalContent = row.SourceMessage?.Content ?? "原始消息已不存在";
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
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
    public IReadOnlyList<int> ReminderHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> ReminderMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public AsyncRelayCommand SaveCommand { get; }
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
        set => SetProperty(ref _status, value);
    }

    public string DueAtText
    {
        get => _dueAtText;
        set => SetProperty(ref _dueAtText, value);
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

    private async Task SaveAsync()
    {
        if (!TryParseOptionalTime(DueAtText, out var dueAt))
        {
            _dialogs.ShowError("截止时间格式应为 yyyy-MM-dd HH:mm，或留空。", "保存失败");
            return;
        }

        var updated = await _todoService.UpdateAsync(
            new UpdateTodoRequest(_todo.Id, Title, Description, _todo.ProjectId, Priority, dueAt, Status, _todo.UpdatedAt),
            CancellationToken.None);
        if (updated is null)
        {
            _dialogs.ShowError("待办已被其他操作修改，请关闭详情并重新打开。", "保存冲突");
            return;
        }

        _todo = updated;
        DueAtText = FormatTime(updated.DueAt);
        StatusText = "待办已保存。";
        await _refreshWorkbench();
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

    private static bool TryParseOptionalTime(string? text, out DateTimeOffset? result)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            result = null;
            return true;
        }

        if (DateTimeOffset.TryParse(text, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
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

    private static string FormatTime(DateTimeOffset? value) => value?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
}
