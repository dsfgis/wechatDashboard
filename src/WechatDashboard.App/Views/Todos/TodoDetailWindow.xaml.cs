using System.Windows;
using WechatDashboard.App.ViewModels.Todos;

namespace WechatDashboard.App.Views.Todos;

public partial class TodoDetailWindow : Window
{
    public TodoDetailWindow(TodoDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += (_, _) => Close();
    }
}
