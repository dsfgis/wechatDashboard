using System.Runtime.InteropServices;

internal static class DesktopCalendarNativeAssertions
{
    [StructLayout(LayoutKind.Sequential)]
    internal record struct Bounds(int Left, int Top, int Right, int Bottom);

    internal static Bounds GetBounds(nint window)
    {
        if (!GetWindowRect(window, out var result)) throw new InvalidOperationException("Cannot read calendar bounds.");
        return result;
    }

    internal static void AssertRoundedRegion(nint window)
    {
        var bounds = GetBounds(window);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(window, region) == 0
                || PtInRegion(region, 0, 0) || PtInRegion(region, width - 1, 0)
                || PtInRegion(region, 0, height - 1) || PtInRegion(region, width - 1, height - 1)
                || !PtInRegion(region, width / 2, height / 2)
                || !PtInRegion(region, width / 2, height - 2))
                throw new InvalidOperationException("The native region must cut all four corners and cover the current resized surface.");
        }
        finally { DeleteObject(region); }
    }

    internal static void AssertRetractedRegion(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(window, region) == 0) throw new InvalidOperationException("Missing reveal handle region.");
            GetRgnBox(region, out var bounds);
            var scale = GetDpiForWindow(window) / 96.0;
            if (bounds.Right - bounds.Left > 105 * scale || bounds.Bottom - bounds.Top > 105 * scale)
                throw new InvalidOperationException("Retraction must expose only the small handle, not the full window background.");
        }
        finally { DeleteObject(region); }
    }

    internal static void AssertStaysBelowOtherWindows(nint window)
    {
        if ((GetWindowLong(window, -16) & 0x40000000) != 0) return;
        var other = new System.Windows.Window
        {
            Title = "日历层级验证：此窗口应始终在日历上方",
            Width = 480, Height = 220, Left = 180, Top = 180,
            Background = System.Windows.Media.Brushes.DarkSlateBlue,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "其他应用窗口\n应完整覆盖日历，点击日历也不能抢到前面",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 20, Margin = new System.Windows.Thickness(24)
            }
        };
        try
        {
            other.Show();
            var otherHandle = new System.Windows.Interop.WindowInteropHelper(other).Handle;
            if (SendMessage(window, 0x0021, otherHandle, (nint)0x02010001) != 3)
                throw new InvalidOperationException("Embedded mouse input must not activate the calendar.");
            foreach (var target in new nint[] { 0, -1 })
            {
                SetWindowPos(window, target, 0, 0, 0, 0, 0x0003);
                var foundOther = false;
                for (var above = GetWindow(window, 3); above != 0; above = GetWindow(above, 3))
                    if (above == otherHandle) { foundOther = true; break; }
                if (!foundOther || (GetWindowLong(window, -20) & 0x00000008) != 0)
                    throw new InvalidOperationException("Even a raise/topmost request must leave the embedded calendar below other applications.");
            }
        }
        finally { other.Close(); }
    }

    internal static void AssertEmbeddedAndImmovable(nint window, Bounds expected)
    {
        var parent = GetParent(window);
        var shellView = GetParent(parent);
        var isLegacyIconList = parent != 0 && shellView != 0
            && FindWindowEx(shellView, 0, "SysListView32", null) == parent
            && FindWindowEx(GetParent(shellView), 0, "SHELLDLL_DefView", null) == shellView;
        var isRaisedDesktop = parent != 0 && GetClassName(parent) == "Progman"
            && FindWindowEx(parent, 0, "SHELLDLL_DefView", null) != 0;
        var isTopLevelDesktopOverlay = parent == 0 && (GetWindowLong(window, -16) & 0x40000000) == 0;
        if (!isLegacyIconList && !isRaisedDesktop && !isTopLevelDesktopOverlay)
            throw new InvalidOperationException("Calendar must be a real child of the desktop list's redirected drawing surface.");
        if (!isRaisedDesktop && (GetWindowLong(parent, -20) & 0x00200000) != 0)
            throw new InvalidOperationException("A layered calendar must not be attached directly to a no-redirection desktop host.");
        if (!isTopLevelDesktopOverlay && (GetWindowLong(window, -16) & 0x40000000) == 0)
            throw new InvalidOperationException("Embedded calendar must have WS_CHILD style.");
        if (GetBounds(window) != expected) throw new InvalidOperationException("Embedding changed the screen position or dimensions.");
        SetWindowPos(window, 0, 17, 19, 0, 0, 0x0015); // move without size/z-order/activation changes
        if (GetBounds(window) != expected) throw new InvalidOperationException("Native movement was not blocked.");
    }

    internal static void AssertRedirectedOpacity(nint window, double expected)
    {
        if (!GetLayeredWindowAttributes(window, out _, out var alpha, out var flags)
            || (flags & 2) == 0 || alpha != (byte)Math.Round(expected * 255))
            throw new InvalidOperationException("Calendar opacity must use redirected painting, not WPF per-pixel alpha.");
    }

    internal static void AssertDetached(nint window, Bounds expected)
    {
        if (GetAncestor(window, 2) != window || (GetWindowLong(window, -16) & 0x40000000) != 0)
            throw new InvalidOperationException("Detaching must restore a top-level calendar window.");
        if (GetBounds(window) != expected) throw new InvalidOperationException("Detaching changed the screen position or dimensions.");
        SetWindowPos(window, 0, expected.Left + 10, expected.Top + 10, 0, 0, 0x0015);
        if (GetBounds(window).Left != expected.Left + 10) throw new InvalidOperationException("Movement was not restored.");
    }

    /// <summary>窗口当前是否处于最小化（iconic）状态。</summary>
    internal static bool IsIconic(nint window) => IsIconicNative(window);

    /// <summary>窗口当前是否可见（未被最小化/隐藏）。</summary>
    internal static bool IsWindowVisible(nint window) => IsWindowVisibleNative(window);

    /// <summary>
    /// 图标层窗口（Progman / 承载 SHELLDLL_DefView 的 WorkerW）是否位于指定窗口之上。
    /// 「显示桌面」/Win+D 的真实机制是把图标层抬高到所有窗口之上，而非最小化窗口。
    /// </summary>
    internal static bool HasIconLayerAbove(nint window)
    {
        for (var above = GetWindow(window, 3); above != 0; above = GetWindow(above, 3))
        {
            var name = new System.Text.StringBuilder(256);
            GetClassName(above, name, name.Capacity);
            var className = name.ToString();
            if (className == "Progman"
                || (className == "WorkerW" && FindWindowEx(above, 0, "SHELLDLL_DefView", null) != 0))
                return true;
        }
        return false;
    }

    /// <summary>注入真实的 Win+D 按键序列（与用户手按/任务栏「显示桌面」按钮走同一条壳层路径）。</summary>
    internal static void SendWinD()
    {
        var inputs = new INPUT[4];
        inputs[0].type = 1; inputs[0].u.ki.wVk = 0x5B;                                  // LWIN down
        inputs[1].type = 1; inputs[1].u.ki.wVk = 0x44;                                 // D down
        inputs[2].type = 1; inputs[2].u.ki.wVk = 0x44; inputs[2].u.ki.dwFlags = 0x0002; // D up
        inputs[3].type = 1; inputs[3].u.ki.wVk = 0x5B; inputs[3].u.ki.dwFlags = 0x0002; // LWIN up
        if (SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>()) != 4)
            throw new InvalidOperationException("Failed to inject the Win+D key sequence.");
    }

    /// <summary>
    /// 在高频采样下执行 <paramref name="action"/>，返回图标层连续盖住日历的最长时长（毫秒）。
    /// 一帧约 16ms：该值只要接近一帧，用户就会看到「先消失再弹回」的闪烁；
    /// 因此它必须远小于一帧，而不是像轮询实现那样达到几百毫秒。
    /// </summary>
    internal static long MeasureLongestIconLayerCoverage(nint window, Action action)
    {
        var samples = new List<(long Elapsed, bool Covered)>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var running = true;
        var sampler = new System.Threading.Thread(() =>
        {
            while (Volatile.Read(ref running))
                samples.Add((clock.ElapsedMilliseconds, HasIconLayerAbove(window)));
        }) { IsBackground = true };

        sampler.Start();
        try
        {
            action();
        }
        finally
        {
            Volatile.Write(ref running, false);
            sampler.Join(2000);
        }

        if (samples.Count == 0) throw new InvalidOperationException("No Z-order samples were collected.");
        long longest = 0;
        long? runStart = null;
        long lastCovered = 0;
        foreach (var sample in samples)
        {
            if (sample.Covered)
            {
                runStart ??= sample.Elapsed;
                lastCovered = sample.Elapsed;
            }
            else if (runStart is { } startedAt)
            {
                longest = Math.Max(longest, lastCovered - startedAt);
                runStart = null;
            }
        }

        if (runStart is { } openStart) longest = Math.Max(longest, lastCovered - openStart);
        return longest;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int count);
    private static string GetClassName(nint window)
    {
        var name = new System.Text.StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "IsIconic")] private static extern bool IsIconicNative(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsWindowVisibleNative(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Bounds bounds);
}
