using System.Windows;
using WechatDashboard.App.ViewModels.Todos;

namespace WechatDashboard.App.Views.Todos;

public partial class TodoCreateWindow : Window
{
    public TodoCreateWindow(TodoCreateViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += (_, _) => Close();
    }
}
