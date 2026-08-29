using System.Windows;
using WechatDashboard.App.ViewModels.Todos;
using WechatDashboard.App.Views.Todos;

namespace WechatDashboard.App.Services;

public sealed class TodoDialogService : ITodoDialogService
{
    public Task ShowAsync(TodoDetailViewModel viewModel)
    {
        var window = new TodoDetailWindow(viewModel) { Owner = ResolveOwner() };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    public Task ShowCreateAsync(TodoCreateViewModel viewModel)
    {
        var window = new TodoCreateWindow(viewModel) { Owner = ResolveOwner() };
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

    private static Window? ResolveOwner() => System.Windows.Application.Current.Windows
        .OfType<Window>()
        .FirstOrDefault(window => window.IsActive)
        ?? System.Windows.Application.Current.MainWindow;
}
