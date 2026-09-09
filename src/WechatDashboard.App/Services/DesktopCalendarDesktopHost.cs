using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows;

namespace WechatDashboard.App.Services;

/// <summary>将日历作为桌面图标层的同级窗口挂载，不修改 Explorer 的窗口样式。</summary>
public sealed class DesktopCalendarDesktopHost
{
    private const int StyleIndex = -16;
    private const int ChildStyle = 0x40000000;
    private const int PopupStyle = unchecked((int)0x80000000);
    private const int ExtendedStyleIndex = -20;
    private const int LayeredStyle = 0x00080000;
    private const int NoActivateStyle = 0x08000000;
    private const uint NoActivate = 0x10;
    private const uint FrameChanged = 0x20;
    private readonly nint _window;
    private readonly HwndSource _source;
    private int _originalStyle;
    private NativeRect _screenBounds;
    private bool _isChangingParent;
    private bool _usesRedirectedOpacity;
    private nint _desktopZOrderAfter;
    private bool _usesTopLevelDesktopOverlay;
    private bool _originalNoActivate;
    private nint _desktopWindow;

    public void SetShape(Rect bounds, double radius)
    {
        var scale = GetDpiForWindow(_window) / 96.0;
        var region = CreateRoundRectRgn(
            (int)Math.Round(bounds.Left * scale), (int)Math.Round(bounds.Top * scale),
            (int)Math.Round(bounds.Right * scale) + 1, (int)Math.Round(bounds.Bottom * scale) + 1,
            (int)Math.Round(radius * 2 * scale), (int)Math.Round(radius * 2 * scale));
        if (region == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        // Windows owns the region after success; only free it on failure.
        if (SetWindowRgn(_window, region, true) == 0)
        {
            DeleteObject(region);
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public DesktopCalendarDesktopHost(nint window)
    {
        _window = window;
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("日历窗口尚未创建。");
        _source.AddHook(OpacityStyleHook);
    }

    public bool IsEmbedded { get; private set; }

    public void SetOpacity(double opacity)
    {
        // Keep WPF's UsesPerPixelOpacity disabled. SetLayeredWindowAttributes
        // uses redirected painting, which remains visible after desktop parenting.
        var extendedStyle = GetWindowLong(_window, ExtendedStyleIndex);
        _usesRedirectedOpacity = true;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLong(_window, ExtendedStyleIndex, extendedStyle | LayeredStyle);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
        Check(SetLayeredWindowAttributes(_window, 0, (byte)Math.Round(Math.Clamp(opacity, 0.55, 1) * 255), 2));
    }

    private nint OpacityStyleHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_usesRedirectedOpacity && message == 0x007c && wParam == ExtendedStyleIndex)
        {
            // HwndTarget strips WS_EX_LAYERED when UsesPerPixelOpacity is false.
            // Preserve only the native opacity style; keep all other requested bits.
            var styles = Marshal.PtrToStructure<WindowStyles>(lParam);
            styles.NewStyle |= LayeredStyle;
            Marshal.StructureToPtr(styles, lParam, false);
            handled = true;
        }
        return 0;
    }

    public void Embed()
    {
        if (IsEmbedded) return;
        var desktop = FindDesktopHost(out _desktopZOrderAfter);
        if (desktop == 0) throw new InvalidOperationException("未找到 Windows 桌面，请确认资源管理器正在运行。");
        Check(GetWindowRect(_window, out _screenBounds));
        _originalStyle = GetWindowLong(_window, StyleIndex);
        _originalNoActivate = (GetWindowLong(_window, ExtendedStyleIndex) & NoActivateStyle) != 0;
        _desktopWindow = desktop;
        var position = new NativePoint { X = _screenBounds.Left, Y = _screenBounds.Top };
        _usesTopLevelDesktopOverlay = _desktopZOrderAfter != 0;
        if (!_usesTopLevelDesktopOverlay) Check(ScreenToClient(desktop, ref position));
        _isChangingParent = true;
        try
        {
            if (!_usesTopLevelDesktopOverlay)
            {
                SetStyle((_originalStyle & ~PopupStyle) | ChildStyle);
                SetParentChecked(desktop);
            }
            IsEmbedded = true;
            if (_usesTopLevelDesktopOverlay)
                SetWindowLong(_window, ExtendedStyleIndex, GetWindowLong(_window, ExtendedStyleIndex) | NoActivateStyle);
            // A raised desktop has a no-redirection Progman. A normal top-level
            // WPF window placed directly above Progman composes reliably there;
            // direct child HWNDs can receive input while contributing no pixels.
            var insertAfter = _usesTopLevelDesktopOverlay ? GetDesktopInsertAfter() : (nint)0;
            var x = _usesTopLevelDesktopOverlay ? _screenBounds.Left : position.X;
            var y = _usesTopLevelDesktopOverlay ? _screenBounds.Top : position.Y;
            Check(SetWindowPos(_window, insertAfter, x, y,
                _screenBounds.Right - _screenBounds.Left, _screenBounds.Bottom - _screenBounds.Top,
                NoActivate | FrameChanged));
            if (!_usesTopLevelDesktopOverlay)
            {
                Check(SetWindowPos(_window, 0, 0, 0, 0, 0,
                    NoActivate | FrameChanged | 0x0001 | 0x0002));
            }
            // Hardware surfaces from a WPF top-level window can remain detached
            // from the raised desktop's redirected composition surface after
            // SetParent. Force software rendering before the first child paint.
            _source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            (_source.RootVisual as UIElement)?.InvalidateVisual();
            ShowWindow(_window, 4); // SW_SHOWNOACTIVATE: make the newly reparented child drawable.
            UpdateWindow(_window);
            RedrawWindow(_window, 0, 0, 0x0100 | 0x0200 | 0x0400 | 0x0800);
            _source.AddHook(WindowHook);
        }
        catch
        {
            RestoreActivationStyle();
            if (!_usesTopLevelDesktopOverlay)
            {
                SetParentChecked(0);
                SetStyle(_originalStyle);
            }
            IsEmbedded = false;
            _usesTopLevelDesktopOverlay = false;
            Check(SetWindowPos(_window, 0, _screenBounds.Left, _screenBounds.Top,
                _screenBounds.Right - _screenBounds.Left, _screenBounds.Bottom - _screenBounds.Top,
                NoActivate | FrameChanged));
            throw;
        }
        finally { _isChangingParent = false; }
    }

    public void Detach()
    {
        if (!IsEmbedded) return;
        // Resizing while embedded must survive the return to a floating window.
        Check(GetWindowRect(_window, out _screenBounds));
        _isChangingParent = true;
        try
        {
            if (!_usesTopLevelDesktopOverlay)
            {
                SetParentChecked(0);
                SetStyle(_originalStyle);
            }
            IsEmbedded = false;
            RestoreActivationStyle();
            _usesTopLevelDesktopOverlay = false;
            _source.CompositionTarget.RenderMode = RenderMode.Default;
            _source.RemoveHook(WindowHook);
            Check(SetWindowPos(_window, 0, _screenBounds.Left, _screenBounds.Top,
                _screenBounds.Right - _screenBounds.Left, _screenBounds.Bottom - _screenBounds.Top,
                NoActivate | FrameChanged));
        }
        finally { _isChangingParent = false; }
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!IsEmbedded || _isChangingParent) return 0;
        if (_usesTopLevelDesktopOverlay && message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE: deliver the click without raising the widget.
        }
        if (message == 0x0112 && ((long)wParam & 0xfff0) is 0xf010 or 0xf000)
        {
            // SC_MOVE / SC_SIZE, including keyboard/system-menu requests.
            handled = true;
        }
        else if (message == 0x0046)
        {
            // WM_WINDOWPOSCHANGING: also block native movement and Aero Snap.
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.Flags |= 0x0002 | NoActivate;
            if (_usesTopLevelDesktopOverlay && (position.Flags & 0x0004) == 0)
                position.InsertAfter = GetDesktopInsertAfter();
            Marshal.StructureToPtr(position, lParam, false);
        }
        return 0;
    }

