using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Messages;

public sealed class MessageFeedViewModel : ObservableObject
{
    private readonly IMessageRepository _messages;
    private readonly ITodoRepository _todoRepository;
    private readonly TodoApplicationService _todos;
    private readonly IMessageActionModel _model;
    private readonly Func<IReadOnlyList<string>> _aliases;
    private readonly Func<IReadOnlyList<string>> _followedChats;
    private readonly Func<FollowedChatFilterMode> _filterMode;
    private readonly Func<CreateTodoResult, Task> _showTodo;
    private int _pageNumber = 1;
    private int _pageSize = 50;
    private int _totalCount;
    private string _pageNumberInput = "1";
    private string _statusText = "正在加载消息流...";
    private bool _isContextMode;
    private MessageListItemViewModel? _selectedMessage;
    private int _pageBeforeContext = 1;
    private long? _pendingContextMessageId;

    /// <summary>串行化对 <see cref="Items"/> 的整批替换，避免并发刷新与上下文跳转互相撕裂集合。</summary>
    private readonly object _itemsGate = new();

    public MessageFeedViewModel(
        IMessageRepository messages,
        ITodoRepository todoRepository,
        TodoApplicationService todos,
        IMessageActionModel model,
        Func<IReadOnlyList<string>> aliases,
        Func<IReadOnlyList<string>> followedChats,
        Func<FollowedChatFilterMode> filterMode,
        Func<CreateTodoResult, Task> showTodo)
    {
        _messages = messages;
        _todoRepository = todoRepository;
        _todos = todos;
        _model = model;
        _aliases = aliases;
        _followedChats = followedChats;
        _filterMode = filterMode;
        _showTodo = showTodo;
        FirstPageCommand = new AsyncRelayCommand(_ => LoadPageAsync(1, refreshCount: false), _ => !_isContextMode && _pageNumber > 1);
        PreviousPageCommand = new AsyncRelayCommand(_ => LoadPageAsync(_pageNumber - 1, false), _ => !_isContextMode && _pageNumber > 1);
        NextPageCommand = new AsyncRelayCommand(_ => LoadPageAsync(_pageNumber + 1, false), _ => !_isContextMode && _pageNumber < PageCount);
        LastPageCommand = new AsyncRelayCommand(_ => LoadPageAsync(PageCount, false), _ => !_isContextMode && _pageNumber < PageCount);
        GoToPageCommand = new AsyncRelayCommand(_ => GoToPageAsync(), _ => !_isContextMode);
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        ConvertToTodoCommand = new AsyncRelayCommand(ConvertToTodoAsync, parameter => parameter is long id && id > 0);
        ReturnToListCommand = new AsyncRelayCommand(_ => LoadPageAsync(_pageBeforeContext, true), _ => _isContextMode);
    }

