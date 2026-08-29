using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WechatDashboard.App.Services;
using WechatDashboard.App.ViewModels.Todos;

namespace WechatDashboard.App.Views.Todos;

public partial class DesktopCalendarWindow : Window
{
    private const double SnapThreshold = 32;
    private readonly DesktopCalendarViewModel _viewModel;
    private readonly DesktopCalendarStateStore _stateStore;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private DesktopCalendarDockEdge _dockEdge;
    private bool _allowClose;
    private bool _isApplyingPosition;
    private bool _hasAppliedSavedState;
    private bool _isRetracted;

    public DesktopCalendarWindow(DesktopCalendarViewModel viewModel, DesktopCalendarStateStore stateStore)
    {
        _viewModel = viewModel;
        _stateStore = stateStore;
        InitializeComponent();
        DataContext = viewModel;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) => await _viewModel.RefreshAsync();
        _autoHideTimer = new DispatcherTimer { Interval = DesktopCalendarDockLayout.AutoHideDelay };
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            if (!IsMouseOver)
            {
                RetractToEdge();
            }
        };
    }

    public async Task ShowWidgetAsync()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (_isRetracted)
        {
            RevealFromEdge();
        }

        Activate();
        _refreshTimer.Start();
        await _viewModel.RefreshAsync();
    }

    public void Shutdown()
    {
        _allowClose = true;
        _refreshTimer.Stop();
        _autoHideTimer.Stop();
        SaveState();
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_hasAppliedSavedState)
        {
            return;
        }

        _hasAppliedSavedState = true;
        var state = _stateStore.Load();
        Width = state.Width;
        Height = state.Height;
        Opacity = state.Opacity;
        EdgeSnapCheckBox.IsChecked = state.IsEdgeSnapEnabled;
        _dockEdge = state.DockEdge;
        ApplyInitialPosition(state);
        SetExpandedVisualState();
        UpdateDockStatus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SaveState();
        _refreshTimer.Stop();
        _autoHideTimer.Stop();
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || IsInteractiveTitleBarElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        _autoHideTimer.Stop();
        _dockEdge = DesktopCalendarDockEdge.None;
        UpdateDockStatus();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (EdgeSnapCheckBox.IsChecked == true)
        {
            DockToNearestEdge(force: false);
        }
        else
        {
            UpdateDockStatus();
        }
    }

    private bool IsInteractiveTitleBarElement(DependencyObject? source)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, TitleBar))
        {
            if (current is ButtonBase or Slider)
            {
                return true;
            }

            current = current is FrameworkContentElement contentElement
                ? contentElement.Parent
                : VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void DockButton_Click(object sender, RoutedEventArgs e) => DockToNearestEdge(force: true);

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        SaveState();
        _refreshTimer.Stop();
        _autoHideTimer.Stop();
        Hide();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => _autoHideTimer.Stop();

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dockEdge == DesktopCalendarDockEdge.None || _isRetracted)
        {
            return;
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (!_isApplyingPosition && !_isRetracted && _dockEdge != DesktopCalendarDockEdge.None)
        {
            var area = GetWorkArea();
            var remainsDocked = _dockEdge switch
            {
                DesktopCalendarDockEdge.Left => Math.Abs(Left - area.Left) < 2,
                DesktopCalendarDockEdge.Top => Math.Abs(Top - area.Top) < 2,
                DesktopCalendarDockEdge.Right => Math.Abs(Left + ActualWidth - area.Right) < 2,
                DesktopCalendarDockEdge.Bottom => Math.Abs(Top + ActualHeight - area.Bottom) < 2,
                _ => false
            };
            if (!remainsDocked)
            {
                _dockEdge = DesktopCalendarDockEdge.None;
                UpdateDockStatus();
            }
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hasAppliedSavedState && _dockEdge != DesktopCalendarDockEdge.None)
        {
            ApplyCurrentDockPosition(GetWorkArea());
        }
    }

    private void DockToNearestEdge(bool force)
    {
        var area = GetWorkArea();
        var distances = new Dictionary<DesktopCalendarDockEdge, double>
        {
            [DesktopCalendarDockEdge.Left] = Math.Abs(Left - area.Left),
            [DesktopCalendarDockEdge.Top] = Math.Abs(Top - area.Top),
            [DesktopCalendarDockEdge.Right] = Math.Abs(Left + ActualWidth - area.Right),
            [DesktopCalendarDockEdge.Bottom] = Math.Abs(Top + ActualHeight - area.Bottom)
        };
        var nearest = distances.MinBy(entry => entry.Value);
        if (!force && nearest.Value > SnapThreshold)
        {
            _dockEdge = DesktopCalendarDockEdge.None;
            UpdateDockStatus();
            return;
        }

        _dockEdge = nearest.Key;
        SetExpandedVisualState();
        ApplyCurrentDockPosition(area);
        UpdateDockStatus();
        SaveState();
    }

    private void ApplyCurrentDockPosition(Rect area)
    {
        var size = GetWindowSize();
        var currentPosition = new System.Windows.Point(Left, Top);
        var position = _isRetracted
            ? DesktopCalendarDockLayout.GetRetractedPosition(area, size, currentPosition, _dockEdge)
            : DesktopCalendarDockLayout.GetDockedPosition(area, size, currentPosition, _dockEdge);
        ApplyPosition(position);
    }

    private void ApplyPosition(System.Windows.Point position)
    {
        _isApplyingPosition = true;
        try
        {
            Left = position.X;
            Top = position.Y;
        }
        finally
        {
            _isApplyingPosition = false;
        }
    }

    private void ApplyInitialPosition(DesktopCalendarWindowState state)
    {
        var area = GetWorkArea();
        var initialPosition = new System.Windows.Point(
            state.Left ?? Math.Max(area.Left, area.Right - Width - 28),
            state.Top ?? Math.Max(area.Top, area.Bottom - Height - 28));
        var visiblePosition = DesktopCalendarDockLayout.GetDockedPosition(
            area,
            GetWindowSize(),
            initialPosition,
            _dockEdge);
        ApplyPosition(visiblePosition);

        if (_dockEdge != DesktopCalendarDockEdge.None)
        {
            ApplyCurrentDockPosition(area);
        }
    }

    private void RetractToEdge()
    {
        if (_dockEdge == DesktopCalendarDockEdge.None || _isRetracted || !IsVisible)
        {
            return;
        }

        SaveState();
        _isRetracted = true;
        CalendarSurface.Visibility = Visibility.Hidden;
        ConfigureRevealHandle();
        EdgeRevealHandle.Visibility = Visibility.Visible;
        ResizeMode = ResizeMode.NoResize;
        ApplyCurrentDockPosition(GetWorkArea());
    }

    private void EdgeRevealHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        RevealFromEdge();
    }

    private void RevealFromEdge()
    {
        if (!_isRetracted)
        {
            return;
        }

        _autoHideTimer.Stop();
        SetExpandedVisualState();
        ApplyCurrentDockPosition(GetWorkArea());
        UpdateDockStatus();
        Activate();
    }

    private void SetExpandedVisualState()
    {
        _isRetracted = false;
        CalendarSurface.Visibility = Visibility.Visible;
        EdgeRevealHandle.Visibility = Visibility.Collapsed;
        ResizeMode = ResizeMode.CanResizeWithGrip;
    }

    private void ConfigureRevealHandle()
    {
        var vertical = _dockEdge is DesktopCalendarDockEdge.Left or DesktopCalendarDockEdge.Right;
        EdgeRevealHandle.Width = vertical
            ? DesktopCalendarDockLayout.RevealThickness
            : DesktopCalendarDockLayout.RevealLength;
        EdgeRevealHandle.Height = vertical
            ? DesktopCalendarDockLayout.RevealLength
            : DesktopCalendarDockLayout.RevealThickness;
        EdgeRevealHandle.HorizontalAlignment = _dockEdge switch
        {
            DesktopCalendarDockEdge.Left => HorizontalAlignment.Right,
            DesktopCalendarDockEdge.Right => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Center
        };
        EdgeRevealHandle.VerticalAlignment = _dockEdge switch
        {
            DesktopCalendarDockEdge.Top => VerticalAlignment.Bottom,
            DesktopCalendarDockEdge.Bottom => VerticalAlignment.Top,
            _ => VerticalAlignment.Center
        };
        EdgeRevealGlyph.Text = _dockEdge switch
        {
            DesktopCalendarDockEdge.Left => "›",
            DesktopCalendarDockEdge.Top => "⌄",
            DesktopCalendarDockEdge.Right => "‹",
            DesktopCalendarDockEdge.Bottom => "⌃",
            _ => "·"
        };
    }

    private Rect GetWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var workingArea = Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(workingArea.Left, workingArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(workingArea.Right, workingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void SaveState()
    {
        if (!_hasAppliedSavedState)
        {
            return;
        }

        var size = GetWindowSize();
        var visiblePosition = DesktopCalendarDockLayout.GetDockedPosition(
            GetWorkArea(),
            size,
            new System.Windows.Point(Left, Top),
            _dockEdge);
        _stateStore.Save(new DesktopCalendarWindowState(
            visiblePosition.X,
            visiblePosition.Y,
            size.Width,
            size.Height,
            Opacity,
            EdgeSnapCheckBox.IsChecked == true,
            _dockEdge));
    }

    private Size GetWindowSize() => new(
        ActualWidth > 0 ? ActualWidth : Width,
        ActualHeight > 0 ? ActualHeight : Height);

    private void UpdateDockStatus()
    {
        DockStatusText.Text = _dockEdge switch
        {
            DesktopCalendarDockEdge.Left => "已停靠左侧",
            DesktopCalendarDockEdge.Top => "已停靠顶部",
            DesktopCalendarDockEdge.Right => "已停靠右侧",
            DesktopCalendarDockEdge.Bottom => "已停靠底部",
            _ => "未停靠"
        };
    }
}