    private void RestoreActivationStyle()
    {
        if (!_usesTopLevelDesktopOverlay) return;
        var style = GetWindowLong(_window, ExtendedStyleIndex);
        SetWindowLong(_window, ExtendedStyleIndex,
            _originalNoActivate ? style | NoActivateStyle : style & ~NoActivateStyle);
    }

    private nint GetDesktopInsertAfter()
    {
        // Keep the overlay below every visible application, including inactive
        // windows. Merely using SWP_NOACTIVATE does not prevent a z-order raise.
        nint lowestApplication = 0;
        EnumWindows((window, _) =>
        {
            if (window == _window || !IsWindowVisible(window)
                || (GetWindowLong(window, ExtendedStyleIndex) & 0x00000008) != 0) return true;
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"))
                lowestApplication = window;
            return true;
        }, 0);
        return lowestApplication != 0 ? lowestApplication : _desktopWindow;
    }

    private nint FindDesktopHost(out nint zOrderAfter)
    {
        nint foundZOrderAfter = 0;
        var progman = FindWindow("Progman", null);
        if (progman == 0)
        {
            zOrderAfter = 0;
            return 0;
        }

        var raisedDesktop = (GetWindowLong(progman, -20) & 0x00200000) != 0;
        SendMessageTimeout(progman, 0x052C, 0xD, 1, 2, 1000, out _);
        nint result = 0;
        EnumWindows((window, _) =>
        {
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW")
            {
                var shellView = FindWindowEx(window, 0, "SHELLDLL_DefView", null);
                if (shellView != 0 && IsWindowVisible(window) && IsWindowVisible(shellView))
                {
                    if (raisedDesktop && window == progman)
                    {
                        result = window;
                        foundZOrderAfter = shellView;
                        return false;
                    }

                    var worker = FindWindowEx(IntPtr.Zero, window, "WorkerW", null);
                    if (worker != 0)
                    {
                        result = worker;
                        return false;
                    }

                    result = FindWindowEx(shellView, 0, "SysListView32", null);
                    return result == 0;
                }
            }
            return true;
        }, 0);
        zOrderAfter = foundZOrderAfter;
        return result;
    }

    private void SetParentChecked(nint parent)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetParent(_window, parent);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
        var actualParent = GetAncestor(_window, 1); // GA_PARENT excludes an owned popup's owner.
        var expectedParent = parent == 0 ? GetDesktopWindow() : parent;
        if (actualParent != expectedParent) throw new InvalidOperationException("Windows 未能切换日历的桌面宿主。");
    }

    private void SetStyle(int style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLong(_window, StyleIndex, style);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowStyles { public int OldStyle, NewStyle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RedrawWindow(nint window, nint updateRect, nint updateRegion, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
