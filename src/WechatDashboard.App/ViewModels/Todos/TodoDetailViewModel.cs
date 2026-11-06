using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.App.Services;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.TodoRules;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

public sealed class TodoDetailViewModel : ObservableObject
{
    private readonly TodoApplicationService _todoService;
    private readonly ITodoRepository _todos;
    private readonly IFollowedProjectRepository _projectRepository;
    private readonly ReminderApplicationService _reminderService;
    private readonly IReminderRepository _reminders;
    private readonly IClock _clock;
    private readonly Func<Task> _refreshWorkbench;
    private readonly Func<long, Task> _navigateToMessage;
    private readonly ITodoDialogService _dialogs;
    // 规则写入后立即刷新共享快照，采集管线无需重启即可生效；测试可不接入。
    private readonly TodoRuleStore? _todoRuleStore;
    private string _todoRuleKeyword = string.Empty;
    private string? _todoRuleStatusText;
    private TodoItem _todo;
    private TodoReminder? _currentReminder;
    private string _title;
    private string? _description;
    private PriorityLevel _priority;
    // 记录打开时的原始层级：超出当前配置层数的旧待办显示为最低一级，
    // 但只要用户没有真的改过下拉，保存时仍写回原值，不因设置变化悄悄改数据。
    private PriorityLevel _originalPriority;
    private TodoStatus _status;
    // 用户在下拉里改了状态但还没点“保存待办”：窗口关闭时只补交状态，不误提交其它编辑中字段。
    private bool _hasUnsavedStatusChange;
    private DateTime? _dueDate;
    private int _dueHour = 23;
    private int _dueMinute = 59;
    private DateTime? _reminderDate;
    private int _reminderHour;
    private int _reminderMinute;
    private string _statusText = "";
    private TodoProjectOption _selectedProject = TodoProjectOption.Unassigned;
    private bool _isPinned;

    public TodoDetailViewModel(
        TodoListItemViewModel row,
        TodoApplicationService todoService,
        ITodoRepository todos,
        IFollowedProjectRepository projectRepository,
        ReminderApplicationService reminderService,
        IReminderRepository reminders,
        IClock clock,
        Func<Task> refreshWorkbench,
        Func<long, Task> navigateToMessage,
        ITodoDialogService dialogs,
        TodoRuleStore? todoRuleStore = null)
    {
        _todo = row.Todo;
        _currentReminder = row.Reminder;
        _todoService = todoService;
        _todos = todos;
        _projectRepository = projectRepository;
        _reminderService = reminderService;
        _reminders = reminders;
        _clock = clock;
        _refreshWorkbench = refreshWorkbench;
        _navigateToMessage = navigateToMessage;
        _dialogs = dialogs;
        _todoRuleStore = todoRuleStore;
        _title = _todo.Title;
        _description = _todo.Description;
        _originalPriority = _todo.Priority;
        _priority = PrioritySettings.Current.Normalize(_todo.Priority);
        _status = _todo.Status;
        // 状态下拉随打开时的真实状态生成；历史“已忽略”记录补一项以免显示空白。
        StatusOptions = TodoStatusOption.ForCurrent(_todo.Status);
        SetDueSelection(_todo.DueAt);
        var reminderSelection = _currentReminder?.ScheduledAt.LocalDateTime ?? GetDefaultReminderTime(_clock.Now);
        _reminderDate = reminderSelection.Date;
        _reminderHour = reminderSelection.Hour;
        _reminderMinute = reminderSelection.Minute;
        Source = row.Source;
        SourceChatName = row.SourceChatName;
        SenderName = row.SenderName;
        SentAt = row.SentAt;
        // 一条待办可能由多个群的多条消息共同触发，原始消息框需要完整展示全部来源。
        OriginalMessages = row.SourceMessages
            .Select(message => new TodoSourceMessageViewModel(message))
            .ToArray();
        OriginalContent = BuildOriginalContent(row);
        ProjectName = row.ProjectName;
        ProjectColor = row.ProjectColor;
        _isPinned = row.Todo.IsPinned;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        SaveDescriptionCommand = new AsyncRelayCommand(_ => SaveDescriptionAsync());
        CompleteCommand = new AsyncRelayCommand(_ => CompleteAsync(), _ => CanComplete);
        TogglePinCommand = new AsyncRelayCommand(_ => TogglePinAsync(), _ => CanTogglePin);
        ScheduleReminderCommand = new AsyncRelayCommand(_ => ScheduleReminderAsync());
        SnoozeTenMinutesCommand = new AsyncRelayCommand(_ => SnoozeAsync(TimeSpan.FromMinutes(10)));
        SnoozeThirtyMinutesCommand = new AsyncRelayCommand(_ => SnoozeAsync(TimeSpan.FromMinutes(30)));
        SnoozeOneHourCommand = new AsyncRelayCommand(_ => SnoozeAsync(TimeSpan.FromHours(1)));
        SnoozeTomorrowCommand = new AsyncRelayCommand(_ => SnoozeTomorrowAsync());
        NavigateToSourceCommand = new AsyncRelayCommand(_ => NavigateAsync(), _ => SourceMessageId.HasValue);
        SaveTodoRuleCommand = new AsyncRelayCommand(_ => SaveTodoRuleAsync(), _ => CanSaveTodoRule);
    }

