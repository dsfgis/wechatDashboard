using System.Windows;
using System.Windows.Controls;

namespace WechatDashboard.App.Behaviors;

/// <summary>把 ViewModel 的 SelectedItem 变化转换成 DataGrid 滚动，不放入窗口代码后置。</summary>
public static class DataGridScrollIntoViewBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(DataGridScrollIntoViewBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not DataGrid grid)
        {
            return;
        }

        if ((bool)args.NewValue)
        {
            grid.SelectionChanged += OnSelectionChanged;
        }
        else
        {
            grid.SelectionChanged -= OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is DataGrid { SelectedItem: not null } grid)
        {
            grid.Dispatcher.BeginInvoke(() => grid.ScrollIntoView(grid.SelectedItem));
        }
    }
}
