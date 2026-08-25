namespace WechatDashboard.Application.Todos;

/// <summary>待办分组可选颜色；使用适合白色背景文字的固定色板。</summary>
public static class TodoGroupColorPolicy
{
    public const string DefaultColor = "#4B5563";
    public const string Red = "#DC2626";
    public const string Orange = "#C2410C";
    public const string Green = "#15803D";
    public const string Blue = "#2563EB";
    public const string Purple = "#7C3AED";
    public const string Pink = "#BE185D";

    private static readonly HashSet<string> SupportedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        DefaultColor,
        Red,
        Orange,
        Green,
        Blue,
        Purple,
        Pink
    };

    public static string Normalize(string? color)
    {
        var candidate = string.IsNullOrWhiteSpace(color) ? DefaultColor : color.Trim();
        if (!SupportedColors.Contains(candidate))
        {
            throw new ArgumentException("请选择列表中的分组颜色。", nameof(color));
        }

        return candidate.ToUpperInvariant();
    }
}
