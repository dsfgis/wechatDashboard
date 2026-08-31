using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.App.Services;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

public sealed record TodoGroupOption(long? Id, string Name, string Color)
{
    public static TodoGroupOption Ungrouped { get; } = new(null, "未分组", TodoGroupColorPolicy.DefaultColor);
}

public sealed record TodoGroupColorOption(string Name, string Color);

public sealed class TodoCreateViewModel : ObservableObject
{
    private readonly TodoApplicationService _todos;
    private readonly TodoGroupApplicationService _groups;
    private readonly IClock _clock;
    private readonly ITodoDialogService _dialogs;
    private readonly Func<Task> _onCreated;
    private string _title = "";
    private string? _description;
    private PriorityLevel _priority = PriorityLevel.P2;
    private DateTime? _dueDate;
    private int _dueHour = 23;
    private int _dueMinute = 59;
    private bool _hasReminder;
    private DateTime? _reminderDate;
    private int _reminderHour;
    private int _reminderMinute;
    private TodoGroupOption _selectedGroup = TodoGroupOption.Ungrouped;
    private TodoGroupColorOption _selectedNewGroupColor;
    private string _newGroupName = "";
    private string _statusText = "";
    private bool _isSaved;

    public TodoCreateViewModel(
        TodoApplicationService todos,
        TodoGroupApplicationService groups,
        IClock clock,
        ITodoDialogService dialogs,
        Func<Task> onCreated)
    {
        _todos = todos;
        _groups = groups;
        _clock = clock;
        _dialogs = dialogs;
        _onCreated = onCreated;
        NewGroupColors =
        [
            new("灰色", TodoGroupColorPolicy.DefaultColor),
            new("红色", TodoGroupColorPolicy.Red),
            new("橙色", TodoGroupColorPolicy.Orange),
            new("绿色", TodoGroupColorPolicy.Green),
            new("蓝色", TodoGroupColorPolicy.Blue),
            new("紫色", TodoGroupColorPolicy.Purple),
            new("粉色", TodoGroupColorPolicy.Pink)
        ];
        _selectedNewGroupColor = NewGroupColors[0];
        var reminder = _clock.Now.LocalDateTime.AddMinutes(5);
        _reminderDate = reminder.Date;
        _reminderHour = reminder.Hour;
        _reminderMinute = reminder.Minute;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => !_isSaved);
        AddGroupCommand = new AsyncRelayCommand(_ => AddGroupAsync());
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RequestClose;
    public ObservableCollection<TodoGroupOption> GroupOptions { get; } = new();
    public IReadOnlyList<PriorityLevel> Priorities { get; } = Enum.GetValues<PriorityLevel>();
    public IReadOnlyList<TodoGroupColorOption> NewGroupColors { get; }
    public IReadOnlyList<int> DueHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> DueMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public IReadOnlyList<int> ReminderHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> ReminderMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand AddGroupCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string? Description { get => _description; set => SetProperty(ref _description, value); }
    public PriorityLevel Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public DateTime? DueDate { get => _dueDate; set => SetProperty(ref _dueDate, value); }
    public int DueHour { get => _dueHour; set => SetProperty(ref _dueHour, value); }
    public int DueMinute { get => _dueMinute; set => SetProperty(ref _dueMinute, value); }
    public bool HasReminder { get => _hasReminder; set => SetProperty(ref _hasReminder, value); }
    public DateTime? ReminderDate { get => _reminderDate; set => SetProperty(ref _reminderDate, value); }
    public int ReminderHour { get => _reminderHour; set => SetProperty(ref _reminderHour, value); }
    public int ReminderMinute { get => _reminderMinute; set => SetProperty(ref _reminderMinute, value); }
    public TodoGroupOption SelectedGroup { get => _selectedGroup; set => SetProperty(ref _selectedGroup, value); }
    public TodoGroupColorOption SelectedNewGroupColor { get => _selectedNewGroupColor; set => SetProperty(ref _selectedNewGroupColor, value); }
    public string NewGroupName { get => _newGroupName; set => SetProperty(ref _newGroupName, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public async Task LoadGroupsAsync()
    {
        try
        {
            var selectedId = SelectedGroup.Id;
            var groups = await _groups.GetAllAsync(CancellationToken.None);
            GroupOptions.Clear();
            GroupOptions.Add(TodoGroupOption.Ungrouped);
            foreach (var group in groups)
            {
                GroupOptions.Add(new TodoGroupOption(group.Id, group.Name, group.Color));
            }

            SelectedGroup = GroupOptions.FirstOrDefault(item => item.Id == selectedId) ?? TodoGroupOption.Ungrouped;
            StatusText = "";
        }
        catch (Exception)
        {
            GroupOptions.Clear();
            GroupOptions.Add(TodoGroupOption.Ungrouped);
            SelectedGroup = TodoGroupOption.Ungrouped;
            StatusText = "分组加载失败，请稍后重试；这不是“当前没有分组”。";
        }
    }

    private async Task AddGroupAsync()
    {
        try
        {
            var result = await _groups.CreateAsync(NewGroupName, SelectedNewGroupColor.Color, CancellationToken.None);
            await LoadGroupsAsync();
            SelectedGroup = GroupOptions.First(item => item.Id == result.Group.Id);
            NewGroupName = "";
            StatusText = result.Outcome == CreateTodoGroupOutcome.Created
                ? $"已新增分组“{result.Group.Name}”。"
                : $"分组“{result.Group.Name}”已存在，已为你选中。";
        }
        catch (ArgumentException exception)
        {
            StatusText = exception.Message;
        }
        catch (Exception)
        {
            StatusText = "新增分组失败，请稍后重试。";
        }
    }

    private async Task SaveAsync()
    {
        if (!TryGetOptionalDueTime(out var dueAt))
        {
            _dialogs.ShowError("请选择有效的截止日期和时间，或清空日期。", "创建失败");
            return;
        }

        DateTimeOffset? reminderAt = null;
        if (HasReminder)
        {
            if (!TryGetReminderTime(out var parsedReminder) || parsedReminder <= _clock.Now)
            {
                _dialogs.ShowError("请选择晚于当前时间的有效提醒日期和时间。", "创建失败");
                return;
            }

            reminderAt = parsedReminder;
        }

        try
        {
            var result = await _todos.CreateManualAsync(
                new CreateManualTodoRequest(
                    Title,
                    Description,
                    Priority,
                    dueAt,
                    reminderAt,
                    SelectedGroup.Id),
                CancellationToken.None);
            if (result.Outcome == CreateManualTodoOutcome.GroupMissing)
            {
                await LoadGroupsAsync();
                StatusText = "所选分组已不存在，请重新选择后保存。";
                return;
            }

            _isSaved = true;
            SaveCommand.RaiseCanExecuteChanged();
            await _onCreated();
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (ArgumentException exception)
        {
            StatusText = exception.Message.Contains("title", StringComparison.OrdinalIgnoreCase)
                ? "标题不能为空。"
                : exception.Message;
        }
        catch (Exception)
        {
            StatusText = "待办保存失败，已保留当前输入，请稍后重试。";
        }
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
        if (ReminderDate is not { } date || ReminderHour is < 0 or > 23 || ReminderMinute is < 0 or > 59)
        {
            result = default;
            return false;
        }

        var localTime = DateTime.SpecifyKind(date.Date.AddHours(ReminderHour).AddMinutes(ReminderMinute), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localTime))
        {
            result = default;
            return false;
        }

        result = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
        return true;
    }
}
