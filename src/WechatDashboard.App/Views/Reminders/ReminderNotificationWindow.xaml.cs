using System.Windows;

namespace WechatDashboard.App.Views.Reminders;

public enum ReminderNotificationAction
{
    Acknowledge,
    SnoozeTenMinutes,
    Complete
}

public partial class ReminderNotificationWindow : Window
{
    private ReminderNotificationWindow(string todoTitle, string? todoDescription)
    {
        InitializeComponent();
        TodoTitle = todoTitle;
        TodoDescription = todoDescription;
        DataContext = this;

        var owner = System.Windows.Application.Current.MainWindow;
        if (owner?.IsVisible == true)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public string TodoTitle { get; }
    public string? TodoDescription { get; }
    public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(TodoDescription)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public ReminderNotificationAction Action { get; private set; } = ReminderNotificationAction.Acknowledge;

    public static ReminderNotificationAction Show(string todoTitle, string? todoDescription)
    {
        var window = new ReminderNotificationWindow(todoTitle, todoDescription);
        window.ShowDialog();
        return window.Action;
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e) => CloseWith(ReminderNotificationAction.Acknowledge);

    private void Snooze_Click(object sender, RoutedEventArgs e) => CloseWith(ReminderNotificationAction.SnoozeTenMinutes);

    private void Complete_Click(object sender, RoutedEventArgs e) => CloseWith(ReminderNotificationAction.Complete);

    private void CloseWith(ReminderNotificationAction action)
    {
        Action = action;
        DialogResult = true;
    }
}
