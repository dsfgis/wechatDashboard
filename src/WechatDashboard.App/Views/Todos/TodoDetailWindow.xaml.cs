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
        // 状态下拉改了但没点“保存待办”就关窗时，只补交状态并刷新待办/日历；
        // 其余编辑中字段保持未保存。方法内部已捕获全部异常，可安全 fire-and-forget。
        Closed += (_, _) => _ = viewModel.SaveStatusIfChangedAsync();
    }
}
