using System.ComponentModel;
using System.Diagnostics;
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
    private readonly DispatcherTimer _retractAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _retractAnimationClock = new();
    private System.Windows.Point _retractStart;
    private System.Windows.Point _retractTarget;
    private DesktopCalendarDockEdge _dockEdge;
    private Size? _dockedWindowSize;
    private bool _allowClose;
    private bool _isApplyingPosition;
    private bool _isApplyingSize;
    private bool _hasAppliedSavedState;
    private bool _isRetracted;
    private DesktopCalendarDesktopHost? _desktopHost;
    private System.Windows.Point? _embeddedPosition;
    private bool _isChangingEmbedding;
    private bool _isDarkTheme;
    private bool IsDesktopEmbedded => _desktopHost?.IsEmbedded == true;

    public DesktopCalendarWindow(DesktopCalendarViewModel viewModel, DesktopCalendarStateStore stateStore)
    {
        _viewModel = viewModel;
        _stateStore = stateStore;
        InitializeComponent();
        ApplyTheme(_stateStore.Load().IsDarkTheme);
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
        _retractAnimationTimer.Tick += (_, _) => AdvanceRetraction();
        Closed += (_, _) =>
        {
            // Explorer may destroy its child windows when the shell restarts.
            // MainWindow clears its reference on Closed so the next open recreates it.
            _refreshTimer.Stop();
            _autoHideTimer.Stop();
            _retractAnimationTimer.Stop();
            _desktopHost = null;
            _hasAppliedSavedState = false;
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

        if (!IsDesktopEmbedded) Activate();
        _refreshTimer.Start();
        await _viewModel.RefreshAsync();
    }

    public void Shutdown()
    {
        if (_retractAnimationTimer.IsEnabled) CompleteRetraction();
        _allowClose = true;
        _refreshTimer.Stop();
        _autoHideTimer.Stop();
        SaveState();
        if (IsDesktopEmbedded) _desktopHost!.Detach();
        _hasAppliedSavedState = false; // Closing must not overwrite the saved embedded preference.
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
        ApplyWindowSize(new Size(state.Width, state.Height));
        _desktopHost = new DesktopCalendarDesktopHost(new WindowInteropHelper(this).Handle);
        OpacitySlider.Value = state.Opacity;
        _desktopHost.SetOpacity(state.Opacity);
        _dockEdge = state.DockEdge;
        ApplyInitialPosition(state);
        _dockedWindowSize = _dockEdge == DesktopCalendarDockEdge.None ? null : GetWindowSize();
        SetExpandedVisualState();
        UpdateDockStatus();
        if (state.IsDesktopEmbedded) DesktopEmbedCheckBox.IsChecked = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_retractAnimationTimer.IsEnabled) CompleteRetraction();
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
        if (IsDesktopEmbedded || e.ChangedButton != MouseButton.Left || e.ClickCount != 1 || IsInteractiveTitleBarElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        RevealFromEdge();
        _autoHideTimer.Stop();
        _dockEdge = DesktopCalendarDockEdge.None;
        _dockedWindowSize = null;
        UpdateDockStatus();
        // Windows Aero Snap may resize a borderless window while DragMove is ending.
        // Preserve user-selected dimensions when docking after a title drag.
        var sizeBeforeDrag = GetWindowSize();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        DockToNearestEdge(force: false, sizeBeforeDrag);
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

    private void ResizeGrip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _autoHideTimer.Stop();
        _dockEdge = DesktopCalendarDockEdge.None;
        _dockedWindowSize = null;
        UpdateDockStatus();
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isRetracted) return;
        var area = GetWorkArea();
        var position = _embeddedPosition ?? new System.Windows.Point(Left, Top);
        ApplyWindowSize(new Size(
            Math.Clamp(Width + e.HorizontalChange, MinWidth, Math.Max(MinWidth, area.Right - position.X)),
            Math.Clamp(Height + e.VerticalChange, MinHeight, Math.Max(MinHeight, area.Bottom - position.Y))));
    }

    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e) => SaveState();

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_hasAppliedSavedState || _desktopHost is null) return;
        try
        {
            _desktopHost.SetOpacity(e.NewValue);
        }
        catch (Win32Exception ex)
        {
            DockStatusText.Text = $"透明度调整失败：{ex.Message}";
        }
    }

    private void DesktopEmbedCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_hasAppliedSavedState || _isChangingEmbedding) return;
        _isChangingEmbedding = true;
        string? error = null;
        try
        {
            _autoHideTimer.Stop();
            if (DesktopEmbedCheckBox.IsChecked == true)
            {
                RevealFromEdge();
                var position = new System.Windows.Point(Left, Top);
                _desktopHost ??= new DesktopCalendarDesktopHost(new WindowInteropHelper(this).Handle);
                _desktopHost.Embed();
                _embeddedPosition = position;
                _dockEdge = DesktopCalendarDockEdge.None;
                _dockedWindowSize = null;
                SetExpandedVisualState();
                // 嵌入桌面后重新应用当前透明度：挂载父窗口可能重置分层属性，但保持用户设置的值不变。
                _desktopHost.SetOpacity(OpacitySlider.Value);
            }
            else if (IsDesktopEmbedded)
            {
                _desktopHost!.Detach();
                if (_embeddedPosition is { } position) ApplyPosition(position);
                _embeddedPosition = null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = $"桌面模式切换失败：{ex.Message}";
        }
        finally
        {
            DesktopEmbedCheckBox.IsChecked = IsDesktopEmbedded;
            _isChangingEmbedding = false;
            UpdateDockStatus();
            if (error is not null) DockStatusText.Text = error;
            SaveState();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(!_isDarkTheme);
        SaveState();
    }

    private void ApplyTheme(bool dark)
    {
        _isDarkTheme = dark;
        DesktopCalendarTheme.Apply(Resources, dark);
        CalendarContent.SetDarkTheme(dark);
        ThemeButton.Content = dark ? "浅色" : "暗黑";
        ThemeButton.ToolTip = dark ? "切换为浅色主题" : "切换为暗黑主题";
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) => _autoHideTimer.Stop();

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (IsDesktopEmbedded || _dockEdge == DesktopCalendarDockEdge.None || _isRetracted)
        {
            return;
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        if (!_isChangingEmbedding && !IsDesktopEmbedded && !_isApplyingPosition && !_isRetracted && _dockEdge != DesktopCalendarDockEdge.None)
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
                _dockedWindowSize = null;
                UpdateDockStatus();
            }
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowShape();
        if (_isChangingEmbedding || _isApplyingSize || !_hasAppliedSavedState)
        {
            return;
        }

        var size = GetWindowSize();
        if (_dockEdge != DesktopCalendarDockEdge.None)
        {
            _dockedWindowSize = size;
            ApplyCurrentDockPosition(GetWorkArea());
        }
    }

    private void DockToNearestEdge(bool force, Size? preservedSize = null)
    {
        if (IsDesktopEmbedded) return;
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
            _dockedWindowSize = null;
            UpdateDockStatus();
            return;
        }

        _dockEdge = nearest.Key;
        var size = preservedSize ?? GetWindowSize();
        _dockedWindowSize = size;
        SetExpandedVisualState();
        ApplyWindowSize(size);
        ApplyCurrentDockPosition(area, size);
        UpdateDockStatus();
        SaveState(size);
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void ApplyCurrentDockPosition(Rect area, Size? preservedSize = null)
    {
        var size = preservedSize ?? GetWindowSize();
        var currentPosition = new System.Windows.Point(Left, Top);
        var position = _isRetracted
            ? DesktopCalendarDockLayout.GetRetractedPosition(area, size, currentPosition, _dockEdge)
            : DesktopCalendarDockLayout.GetDockedPosition(area, size, currentPosition, _dockEdge);
        ApplyPosition(position);
    }

    private void ApplyWindowSize(Size size)
    {
        _isApplyingSize = true;
        try
        {
            Width = Math.Max(MinWidth, size.Width);
            Height = Math.Max(MinHeight, size.Height);
        }
        finally
        {
            _isApplyingSize = false;
        }
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
        if (IsDesktopEmbedded || _dockEdge == DesktopCalendarDockEdge.None || _isRetracted || !IsVisible)
        {
            return;
        }

        var size = _dockedWindowSize ?? GetWindowSize();
        SaveState();
        ApplyWindowSize(size);
        _retractStart = new System.Windows.Point(Left, Top);
        _retractTarget = DesktopCalendarDockLayout.GetRetractedPosition(GetWorkArea(), size, _retractStart, _dockEdge);
        _isRetracted = true;
        ResizeGrip.Visibility = Visibility.Collapsed;
        // Keep the complete native window visible until it has slid off screen.
        _retractAnimationClock.Restart();
        _retractAnimationTimer.Start();
    }

    private void AdvanceRetraction()
    {
        var progress = Math.Clamp(_retractAnimationClock.Elapsed.TotalMilliseconds / 250, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        ApplyPosition(new System.Windows.Point(
            _retractStart.X + (_retractTarget.X - _retractStart.X) * eased,
            _retractStart.Y + (_retractTarget.Y - _retractStart.Y) * eased));
        if (progress >= 1) CompleteRetraction();
    }

    private void CompleteRetraction()
    {
        _retractAnimationTimer.Stop();
        _retractAnimationClock.Stop();
        ApplyPosition(_retractTarget);
        CalendarSurface.Visibility = Visibility.Hidden;
        ConfigureRevealHandle();
        EdgeRevealHandle.Visibility = Visibility.Visible;
        UpdateWindowShape();
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

        var size = _dockedWindowSize ?? GetWindowSize();
        _autoHideTimer.Stop();
        SetExpandedVisualState();
        ApplyWindowSize(size);
        ApplyCurrentDockPosition(GetWorkArea(), size);
        UpdateDockStatus();
        Activate();
    }

    private void SetExpandedVisualState()
    {
        _retractAnimationTimer.Stop();
        _retractAnimationClock.Stop();
        _isRetracted = false;
        CalendarSurface.Visibility = Visibility.Visible;
        ResizeGrip.Visibility = Visibility.Visible;
        EdgeRevealHandle.Visibility = Visibility.Collapsed;
        ResizeMode = ResizeMode.NoResize;
        UpdateWindowShape();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(new Action(UpdateWindowShape), DispatcherPriority.Loaded);
    }

    private void UpdateWindowShape()
    {
        if (_desktopHost is null) return;
        var bounds = new Rect(0, 0, Width, Height);
        var radius = CalendarSurface.CornerRadius.TopLeft;
        if (_isRetracted && !_retractAnimationTimer.IsEnabled)
        {
            var width = EdgeRevealHandle.Width;
            var height = EdgeRevealHandle.Height;
            var left = EdgeRevealHandle.HorizontalAlignment switch
            {
                HorizontalAlignment.Right => Width - width,
                HorizontalAlignment.Center => (Width - width) / 2,
                _ => 0
            };
            var top = EdgeRevealHandle.VerticalAlignment switch
            {
                VerticalAlignment.Bottom => Height - height,
                VerticalAlignment.Center => (Height - height) / 2,
                _ => 0
            };
            bounds = new Rect(left, top, width, height);
            radius = EdgeRevealHandle.CornerRadius.TopLeft;
        }
        _desktopHost.SetShape(bounds, radius);
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

    private void SaveState(Size? preservedSize = null)
    {
        if (!_hasAppliedSavedState)
        {
            return;
        }

        var size = preservedSize ?? _dockedWindowSize ?? GetWindowSize();
        var visiblePosition = _embeddedPosition ?? DesktopCalendarDockLayout.GetDockedPosition(
            GetWorkArea(),
            size,
            new System.Windows.Point(Left, Top),
            _dockEdge);
        _stateStore.Save(new DesktopCalendarWindowState(
            visiblePosition.X,
            visiblePosition.Y,
            size.Width,
            size.Height,
            OpacitySlider.Value,
            IsEdgeSnapEnabled: true,
            _dockEdge,
            IsDesktopEmbedded,
            IsDarkTheme: _isDarkTheme));
    }

    private Size GetWindowSize() => new(Width, Height);

    private void UpdateDockStatus()
    {
        if (IsDesktopEmbedded)
        {
            DockStatusText.Text = "已嵌入桌面 · 位置已锁定；拖拽右下角调整大小";
            return;
        }
        DockStatusText.Text = _dockEdge switch
        {
            DesktopCalendarDockEdge.Left => "已停靠左侧",
            DesktopCalendarDockEdge.Top => "已停靠顶部",
            DesktopCalendarDockEdge.Right => "已停靠右侧",
            DesktopCalendarDockEdge.Bottom => "已停靠底部",
            _ => "拖动标题栏移动；拖拽右下角调整大小；停靠后离开 1 秒后动画收起"
        };
    }
}
