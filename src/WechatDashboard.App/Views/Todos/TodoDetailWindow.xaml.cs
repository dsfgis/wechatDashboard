using System;
using System.Windows;
using WechatDashboard.App.ViewModels.Todos;

namespace WechatDashboard.App.Views.Todos;

public partial class TodoDetailWindow : Window
{
    private readonly TodoDetailViewModel _viewModel;

    public TodoDetailWindow(TodoDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        viewModel.RequestClose += (_, _) => Close();
    }

    /// <summary>复制原始消息正文。</summary>
    private void CopyOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(_viewModel.OriginalContent, "原始消息已复制到剪贴板。");
    }

    /// <summary>复制待办字段信息 + 原始消息正文。</summary>
    private void CopyInfoButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(_viewModel.BuildCopyText(), "待办信息已复制到剪贴板。");
    }

    private void CopyToClipboard(string? text, string successMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _viewModel.SetStatus("没有可复制的内容。");
            return;
        }

        try
        {
            // copy: true 表示关闭窗口后剪贴板内容仍然保留
            System.Windows.Clipboard.SetDataObject(text, true);
            _viewModel.SetStatus(successMessage);
        }
        catch (Exception)
        {
            _viewModel.SetStatus("复制失败，请手动选中文本后按 Ctrl+C。");
        }
    }
}
