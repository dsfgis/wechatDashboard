namespace WechatDashboard.Application.Capture;

/// <summary>关注项目使用的固定可读色板。</summary>
public static class ProjectColorPolicy
{
    public const string DefaultColor = "#4B5563";
    public const string Red = "#DC2626";
    public const string Orange = "#EA580C";
    public const string Green = "#16A34A";
    public const string Blue = "#2563EB";
    public const string Purple = "#7C3AED";
    public const string Pink = "#DB2777";

    public static IReadOnlyList<string> SupportedColors { get; } =
    [DefaultColor, Red, Orange, Green, Blue, Purple, Pink];

    public static string Normalize(string? color)
    {
        var candidate = string.IsNullOrWhiteSpace(color)
            ? DefaultColor
            : color.Trim().ToUpperInvariant();
        if (!SupportedColors.Contains(candidate))
        {
            throw new ArgumentException("项目颜色必须来自预设色板。", nameof(color));
        }

        return candidate;
    }
}
