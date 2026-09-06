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

    internal static void AssertEmbeddedAndImmovable(nint window, Bounds expected)
    {
        var parent = GetParent(window);
        if (parent == 0 || FindWindowEx(parent, 0, "SHELLDLL_DefView", null) == 0)
            throw new InvalidOperationException("Calendar must be a real child of the desktop icon host.");
        if ((GetWindowLong(window, -16) & 0x40000000) == 0)
            throw new InvalidOperationException("Embedded calendar must have WS_CHILD style.");
        if (GetBounds(window) != expected) throw new InvalidOperationException("Embedding changed the screen position or dimensions.");
        SetWindowPos(window, 0, 17, 19, 0, 0, 0x0015); // move without size/z-order/activation changes
        if (GetBounds(window) != expected) throw new InvalidOperationException("Native movement was not blocked.");
    }

    internal static void AssertDetached(nint window, Bounds expected)
    {
        if (GetAncestor(window, 2) != window || (GetWindowLong(window, -16) & 0x40000000) != 0)
            throw new InvalidOperationException("Detaching must restore a top-level calendar window.");
        if (GetBounds(window) != expected) throw new InvalidOperationException("Detaching changed the screen position or dimensions.");
        SetWindowPos(window, 0, expected.Left + 10, expected.Top + 10, 0, 0, 0x0015);
        if (GetBounds(window).Left != expected.Left + 10) throw new InvalidOperationException("Movement was not restored.");
    }

    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
