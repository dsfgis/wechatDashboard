using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace WechatDashboard.App.Services;

/// <summary>将可交互日历作为桌面图标宿主的子窗口挂载，不修改 Explorer 的窗口样式。</summary>
public sealed class DesktopCalendarDesktopHost
{
    private const int StyleIndex = -16;
    private const int ChildStyle = 0x40000000;
    private const int PopupStyle = unchecked((int)0x80000000);
    private const uint NoActivate = 0x10;
    private const uint FrameChanged = 0x20;
    private readonly nint _window;
    private readonly HwndSource _source;
    private int _originalStyle;
    private NativeRect _screenBounds;
    private bool _isChangingParent;

    public DesktopCalendarDesktopHost(nint window)
    {
        _window = window;
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("日历窗口尚未创建。");
    }

    public bool IsEmbedded { get; private set; }

    public void Embed()
    {
        if (IsEmbedded) return;
        var desktop = FindDesktopHost();
        if (desktop == 0) throw new InvalidOperationException("未找到 Windows 桌面，请确认资源管理器正在运行。");
        Check(GetWindowRect(_window, out _screenBounds));
        _originalStyle = GetWindowLong(_window, StyleIndex);
        var position = new NativePoint { X = _screenBounds.Left, Y = _screenBounds.Top };
        Check(ScreenToClient(desktop, ref position));
        _isChangingParent = true;
        try
        {
            SetStyle((_originalStyle & ~PopupStyle) | ChildStyle);
            SetParentChecked(desktop);
            IsEmbedded = true;
            // Above the icon view within its host: the calendar remains clickable,
            // while ordinary top-level application windows can still cover it.
            Check(SetWindowPos(_window, 0, position.X, position.Y,
                _screenBounds.Right - _screenBounds.Left, _screenBounds.Bottom - _screenBounds.Top,
                NoActivate | FrameChanged));
            _source.AddHook(WindowHook);
        }
        catch
        {
            SetParentChecked(0);
            SetStyle(_originalStyle);
            IsEmbedded = false;
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
        _isChangingParent = true;
        try
        {
            SetParentChecked(0);
            SetStyle(_originalStyle);
            IsEmbedded = false;
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
        if (message == 0x0112 && ((long)wParam & 0xfff0) is 0xf010 or 0xf000)
        {
            // SC_MOVE / SC_SIZE, including keyboard/system-menu requests.
            handled = true;
        }
        else if (message == 0x0046)
        {
            // WM_WINDOWPOSCHANGING: also block native movement and Aero Snap.
            var position = Marshal.PtrToStructure<WindowPosition>(lParam);
            position.Flags |= 0x0002; // SWP_NOMOVE; visibility and z-order still work.
            Marshal.StructureToPtr(position, lParam, false);
        }
        return 0;
    }

    private static nint FindDesktopHost()
    {
        nint result = 0;
        EnumWindows((window, _) =>
        {
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW"
                && FindWindowEx(window, 0, "SHELLDLL_DefView", null) != 0)
            {
                result = window;
                return false;
            }
            return true;
        }, 0);
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
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ScreenToClient(nint window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
