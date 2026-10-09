using System.Windows;

namespace WechatDashboard.App.Services;

/// <summary>计算桌面日历完整停靠和边缘缩进时的位置，不改变窗口尺寸。</summary>
public static class DesktopCalendarDockLayout
{
    public const double RevealThickness = 18;
    public const double RevealLength = 104;
    public static TimeSpan AutoHideDelay { get; } = TimeSpan.FromSeconds(1);

    public static Point GetDockedPosition(
        Rect workArea,
        Size windowSize,
        Point currentPosition,
        DesktopCalendarDockEdge edge)
    {
        var maxLeft = Math.Max(workArea.Left, workArea.Right - windowSize.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - windowSize.Height);
        var left = Math.Clamp(currentPosition.X, workArea.Left, maxLeft);
        var top = Math.Clamp(currentPosition.Y, workArea.Top, maxTop);

        return edge switch
        {
            DesktopCalendarDockEdge.Left => new Point(workArea.Left, top),
            DesktopCalendarDockEdge.Top => new Point(left, workArea.Top),
            DesktopCalendarDockEdge.Right => new Point(workArea.Right - windowSize.Width, top),
            DesktopCalendarDockEdge.Bottom => new Point(left, workArea.Bottom - windowSize.Height),
            _ => new Point(left, top)
        };
    }

    public static Point GetRetractedPosition(
        Rect workArea,
        Size windowSize,
        Point currentPosition,
        DesktopCalendarDockEdge edge)
    {
        var docked = GetDockedPosition(workArea, windowSize, currentPosition, edge);
        return edge switch
        {
            DesktopCalendarDockEdge.Left => new Point(workArea.Left - windowSize.Width + RevealThickness, docked.Y),
            DesktopCalendarDockEdge.Top => new Point(docked.X, workArea.Top - windowSize.Height + RevealThickness),
            DesktopCalendarDockEdge.Right => new Point(workArea.Right - RevealThickness, docked.Y),
            DesktopCalendarDockEdge.Bottom => new Point(docked.X, workArea.Bottom - RevealThickness),
            _ => docked
        };
    }
}
