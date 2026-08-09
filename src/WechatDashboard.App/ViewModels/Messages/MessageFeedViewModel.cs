using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Application.Todos;

namespace WechatDashboard.App.ViewModels.Messages;

public sealed class MessageFeedViewModel : ObservableObject
{
    private readonly IMessageRepository _messages;
    private readonly TodoApplicationService _todos;
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

    public MessageFeedViewModel(
        IMessageRepository messages,
        TodoApplicationService todos,
        Func<IReadOnlyList<string>> aliases,
        Func<IReadOnlyList<string>> followedChats,
        Func<FollowedChatFilterMode> filterMode,
        Func<CreateTodoResult, Task> showTodo)
    {
        _messages = messages;
        _todos = todos;
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

    public Task RefreshAsync() => LoadPageAsync(_pageNumber, refreshCount: true);

    public async Task ShowContextAsync(long messageId)
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
        ReplaceItems(context.Messages);
        SelectedMessage = Items.First(item => item.MessageId == messageId);
        StatusText = "正在查看原始消息上下文；当前关注群过滤已临时忽略。";
        OnPropertyChanged(nameof(PageText));
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
        ReplaceItems(page.Messages);
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

    private void ReplaceItems(IEnumerable<WechatDashboard.Domain.Entities.Message> messages)
    {
        var detector = new MentionDetector(_aliases());
        Items.Clear();
        foreach (var message in messages)
        {
            Items.Add(new MessageListItemViewModel(
                message.Id,
                message.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                FormatSource(message.Source),
                message.ChatName,
                message.SenderName,
                message.IsMentionMe ? "是" : "否",
                message.Content,
                MessageHighlighter.HighlightMentions(message.Content, detector.ExtractMentionedAliases(message.Content))));
        }
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
    IEnumerable<MessageHighlighter.TextSegment> ContentSegments);
