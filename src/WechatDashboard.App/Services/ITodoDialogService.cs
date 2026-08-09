using WechatDashboard.App.ViewModels.Todos;

namespace WechatDashboard.App.Services;

public interface ITodoDialogService
{
    Task ShowAsync(TodoDetailViewModel viewModel);
    bool Confirm(string message, string title);
    void ShowError(string message, string title);
}