    public ObservableCollection<MessageListItemViewModel> Items { get; } = new();
    public IReadOnlyList<int> PageSizes { get; } = new[] { 20, 50, 100, 200 };
    public AsyncRelayCommand FirstPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand LastPageCommand { get; }
    public AsyncRelayCommand GoToPageCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ConvertToTodoCommand { get; }
    public AsyncRelayCommand ReturnToListCommand { get; }

    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (SetProperty(ref _pageSize, Math.Clamp(value, 1, 200)))
            {
                _ = LoadPageAsync(1, refreshCount: true);
            }
        }
    }

    public string PageNumberInput
    {
        get => _pageNumberInput;
        set => SetProperty(ref _pageNumberInput, value);
    }

    public string PageText => IsContextMode
        ? $"原消息上下文，共 {Items.Count} 条"
        : $"第 {_pageNumber}/{PageCount} 页，共 {_totalCount} 条，每页 {_pageSize} 条";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsContextMode
    {
        get => _isContextMode;
        private set
        {
            if (SetProperty(ref _isContextMode, value))
            {
                OnPropertyChanged(nameof(ContextVisibility));
                OnPropertyChanged(nameof(PageText));
                RaisePagingCanExecuteChanged();
            }
        }
    }

    public System.Windows.Visibility ContextVisibility => IsContextMode
        ? System.Windows.Visibility.Visible
        : System.Windows.Visibility.Collapsed;

    public MessageListItemViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set => SetProperty(ref _selectedMessage, value);
    }

    public int TotalCount => _totalCount;

    public Task RefreshAsync()
    {
        // 正在跳转到某条消息的上下文时，切页触发的自动刷新会覆盖上下文结果，改为重新定位该消息。
        return _pendingContextMessageId is { } messageId
            ? ShowContextAsync(messageId)
            : LoadPageAsync(_pageNumber, refreshCount: true);
    }

    /// <summary>登记即将跳转到的原始消息，避免切页自动刷新覆盖跳转结果。</summary>
    public void PrepareContextNavigation(long messageId) => _pendingContextMessageId = messageId;

    public async Task ShowContextAsync(long messageId)
    {
        _pendingContextMessageId = messageId;
        try
        {
            var context = await _messages.GetContextAsync(messageId, before: 20, after: 20, CancellationToken.None);
            if (context is null)
            {
                StatusText = "原始消息已不存在。";
                return;
            }

            if (!IsContextMode)
            {
                _pageBeforeContext = _pageNumber;
            }

            IsContextMode = true;
            var rows = await ReplaceItemsAsync(context.Messages);
            // 从本次载入的行里定位，而不是再枚举共享集合：并发刷新正在改写 Items 时不会抛“集合已被修改”。
            SelectedMessage = rows.FirstOrDefault(item => item.MessageId == messageId);
            StatusText = "正在查看原始消息上下文；当前关注群过滤已临时忽略。";
            OnPropertyChanged(nameof(PageText));
        }
        finally
        {
            if (_pendingContextMessageId == messageId)
            {
                _pendingContextMessageId = null;
            }
        }
    }

    private async Task LoadPageAsync(int pageNumber, bool refreshCount)
    {
        var safePage = Math.Max(1, pageNumber);
        var chats = _followedChats();
        var include = _filterMode() == FollowedChatFilterMode.Include;
        MessagePage page;
        if (chats.Count == 0)
        {
            page = refreshCount || _totalCount == 0 || IsContextMode
                ? await _messages.GetPageAsync(safePage, _pageSize, CancellationToken.None)
                : await _messages.GetPageWithKnownCountAsync(safePage, _pageSize, _totalCount, CancellationToken.None);
        }
        else
        {
            page = refreshCount || _totalCount == 0 || IsContextMode
                ? await _messages.GetPageAsync(safePage, _pageSize, chats, include, CancellationToken.None)
                : await _messages.GetPageWithKnownCountAsync(safePage, _pageSize, _totalCount, chats, include, CancellationToken.None);
        }

        // 已经登记了上下文跳转时，本次整页结果已过期，直接丢弃，避免把上下文替换成普通列表
        if (_pendingContextMessageId is not null)
        {
            return;
        }

        _totalCount = page.TotalCount;
        var pageCount = PageCount;
        if (page.Messages.Count == 0 && page.TotalCount > 0 && safePage > pageCount)
        {
            await LoadPageAsync(pageCount, refreshCount: false);
            return;
        }

        _pageNumber = page.PageNumber;
        PageNumberInput = _pageNumber.ToString();
        IsContextMode = false;
        await ReplaceItemsAsync(page.Messages);
        SelectedMessage = null;
        StatusText = page.Messages.Count == 0 ? "暂无符合当前关注群设置的消息。" : $"已加载本页 {page.Messages.Count} 条消息。";
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(TotalCount));
        RaisePagingCanExecuteChanged();
    }

    private async Task ConvertToTodoAsync(object? parameter)
    {
        if (parameter is not long messageId || messageId <= 0)
        {
            return;
        }

        // 幂等打开已有待办：主来源或被合并来源都算已转待办，避免重复建模或重复追加评分。
        var existing = await _todoRepository.GetByAnySourceMessageIdAsync(messageId, CancellationToken.None);
        var message = existing is null
            ? await _messages.GetByIdAsync(messageId, CancellationToken.None)
            : null;
        MentionMessageAnalysis? analysis = null;
        var analysisFailed = false;
        if (message is not null)
        {
            StatusText = "正在调用 Agent 总结消息并分析优先级...";
            try
            {
                analysis = await _model.AnalyzeMentionAsync(message, CancellationToken.None);
            }
            catch (Exception)
            {
                // 手动点击是明确创建指令，模型失败只回退内容，不阻断待办创建。
                analysisFailed = true;
            }
        }

        // 手动创建忽略 ShouldCreate；空白摘要回退默认说明，但保留 Agent 优先级和识别出的时间提醒。
        var agentDescription = analysis?.Description?.Trim();
        var description = string.IsNullOrWhiteSpace(agentDescription) ? null : agentDescription;

        // 跨群去重：同一件事已在待办中时只追加来源消息，并在新提醒更晚时改期。
        if (existing is null && message is not null)
        {
            var duplicate = await new TodoDuplicateDetector(_todoRepository, _model)
                .DetectAsync(message, CancellationToken.None);
            if (duplicate is not null)
            {
                StatusText = "正在把该消息合并到已有待办...";
                // “那就5点半”“改到16点”这类确认表达代表大家最终认同的事件时间，
                // 合并时允许把既有待办的截止与提醒改早；询问式时间仍只向后取更晚。
                var timeConfirmed = TodoService.TryResolveConfirmedClockTime(
                    message, TimeZoneInfo.Local, out var confirmedDueAt);
                var confirmedReminderAt = timeConfirmed
                    ? TodoService.ResolveConfirmedReminder(confirmedDueAt, DateTimeOffset.Now)
                    : null;
                // 只有未来时间才提交，避免过期提醒触发参数校验失败。
                var reminderAt = analysis?.FirstReminderAt is { } candidate && candidate > DateTimeOffset.Now
                    ? candidate
                    : confirmedReminderAt;
                var merge = await _todos.MergeIntoExistingAsync(
                    new MergeTodoRequest(
                        duplicate.Id,
                        messageId,
                        reminderAt,
                        analysis?.DueAt ?? (timeConfirmed ? confirmedDueAt : null),
                        TimeConfirmed: timeConfirmed),
                    CancellationToken.None);
                if (merge.Merged && merge.Todo is not null)
                {
                    await RefreshAsync();
                    StatusText = merge.ReminderRescheduled
                        ? timeConfirmed
                            ? "该事项已有待办，已合并来源消息并按确认的时间更新提醒。"
                            : "该事项已有待办，已合并来源消息并按更晚时间更新提醒。"
                        : "该事项已有待办，已把本条消息加入原始消息。";
                    await _showTodo(new CreateTodoResult(CreateTodoOutcome.MergedIntoExisting, merge.Todo));
                    return;
                }
            }
        }

        var result = existing is not null
            ? new CreateTodoResult(CreateTodoOutcome.ExistingTodo, existing)
            : await _todos.CreateFromMessageAsync(
                new CreateTodoFromMessageRequest(
                    messageId,
                    Title: null,
                    Description: description,
                    ProjectId: null,
                    Priority: null,
                    DueAt: null,
                    FirstReminderAt: analysis?.FirstReminderAt,
                    AgentPriority: analysis?.Priority,
                    AnalysisDueAt: analysis?.DueAt),
                CancellationToken.None);
        // 刷新会更新列表状态文本，先刷新再写最终结果，保留模型失败回退提示。
        if (result.Todo is not null && result.Outcome == CreateTodoOutcome.Created)
        {
            await RefreshAsync();
        }

        StatusText = result.Outcome switch
        {
            CreateTodoOutcome.Created when analysisFailed => "模型分析失败，已按默认内容和本地优先级创建待办。",
            CreateTodoOutcome.Created => "已从消息创建待办。",
            CreateTodoOutcome.ExistingTodo => "该消息已有待办，已打开现有详情。",
            CreateTodoOutcome.MergedIntoExisting => "该事项已有待办，已把本条消息加入原始消息。",
            _ => "原始消息已不存在，无法创建待办。"
        };
        if (result.Todo is not null)
        {
            await _showTodo(result);
        }
    }

    private Task GoToPageAsync()
    {
        if (!int.TryParse(PageNumberInput, out var page))
        {
            StatusText = "请输入有效页码。";
            return Task.CompletedTask;
        }

        return LoadPageAsync(Math.Clamp(page, 1, PageCount), refreshCount: false);
    }

    private int PageCount => Math.Max(1, (int)Math.Ceiling(_totalCount / (double)_pageSize));

    private async Task<IReadOnlyList<MessageListItemViewModel>> ReplaceItemsAsync(IEnumerable<Message> messages)
    {
        var messageList = messages.ToArray();
        var aliases = _aliases().ToArray();
        var itemRows = await Task.Run(async () =>
        {
            var ids = messageList.Select(message => message.Id).ToArray();
            var todosByMessageId = await _todoRepository.GetBySourceMessageIdsAsync(
                ids,
                CancellationToken.None);
            // 最新消息评分统一来自 urgency_scores；手动 Agent 评分也追加在该表中。
            var prioritiesByMessageId = await _messages.GetEffectivePrioritiesAsync(ids, CancellationToken.None);
            var detector = new MentionDetector(aliases);
            return messageList.Select(message =>
            {
                todosByMessageId.TryGetValue(message.Id, out var todo);
                var todoAction = FormatTodoAction(todo);
                var priority = prioritiesByMessageId.GetValueOrDefault(message.Id, PriorityLevel.P3);
                return new MessageListItemViewModel(
                    message.Id,
                    message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                    FormatSource(message.Source),
                    message.ChatName,
                    message.SenderName,
                    message.IsMentionMe ? "是" : "否",
                    message.Content,
                    MessageHighlighter.HighlightMentions(message.Content, detector.ExtractMentionedAliases(message.Content)),
                    todoAction.Text,
                    todoAction.ToolTip,
                    PrioritySettings.Current.CodeFor(priority),
                    PriorityColor(priority));
            }).ToArray();
        });

        // Clear + 逐条 Add 视为一个整体，必须在同一把锁内完成；否则并发的一批会在中途清空，
        // 既丢失这一批的行，也会让正在枚举集合的一方抛“集合已被修改”。
        lock (_itemsGate)
        {
            Items.Clear();
            foreach (var item in itemRows)
            {
                Items.Add(item);
            }
        }

        return itemRows;
    }

    /// <summary>保留原有入口，实际色值统一来自 <see cref="PriorityColorPolicy"/>。</summary>
    public static string PriorityColor(PriorityLevel priority) => PriorityColorPolicy.For(priority);

    private static (string Text, string ToolTip) FormatTodoAction(TodoItem? todo)
    {
        if (todo is null)
        {
            return ("转为待办", "该消息尚未关联待办");
        }

        if (todo.Status is TodoStatus.Done or TodoStatus.Ignored)
        {
            return ("已办理", "该消息的待办已结束；点击查看详情");
        }

        return todo.IsAutoCreated
            ? ("已自动加入", "系统已根据 @我 自动创建待办；点击查看详情")
            : ("已加入待办", "该消息已手动转为待办；点击查看详情");
    }

    private void RaisePagingCanExecuteChanged()
    {
        FirstPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        LastPageCommand.RaiseCanExecuteChanged();
        GoToPageCommand.RaiseCanExecuteChanged();
        ReturnToListCommand.RaiseCanExecuteChanged();
    }

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

public sealed record MessageListItemViewModel(
    long MessageId,
    string SentAt,
    string Source,
    string ChatName,
    string SenderName,
    string IsMentionMe,
    string RawContent,
    IEnumerable<MessageHighlighter.TextSegment> ContentSegments,
    string TodoActionText,
    string TodoActionToolTip,
    string Priority,
    string PriorityColor);
