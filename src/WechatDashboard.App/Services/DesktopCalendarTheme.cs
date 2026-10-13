using System.Windows;
using System.Windows.Media;

namespace WechatDashboard.App.Services;

/// <summary>仅作用于日历资源，不改变主窗口或系统主题。</summary>
public static class DesktopCalendarTheme
{
    public static void Apply(ResourceDictionary resources, bool dark)
    {
        (string Key, string Light, string Dark)[] colors =
        [
            ("Canvas", "#F8FAFC", "#111827"),
            ("Surface", "#FFFFFF", "#1B2535"),
            ("MutedSurface", "#F1F5F9", "#151E2D"),
            ("Border", "#E2E8F0", "#334155"),
            ("OuterBorder", "#CBD5E1", "#475569"),
            ("CellBorder", "#EDF1F5", "#293548"),
            ("Heading", "#0F172A", "#F1F5F9"),
            ("Text", "#334155", "#D5DEEB"),
            ("StrongText", "#1E293B", "#E2E8F0"),
            ("SecondaryText", "#475569", "#B8C6D9"),
            ("MutedText", "#64748B", "#A5B4C8"),
            ("DisabledText", "#94A3B8", "#8899AE"),
            ("Selected", "#EFF6FF", "#213958"),
            ("Hover", "#DBEAFE", "#2D4668"),
            ("AccentBorder", "#93C5FD", "#74AEF8"),
            ("SelectedBorder", "#3B82F6", "#60A5FA"),
            ("HoverBorder", "#60A5FA", "#93C5FD"),
            ("Accent", "#2563EB", "#2563EB"),
            ("AccentText", "#2563EB", "#93C5FD"),
            ("Weekend", "#B45309", "#FBBF77"),
            ("RevealBorder", "#BFDBFE", "#60A5FA"),
            ("ScrollThumb", "#C2CAD6", "#4A5872"),
            ("ScrollThumbHover", "#9BA6B6", "#63728F"),
            ("ScrollThumbPressed", "#7D8998", "#8291AC")
        ];
        foreach (var (key, light, night) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            resources["Calendar" + key] = brush;
        }
    }
}
