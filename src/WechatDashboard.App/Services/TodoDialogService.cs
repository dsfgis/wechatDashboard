using System.Windows;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.App.Views.Todos;

namespace WechatDashboard.App.Services;

public sealed class TodoDialogService : ITodoDialogService
{
    public Task ShowAsync(TodoDetailViewModel viewModel)
    {
        var window = new TodoDetailWindow(viewModel) { Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public bool Confirm(string message, string title)
    {
        return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public void ShowError(string message, string title)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