    public event EventHandler? RequestClose;
    /// <summary>可选优先级，数量与颜色跟随“优先级设置”。</summary>
    public IReadOnlyList<PriorityOption> PriorityOptions { get; } = PriorityOption.FromCurrentPalette();
    /// <summary>详情窗口的状态选项：只有“完成”会移入已办理，其余状态仍留在待办理。</summary>
    public IReadOnlyList<TodoStatusOption> StatusOptions { get; }
    public ObservableCollection<TodoProjectOption> ProjectOptions { get; } = new();
    public IReadOnlyList<int> DueHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> DueMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public IReadOnlyList<int> ReminderHours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<int> ReminderMinutes { get; } = Enumerable.Range(0, 60).ToArray();
    public AsyncRelayCommand SaveCommand { get; }
    /// <summary>只提交“待办说明”字段，复用保存流程以保证并发版本检查一致。</summary>
    public AsyncRelayCommand SaveDescriptionCommand { get; }
    public AsyncRelayCommand CompleteCommand { get; }
    public AsyncRelayCommand TogglePinCommand { get; }
    public AsyncRelayCommand ScheduleReminderCommand { get; }
    public AsyncRelayCommand SnoozeTenMinutesCommand { get; }
    public AsyncRelayCommand SnoozeThirtyMinutesCommand { get; }
    public AsyncRelayCommand SnoozeOneHourCommand { get; }
    public AsyncRelayCommand SnoozeTomorrowCommand { get; }
    public AsyncRelayCommand NavigateToSourceCommand { get; }
    /// <summary>把当前来源消息归纳为“此类不再建待办”的规则。</summary>
    public AsyncRelayCommand SaveTodoRuleCommand { get; }
    /// <summary>没有来源消息或未接入规则仓储（例如单元测试）时隐藏整个规则区块。</summary>
    public System.Windows.Visibility TodoRuleVisibility => _todoRuleStore is not null && OriginalMessages.Count > 0
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;
    /// <summary>规则关键词：用户可参考上方原始消息自行填写，例如“验证码”。</summary>
    public string TodoRuleKeyword
    {
        get => _todoRuleKeyword;
        set
        {
            if (SetProperty(ref _todoRuleKeyword, value))
            {
                SaveTodoRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanSaveTodoRule => _todoRuleStore is not null && !string.IsNullOrWhiteSpace(_todoRuleKeyword);
    /// <summary>规则保存结果或校验提示，只影响本区块的文案。</summary>
    public string? TodoRuleStatusText
    {
        get => _todoRuleStatusText;
        private set => SetProperty(ref _todoRuleStatusText, value);
    }
    public long? SourceMessageId => _todo.SourceMessageId;
    public string Source { get; }
    public string SourceChatName { get; }
    public string SenderName { get; }
    public string SentAt { get; }
    public string OriginalContent { get; }
    /// <summary>全部来源消息（主来源在前），用于在原始消息框中逐条展示。</summary>
    public IReadOnlyList<TodoSourceMessageViewModel> OriginalMessages { get; }
    /// <summary>原始消息区标题，多来源时显示条数。</summary>
    public string OriginalMessageHeader => OriginalMessages.Count > 1
        ? $"原始消息（共 {OriginalMessages.Count} 条，只读，可直接选中复制）"
        : "原始消息（只读，可直接选中复制）";
    /// <summary>单条来源时显示来源/发送人行，多来源时由每条消息自带头信息。</summary>
    public System.Windows.Visibility PrimarySourceVisibility => OriginalMessages.Count > 1
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;
    /// <summary>来源与群聊的只读展示行，绑定到可选中复制的文本框。</summary>
    public string SourceInfoLine => $"来源：{Source}　群聊：{SourceChatName}";
    /// <summary>发送人与时间的只读展示行，绑定到可选中复制的文本框。</summary>
    public string SenderInfoLine => $"发送人：{SenderName}　时间：{SentAt}";
    public string ProjectName { get; private set; }
    public string ProjectColor { get; private set; }
    public bool IsProjectEditable => Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting;
    public bool CanComplete => Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting;
    /// <summary>只有活动待办可以置顶；已完成和已忽略的记录不能重新置顶。</summary>
    public bool CanTogglePin => Status is TodoStatus.Pending or TodoStatus.InProgress or TodoStatus.Waiting;
    public string PinButtonText => IsPinned ? "取消置顶" : "置顶";

    public bool IsPinned
    {
        get => _isPinned;
        private set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinButtonText));
            }
        }
    }

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

    /// <summary>
    /// 实际提交的层级：用户没有改动下拉时写回打开时的原值，
    /// 避免“减少优先级层数”把历史 P3 待办在保存其它字段时顺手降级。
    /// </summary>
    private PriorityLevel SubmittedPriority =>
        _priority == PrioritySettings.Current.Normalize(_originalPriority) ? _originalPriority : _priority;

    public TodoStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                // 与已持久化的状态比较，改回原值时清除未保存标记。
                _hasUnsavedStatusChange = value != _todo.Status;
                OnPropertyChanged(nameof(IsProjectEditable));
                OnPropertyChanged(nameof(CanComplete));
                OnPropertyChanged(nameof(CanTogglePin));
                CompleteCommand.RaiseCanExecuteChanged();
                TogglePinCommand.RaiseCanExecuteChanged();
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

    /// <summary>拼装待办的字段信息 + 原始消息正文，供“复制全部信息”使用。</summary>
    public string BuildCopyText()
    {
        var lines = new List<string>
        {
            $"标题：{Title}",
            $"项目：{ProjectName}",
            $"置顶：{(IsPinned ? "是" : "否")}",
            $"优先级：{PrioritySettings.Current.CodeFor(Priority)}",
            $"状态：{Status}",
            $"截止时间：{FormatDueText()}",
            $"提醒：{CurrentReminderText}",
            $"说明：{(string.IsNullOrWhiteSpace(Description) ? "无" : Description)}",
            $"来源：{Source}",
            $"群聊：{SourceChatName}",
            $"发送人：{SenderName}",
            $"发送时间：{SentAt}",
            string.Empty,
            "原始消息：",
            OriginalContent
        };
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>由界面层写入底部提示文本（如复制结果反馈）。</summary>
    public void SetStatus(string text) => StatusText = text;

    /// <summary>拼装原始消息区正文：单条直接返回原文，多条按“群聊/发送人/时间 + 正文”分块。</summary>
    private string BuildOriginalContent(TodoListItemViewModel row)
    {
        if (OriginalMessages.Count == 0)
        {
            return row.Todo.SourceMessageId is null
                ? row.Todo.Description ?? row.Todo.Title
                : "原始消息已不存在";
        }

        if (OriginalMessages.Count == 1)
        {
            return OriginalMessages[0].Content;
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            OriginalMessages.Select((message, index) =>
                $"[{index + 1}] {message.HeaderLine}{Environment.NewLine}{message.Content}"));
    }

    private string FormatDueText()
    {
        if (DueDate is not { } date)
        {
            return "无截止时间";
        }

        return $"{date:yyyy-MM-dd} {DueHour:00}:{DueMinute:00}";
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

    /// <summary>只保存“待办说明”字段；与“保存待办”共用同一条保存与并发检查路径。</summary>
    public Task SaveDescriptionAsync() => SaveAsync("待办说明已保存。");

    /// <summary>
    /// 提交详情里全部可编辑字段（含状态），成功后刷新待办、消息流与日历。
    /// 对外公开以便回归测试直接驱动真实的“保存待办”流程。
    /// </summary>
    public async Task SaveAsync(string successMessage = "待办已保存。")
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
                NormalizeDescription(),
                _todo.ProjectId,
                SubmittedPriority,
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
        _hasUnsavedStatusChange = false;
        ProjectName = SelectedProject.Name;
        ProjectColor = SelectedProject.Color;
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(ProjectColor));
        SetDueSelection(updated.DueAt);
        // 回写规范化后的说明（空白说明按“无说明”处理），保持界面与数据库一致。
        Description = updated.Description;
        StatusText = successMessage;
        await _refreshWorkbench();
    }

    /// <summary>
    /// 窗口关闭兜底：只提交尚未保存的状态改动。字段一律取已持久化的快照，
    /// 不把用户仍在编辑但没点“保存待办”的标题、说明、优先级、截止时间或项目顺带写库。
    /// </summary>
    /// <returns>是否确实提交了一次状态变更。</returns>
    public async Task<bool> SaveStatusIfChangedAsync()
    {
        if (!_hasUnsavedStatusChange)
        {
            return false;
        }

        try
        {
            var updated = await _todoService.UpdateAsync(
                new UpdateTodoRequest(
                    _todo.Id,
                    _todo.Title,
                    _todo.Description,
                    _todo.ProjectId,
                    _todo.Priority,
                    _todo.DueAt,
                    Status,
                    _todo.UpdatedAt,
                    GroupId: null,
                    ManagedProjectId: _todo.ManagedProjectId),
                CancellationToken.None);
            if (updated is null)
            {
                System.Diagnostics.Debug.WriteLine("Unsaved status change was rejected by the concurrency check.");
                return false;
            }

            _todo = updated;
            _hasUnsavedStatusChange = false;
            // 状态改回活动后待办、消息流与日历必须一起重读。
            await _refreshWorkbench();
            return true;
        }
        catch (Exception exception)
        {
            // 窗口已关闭，不能再弹对话框；记录诊断信息，下一次刷新会显示当前真实状态。
            System.Diagnostics.Debug.WriteLine($"Unsaved status change could not be saved: {exception.Message}");
            return false;
        }
    }

    /// <summary>把空白说明归一为 null，避免把空串写入数据库。</summary>
    private string? NormalizeDescription()
        => string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();

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
        IsPinned = updated.IsPinned;
        StatusText = "待办已完成。";
        await _refreshWorkbench();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换置顶：置顶的待办会排在待办页最上方。</summary>
    public async Task TogglePinAsync()
    {
        if (!CanTogglePin)
        {
            return;
        }

        var isPinned = !IsPinned;
        var updated = await _todos.SetPinnedAsync(_todo.Id, isPinned, _clock.Now, CancellationToken.None);
        if (!updated)
        {
            _dialogs.ShowError("待办已被其他操作修改，请关闭详情并刷新后重试。", isPinned ? "置顶失败" : "取消置顶失败");
            await _refreshWorkbench();
            return;
        }

        IsPinned = isPinned;
        // 置顶会写 updated_at，这里同步最新版本，避免随后的保存或完成被并发检查拒绝。
        var reloaded = await _todos.GetByIdAsync(_todo.Id, CancellationToken.None);
        if (reloaded is not null)
        {
            _todo = reloaded;
        }

        StatusText = isPinned ? "待办已置顶。" : "已取消置顶。";
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

    /// <summary>
    /// 保存“此类不再建待办”的关键词规则。只新增规则，不删除本条待办：
    /// 规则只影响之后新采集的消息，已有待办仍需用户自行处理。
    /// </summary>
    private async Task SaveTodoRuleAsync()
    {
        if (_todoRuleStore is null)
        {
            return;
        }

        try
        {
            var normalized = TodoRuleMatcher.Normalize(TodoRuleKeyword);
            await _todoRuleStore.AddAsync(normalized, CancellationToken.None);
            // 写入后清空输入，避免连续点击产生重复规则或误解为“已加入多条”。
            TodoRuleKeyword = string.Empty;
            TodoRuleStatusText = $"已记住「{normalized}」：之后命中该关键词的消息只入库，不再自动建待办。";
        }
        catch (ArgumentException exception)
        {
            // 校验信息面向用户，例如“至少包含一个不少于 2 个字符的关键词”。
            TodoRuleStatusText = exception.Message;
        }
        catch (Exception exception)
        {
            TodoRuleStatusText = $"规则保存失败：{exception.Message}";
        }
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

/// <summary>原始消息框中的单条来源消息，自带群聊/发送人/时间头信息。</summary>
public sealed class TodoSourceMessageViewModel
{
    public TodoSourceMessageViewModel(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Source = message.Source;
        ChatName = message.ChatName;
        SenderName = message.SenderName;
        SentAt = message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        Content = message.Content;
        HeaderLine = $"群聊：{ChatName}　发送人：{SenderName}　时间：{SentAt}";
    }

    public string Source { get; }
    public string ChatName { get; }
    public string SenderName { get; }
    public string SentAt { get; }
    public string Content { get; }
    /// <summary>"群聊：xx　发送人：xx　时间：xx"，用于多来源时区分每条消息。</summary>
    public string HeaderLine { get; }
}
