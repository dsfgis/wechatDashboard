using System.Windows.Controls;

namespace WechatDashboard.App.Views.Todos;

public partial class DesktopCalendarView : UserControl
{
    public DesktopCalendarView()
    {
        InitializeComponent();
        Services.DesktopCalendarTheme.Apply(Resources, false);
    }

    public void SetDarkTheme(bool dark) => Services.DesktopCalendarTheme.Apply(Resources, dark);
}
