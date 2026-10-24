using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.Application.Agent;
using WechatDashboard.Application.Capture;
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
    private readonly ITodoSuggestionRepository _suggestions;
    private readonly TodoSuggestionAgent _agent;
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
    private TodoSuggestion? _selectedSuggestion;
    private string _clarificationAnswer = string.Empty;
    private int _pageBeforeContext = 1;
    private long? _pendingContextMessageId;

    public MessageFeedViewModel(
        IMessageRepository messages,
        ITodoRepository todoRepository,
        TodoApplicationService todos,
        ITodoSuggestionRepository suggestions,
        TodoSuggestionAgent agent,
        Func<IReadOnlyList<string>> aliases,
        Func<IReadOnlyList<string>> followedChats,
        Func<FollowedChatFilterMode> filterMode,
        Func<CreateTodoResult, Task> showTodo)
    {
        _messages = messages;
        _todoRepository = todoRepository;
        _todos = todos;
        _suggestions = suggestions;
        _agent = agent;
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
        AnalyzeMessageCommand = new AsyncRelayCommand(AnalyzeMessageAsync, parameter => parameter is long id && id > 0);
        ApplySuggestionCommand = new AsyncRelayCommand(_ => ApplySuggestionAsync(), _ => CanApplySuggestion);
        DismissSuggestionCommand = new AsyncRelayCommand(_ => DismissSuggestionAsync(), _ => CanDismissSuggestion);
        SubmitClarificationCommand = new AsyncRelayCommand(_ => SubmitClarificationAsync(), _ => CanClarifySuggestion);
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
    public AsyncRelayCommand AnalyzeMessageCommand { get; }
    public AsyncRelayCommand ApplySuggestionCommand { get; }
    public AsyncRelayCommand DismissSuggestionCommand { get; }
    public AsyncRelayCommand SubmitClarificationCommand { get; }
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
        set
        {
            if (SetProperty(ref _selectedMessage, value)) SelectedSuggestion = value?.Suggestion;
        }
    }

    public TodoSuggestion? SelectedSuggestion
    {
        get => _selectedSuggestion;
        private set
        {
            if (!SetProperty(ref _selectedSuggestion, value)) return;
            OnPropertyChanged(nameof(SuggestionVisibility));
            OnPropertyChanged(nameof(SuggestionTitle));
            OnPropertyChanged(nameof(SuggestionDescription));
            OnPropertyChanged(nameof(SuggestionDetails));
            OnPropertyChanged(nameof(CanApplySuggestion));
            OnPropertyChanged(nameof(CanDismissSuggestion));
            OnPropertyChanged(nameof(ClarificationVisibility));
            OnPropertyChanged(nameof(CanClarifySuggestion));
            ApplySuggestionCommand.RaiseCanExecuteChanged();
            DismissSuggestionCommand.RaiseCanExecuteChanged();
            SubmitClarificationCommand.RaiseCanExecuteChanged();
        }
    }

    public System.Windows.Visibility SuggestionVisibility => SelectedSuggestion is null
        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public string SuggestionTitle => SelectedSuggestion is null ? string.Empty : SelectedSuggestion.Decision switch
    {
        MessageActionDecision.SuggestTodo => $"待办建议 · {SelectedSuggestion.Title}",
        MessageActionDecision.AskUser => $"需要确认 · {SelectedSuggestion.Question}",
        _ => "模型判断：无需新建待办"
    };
    public string SuggestionDescription => SelectedSuggestion?.Description ?? string.Empty;
    public string SuggestionDetails => SelectedSuggestion is null ? string.Empty
        : $"优先级 {SelectedSuggestion.Priority} · {SelectedSuggestion.Reason}"
          + (SelectedSuggestion.DueAt is { } due ? $" · 截止 {due.LocalDateTime:yyyy-MM-dd HH:mm}" : "")
          + $" · {SelectedSuggestion.ReviewStatus switch { TodoSuggestionReviewStatus.Pending => "待确认", TodoSuggestionReviewStatus.Accepted => "已采纳", _ => "已忽略" }}";
    public bool CanApplySuggestion => SelectedSuggestion is { Decision: MessageActionDecision.SuggestTodo, ReviewStatus: TodoSuggestionReviewStatus.Pending };
    public bool CanDismissSuggestion => SelectedSuggestion is { ReviewStatus: TodoSuggestionReviewStatus.Pending };
    public System.Windows.Visibility ClarificationVisibility => SelectedSuggestion is
        { Decision: MessageActionDecision.AskUser, ReviewStatus: TodoSuggestionReviewStatus.Pending }
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string ClarificationAnswer
    {
        get => _clarificationAnswer;
        set
        {
            if (SetProperty(ref _clarificationAnswer, value)) SubmitClarificationCommand.RaiseCanExecuteChanged();
        }
    }
    public bool CanClarifySuggestion => ClarificationVisibility == System.Windows.Visibility.Visible
        && !string.IsNullOrWhiteSpace(ClarificationAnswer);

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
            await ReplaceItemsAsync(context.Messages);
            SelectedMessage = Items.FirstOrDefault(item => item.MessageId == messageId);
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

        var result = await _todos.CreateFromMessageAsync(
            new CreateTodoFromMessageRequest(messageId, null, null, null, null, null, null),
            CancellationToken.None);
        StatusText = result.Outcome switch
        {
            CreateTodoOutcome.Created => "已从消息创建待办。",
            CreateTodoOutcome.ExistingTodo => "该消息已有待办，已打开现有详情。",
            _ => "原始消息已不存在，无法创建待办。"
        };
        if (result.Todo is not null)
        {
            if (result.Outcome == CreateTodoOutcome.Created)
            {
                await RefreshAsync();
            }
            await _showTodo(result);
        }
    }

    private async Task AnalyzeMessageAsync(object? parameter)
    {
        if (parameter is not long messageId || messageId <= 0) return;
        try
        {
            StatusText = "正在分析消息的行动要求和优先级...";
            var suggestion = await _agent.AnalyzeAsync(messageId, CancellationToken.None);
            await RefreshAsync();
            SelectedMessage = Items.FirstOrDefault(item => item.MessageId == messageId);
            SelectedSuggestion = suggestion;
            StatusText = suggestion.Decision switch
            {
                MessageActionDecision.SuggestTodo => "建议已生成。核对内容后可加入待办。",
                MessageActionDecision.AskUser => "行动要求尚不明确，请查看建议中的澄清问题。",
                _ => "已完成分析，当前消息无需新建待办。"
            };
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
        }
        catch (Exception)
        {
            StatusText = "消息分析失败，未保存建议；请检查模型配置后重试。";
        }
    }

    private async Task ApplySuggestionAsync()
    {
        var suggestion = SelectedSuggestion;
        if (suggestion is not { Decision: MessageActionDecision.SuggestTodo, ReviewStatus: TodoSuggestionReviewStatus.Pending }) return;
        try
        {
            var result = await _todos.CreateFromMessageAsync(new CreateTodoFromMessageRequest(
                suggestion.MessageId, suggestion.Title, suggestion.Description, null, suggestion.Priority,
                suggestion.DueAt, null), CancellationToken.None);
            var applied = result.Outcome == CreateTodoOutcome.Created
                || result is { Outcome: CreateTodoOutcome.ExistingTodo, Todo: { } existing }
                   && existing.Title == suggestion.Title
                   && existing.Description == suggestion.Description
                   && existing.Priority == suggestion.Priority
                   && existing.DueAt == suggestion.DueAt;
            if (applied)
            {
                await _suggestions.SetReviewStatusAsync(suggestion.MessageId, TodoSuggestionReviewStatus.Accepted, CancellationToken.None);
                SelectedSuggestion = suggestion with { ReviewStatus = TodoSuggestionReviewStatus.Accepted };
                StatusText = result.Outcome == CreateTodoOutcome.Created
                    ? "建议已采纳并创建待办。" : "建议已采纳；现有待办内容与建议一致。";
                await RefreshAsync();
            }
            else StatusText = result.Outcome == CreateTodoOutcome.ExistingTodo
                ? "该消息已有待办，请核对现有待办是否包含建议内容。"
                : "原始消息已不存在，无法创建待办。";
            if (result.Todo is not null) await _showTodo(result);
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
        }
        catch (Exception)
        {
            StatusText = "采纳建议失败，请检查待办和建议状态后重试。";
        }
    }

    private async Task DismissSuggestionAsync()
    {
        var suggestion = SelectedSuggestion;
        if (suggestion is not { ReviewStatus: TodoSuggestionReviewStatus.Pending }) return;
        try
        {
            await _suggestions.SetReviewStatusAsync(suggestion.MessageId, TodoSuggestionReviewStatus.Dismissed, CancellationToken.None);
            SelectedSuggestion = suggestion with { ReviewStatus = TodoSuggestionReviewStatus.Dismissed };
            StatusText = "已忽略这条建议。";
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
        }
        catch (Exception)
        {
            StatusText = "处理建议失败，请稍后重试。";
        }
    }

    private async Task SubmitClarificationAsync()
    {
        var suggestion = SelectedSuggestion;
        if (suggestion is not { Decision: MessageActionDecision.AskUser, ReviewStatus: TodoSuggestionReviewStatus.Pending }) return;
        try
        {
            StatusText = "正在根据回答重新判断...";
            var revised = await _agent.ClarifyAsync(suggestion.MessageId, ClarificationAnswer, CancellationToken.None);
            ClarificationAnswer = string.Empty;
            await RefreshAsync();
            SelectedMessage = Items.FirstOrDefault(item => item.MessageId == suggestion.MessageId);
            SelectedSuggestion = revised;
            StatusText = revised.Decision switch
            {
                MessageActionDecision.SuggestTodo => "已生成更新后的待办建议，请核对后采纳。",
                MessageActionDecision.NoAction => "根据回答，这条消息无需新建待办。",
                _ => "仍需澄清；请查看更新后的问题。"
            };
        }
        catch (ArgumentException exception)
        {
            StatusText = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
        }
        catch (Exception)
        {
            StatusText = "重新判断失败；原建议仍保留。";
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

    private async Task ReplaceItemsAsync(IEnumerable<Message> messages)
    {
        var messageList = messages.ToArray();
        var aliases = _aliases().ToArray();
        var itemRows = await Task.Run(async () =>
        {
            var ids = messageList.Select(message => message.Id).ToArray();
            var todosByMessageId = await _todoRepository.GetBySourceMessageIdsAsync(
                ids,
                CancellationToken.None);
            var suggestionsByMessageId = await _suggestions.GetByMessageIdsAsync(ids, CancellationToken.None);
            var prioritiesByMessageId = await _suggestions.GetEffectivePrioritiesAsync(ids, CancellationToken.None);
            var detector = new MentionDetector(aliases);
            return messageList.Select(message =>
            {
                todosByMessageId.TryGetValue(message.Id, out var todo);
                var todoAction = FormatTodoAction(todo);
                suggestionsByMessageId.TryGetValue(message.Id, out var suggestion);
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
                    priority.ToString(),
                    PriorityColor(priority),
                    suggestion);
            }).ToArray();
        });

        Items.Clear();
        foreach (var item in itemRows)
        {
            Items.Add(item);
        }
    }

    public static string PriorityColor(PriorityLevel priority) => priority switch
    {
        PriorityLevel.P0 => "#D92D20",
        PriorityLevel.P1 => "#B54708",
        PriorityLevel.P2 => "#3B5998",
        _ => "#475467"
    };

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
    string PriorityColor,
    TodoSuggestion? Suggestion);
