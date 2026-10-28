using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WechatDashboard.App.Views.Reminders;

public enum ReminderNotificationAction
{
    Acknowledge,
    SnoozeTenMinutes,
    Complete
}

/// <summary>
/// 单条待办提醒卡片。窗口不再自行 ShowDialog，而是由 <c>ReminderNotificationQueue</c>
/// 统一入栈、定位与回收；关闭时通过 <see cref="Action"/> 暴露用户的选择。
/// </summary>
public partial class ReminderNotificationWindow : Window, INotifyPropertyChanged
{
    private string _queueBadgeText = string.Empty;
    private Visibility _queueBadgeVisibility = Visibility.Collapsed;
    // 防止重复点击按钮触发多次关闭。
    private bool _resolved;

    public ReminderNotificationWindow(string todoTitle, string? todoDescription)
    {
        InitializeComponent();
        TodoTitle = todoTitle;
        TodoDescription = todoDescription;
        DataContext = this;

        // 主窗口仅隐藏到托盘时不设 Owner：避免提醒随隐藏窗口一起不可见。
        var owner = System.Windows.Application.Current.MainWindow;
        if (owner?.IsVisible == true)
        {
            Owner = owner;
        }

        // 队列定位完成前保持透明，避免卡片先在左上角闪现。
        Opacity = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TodoTitle { get; }
    public string? TodoDescription { get; }

    public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(TodoDescription)
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>用户选择；未显式操作直接关窗（点 X）时按“知道了”处理。</summary>
    public ReminderNotificationAction Action { get; private set; } = ReminderNotificationAction.Acknowledge;

    public string QueueBadgeText
    {
        get => _queueBadgeText;
        private set
        {
            if (_queueBadgeText == value)
            {
                return;
            }

            _queueBadgeText = value;
            OnPropertyChanged();
        }
    }

    public Visibility QueueBadgeVisibility
    {
        get => _queueBadgeVisibility;
        private set
        {
            if (_queueBadgeVisibility == value)
            {
                return;
            }

            _queueBadgeVisibility = value;
            OnPropertyChanged();
        }
    }

    /// <summary>显示队列中还有多少条提醒在等待，避免用户误以为只有当前这一条。</summary>
    public void UpdateQueueStatus(int pendingCount)
    {
        QueueBadgeText = pendingCount > 0 ? $"+{pendingCount} 条待查看" : string.Empty;
        QueueBadgeVisibility = pendingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e) => Resolve(ReminderNotificationAction.Acknowledge);

    private void Snooze_Click(object sender, RoutedEventArgs e) => Resolve(ReminderNotificationAction.SnoozeTenMinutes);

    private void Complete_Click(object sender, RoutedEventArgs e) => Resolve(ReminderNotificationAction.Complete);

    private void Resolve(ReminderNotificationAction action)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;
        Action = action;
        Close();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
