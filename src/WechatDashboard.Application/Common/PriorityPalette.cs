using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Application.Common;

/// <summary>一个优先级层级的显示定义：层级、代码文本（P0…）与颜色。</summary>
/// <param name="Level">层级枚举值，P0 最高。</param>
/// <param name="Code">显示代码，等于枚举名，如 P0。</param>
/// <param name="Color">该层级的十六进制颜色，如 #D92D20。</param>
public sealed record PriorityLevelDefinition(PriorityLevel Level, string Code, string Color);

/// <summary>
/// 优先级的运行时色板：层数（2–6 级）与每级颜色由“优先级设置”决定，
/// 消息流、待办列表、日程卡片共用同一份定义，保证同一层级全局只有一个颜色。
/// 实例不可变，配置变更时整体替换（见 <see cref="PrioritySettings"/>）。
/// </summary>
public sealed class PriorityPalette
{
    /// <summary>允许配置的最少层数。</summary>
    public const int MinLevelCount = 2;

    /// <summary>允许配置的最多层数，等于 <see cref="PriorityLevel"/> 的取值个数。</summary>
    public const int MaxLevelCount = 6;

    /// <summary>默认层数，与历史版本一致（P0–P3）。</summary>
    public const int DefaultLevelCount = 4;

    /// <summary>
    /// 默认色板。前四项与历史版本 <see cref="PriorityColorPolicy"/> 的常量完全一致，
    /// 未做任何配置时界面颜色与旧版行为相同。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultColors = new[]
    {
        "#D92D20",
        "#B54708",
        "#3B5998",
        "#475467",
        "#7C3AED",
        "#0E7490"
    };

    private readonly string[] _colors;
    private readonly PriorityLevelDefinition[] _levels;

    private PriorityPalette(string[] colors)
    {
        _colors = colors;
        _levels = colors
            .Select((color, index) => new PriorityLevelDefinition((PriorityLevel)index, ((PriorityLevel)index).ToString(), color))
            .ToArray();
    }

    /// <summary>默认色板：4 级、历史配色。</summary>
    public static PriorityPalette Default { get; } =
        Create(DefaultColors.Take(DefaultLevelCount))!;

    /// <summary>当前生效的层级定义，按优先级从高到低排列。</summary>
    public IReadOnlyList<PriorityLevelDefinition> Levels => _levels;

    /// <summary>当前生效的颜色列表，索引即层级序号。</summary>
    public IReadOnlyList<string> Colors => _colors;

    /// <summary>当前生效的层数。</summary>
    public int Count => _colors.Length;

    /// <summary>判断层级是否落在当前配置的层数范围内。</summary>
    public bool Contains(PriorityLevel level) => (int)level >= 0 && (int)level < Count;

    /// <summary>
    /// 把超出当前层数的层级收敛到最低一级。
    /// 用户减少层数后，历史数据（如 P3 待办）仍保存在库中，只在显示与选择时按最低一级处理，
    /// 之后把层数调回去即可恢复原层级。
    /// </summary>
    public PriorityLevel Normalize(PriorityLevel level) => Contains(level) ? level : (PriorityLevel)(Count - 1);

    /// <summary>按当前层数归一化后的显示代码，如 P2。</summary>
    public string CodeFor(PriorityLevel level) => Normalize(level).ToString();

    /// <summary>按当前层数归一化后的颜色。</summary>
    public string ColorFor(PriorityLevel level) => _colors[(int)Normalize(level)];

    /// <summary>是否属于“高优先级”：当前配置中最高的两个层级。</summary>
    public bool IsHighPriority(PriorityLevel level) => (int)Normalize(level) <= 1;

    /// <summary>校验并创建色板；层数或任一颜色非法时返回 null。</summary>
    public static PriorityPalette? Create(IEnumerable<string>? colors)
    {
        if (colors is null)
        {
            return null;
        }

        var parsed = new List<string>();
        foreach (var color in colors)
        {
            if (!TryNormalizeColor(color, out var normalized))
            {
                return null;
            }

            parsed.Add(normalized);
        }

        return parsed.Count is < MinLevelCount or > MaxLevelCount ? null : new PriorityPalette(parsed.ToArray());
    }

    /// <summary>校验并创建色板，失败时给出可直接展示的原因。</summary>
    public static bool TryCreate(IEnumerable<string>? colors, out PriorityPalette palette, out string error)
    {
        var created = Create(colors);
        if (created is null)
        {
            palette = Default;
            error = $"颜色配置无效：需要 {MinLevelCount}–{MaxLevelCount} 个 #RRGGBB 颜色。";
            return false;
        }

        palette = created;
        error = string.Empty;
        return true;
    }

    /// <summary>把用户输入规范化为 #RRGGBB；允许省略 #，其他格式一律视为非法。</summary>
    public static bool TryNormalizeColor(string? text, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length != 7)
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!Uri.IsHexDigit(value[index]))
            {
                return false;
            }
        }

        normalized = value.ToUpperInvariant();
        return true;
    }

    /// <summary>规范化颜色，非法输入回退到给定颜色，用于控件双向绑定时的容错。</summary>
    public static string NormalizeColorOrDefault(string? text, string fallback) =>
        TryNormalizeColor(text, out var normalized) ? normalized : fallback;
}

/// <summary>
/// 全局生效的优先级色板入口。设置页保存后调用 <see cref="Apply"/>，
/// 各视图在下一次刷新时读到新色板；<see cref="Changed"/> 供需要立即重绘的宿主订阅。
/// </summary>
public static class PrioritySettings
{
    private static PriorityPalette _current = PriorityPalette.Default;

    /// <summary>当前生效的色板。读取使用 Volatile，保证 UI 线程与后台刷新看到同一实例。</summary>
    public static PriorityPalette Current => Volatile.Read(ref _current);

    /// <summary>色板被替换时触发，参数为新的色板。</summary>
    public static event EventHandler<PriorityPalette>? Changed;

    /// <summary>应用新色板并通知订阅者。</summary>
    public static void Apply(PriorityPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Volatile.Write(ref _current, palette);
        Changed?.Invoke(null, palette);
    }

    /// <summary>恢复默认 4 级配色。</summary>
    public static void ResetToDefault() => Apply(PriorityPalette.Default);
}
