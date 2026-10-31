using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.App.Services;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

public sealed record TodoProjectOption(long? Id, string Name, string Color)
{
    public static TodoProjectOption Unassigned { get; } = new(null, "未指定项目", ProjectColorPolicy.DefaultColor);
}

public sealed record ProjectColorOption(string Name, string Color);

public sealed class TodoCreateViewModel : ObservableObject
{
    private readonly TodoApplicationService _todos;
    private readonly IFollowedProjectRepository _projects;
    private readonly IClock _clock;
    private readonly ITodoDialogService _dialogs;
    private readonly Func<Task> _onCreated;
    private string _title = "";
    private string? _description;
    // 默认中优先级；用户把层数改到 2 级时按当前最低一级收敛，避免下拉出现不存在的选项。
    private PriorityLevel _priority = PrioritySettings.Current.Normalize(PriorityLevel.P2);
    private DateTime? _dueDate;
    private int _dueHour = 23;
    private int _dueMinute = 59;
    private bool _hasReminder;
    private DateTime? _reminderDate;
    private int _reminderHour;
    private int _reminderMinute;
    private TodoProjectOption _selectedProject = TodoProjectOption.Unassigned;
    private string _statusText = "";
    private bool _isSaved;

    public TodoCreateViewModel(
        TodoApplicationService todos,
        IFollowedProjectRepository projects,
        IClock clock,
        ITodoDialogService dialogs,
        Func<Task> onCreated)
    {
        _todos = todos;
        _projects = projects;
        _clock = clock;
        _dialogs = dialogs;
        _onCreated = onCreated;
        var reminder = _clock.Now.LocalDateTime.AddMinutes(5);
        _reminderDate = reminder.Date;
        _reminderHour = reminder.Hour;
        _reminderMinute = reminder.Minute;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => !_isSaved);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RequestClose;
    public ObservableCollection<TodoProjectOption> ProjectOptions { get; } = new();
    /// <summary>可选优先级，数量与颜色跟随“优先级设置”。</summary>
    public IReadOnlyList<PriorityOption> PriorityOptions { get; } = PriorityOption.FromCurrentPalette();
    public IReadOnlyList<int> DueHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> DueMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public IReadOnlyList<int> ReminderHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> ReminderMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public AsyncRelayCommand SaveCommand { get; }
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
    public TodoProjectOption SelectedProject { get => _selectedProject; set => SetProperty(ref _selectedProject, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public async Task LoadProjectsAsync()
    {
        try
        {
            var selectedId = SelectedProject.Id;
            var projects = await _projects.GetAllAsync(CancellationToken.None);
            ProjectOptions.Clear();
            ProjectOptions.Add(TodoProjectOption.Unassigned);
            foreach (var project in projects)
            {
                ProjectOptions.Add(new TodoProjectOption(project.Id, project.ProjectName, project.Color));
            }

            SelectedProject = ProjectOptions.FirstOrDefault(item => item.Id == selectedId) ?? TodoProjectOption.Unassigned;
            StatusText = "";
        }
        catch (Exception)
        {
            ProjectOptions.Clear();
            ProjectOptions.Add(TodoProjectOption.Unassigned);
            SelectedProject = TodoProjectOption.Unassigned;
            StatusText = "项目加载失败，请稍后重试；这不是“当前没有项目”。";
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
                    GroupId: null,
                    ManagedProjectId: SelectedProject.Id),
                CancellationToken.None);
            if (result.Outcome == CreateManualTodoOutcome.ProjectMissing)
            {
                await LoadProjectsAsync();
                StatusText = "所选项目已不存在，请重新选择后保存。";
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

        var localTime = DateTime.SpecifyKind(date.Date.AddHours(DueHour).AddMinutes(DueMinute), DateTimeKind.Unspecified);
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
