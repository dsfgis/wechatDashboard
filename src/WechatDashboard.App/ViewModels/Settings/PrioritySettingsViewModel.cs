using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.Application.Common;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Settings;

/// <summary>预设颜色项：名称 + 十六进制值，供设置页下拉快速选色。</summary>
/// <param name="Name">中文色名。</param>
/// <param name="Color">十六进制颜色。</param>
public sealed record PriorityColorOption(string Name, string Color);

/// <summary>
/// 优先级设置页的一行：一个层级 + 它的颜色。
/// 颜色既可以下拉选预设，也可以直接输入十六进制值；非法输入回退到上一次的有效颜色。
/// </summary>
public sealed class PriorityLevelRowViewModel : ObservableObject
{
    // 预设色板覆盖常见语义色，避免用户每次手写十六进制。
    private static readonly IReadOnlyList<PriorityColorOption> Presets = new[]
    {
        new PriorityColorOption("深红", "#D92D20"),
        new PriorityColorOption("红", "#DC2626"),
        new PriorityColorOption("橙", "#B54708"),
        new PriorityColorOption("琥珀", "#D97706"),
        new PriorityColorOption("靛蓝", "#3B5998"),
        new PriorityColorOption("蓝", "#2563EB"),
        new PriorityColorOption("青", "#0E7490"),
        new PriorityColorOption("绿", "#15803D"),
        new PriorityColorOption("紫", "#7C3AED"),
        new PriorityColorOption("粉", "#DB2777"),
        new PriorityColorOption("灰", "#475467"),
        new PriorityColorOption("石板", "#334155")
    };

    private string _color;

    /// <summary>创建一行层级配置。</summary>
    /// <param name="level">层级枚举值。</param>
    /// <param name="color">初始颜色。</param>
    public PriorityLevelRowViewModel(PriorityLevel level, string color)
    {
        Level = level;
        _color = PriorityPalette.NormalizeColorOrDefault(color, PriorityPalette.DefaultColors[0]);
    }

    /// <summary>层级枚举值。</summary>
    public PriorityLevel Level { get; }

    /// <summary>显示代码，如 P0。</summary>
    public string Code => Level.ToString();

    /// <summary>该层的十六进制颜色；写入时自动规范化并拒绝非法值。</summary>
    public string Color
    {
        get => _color;
        set
        {
            var normalized = PriorityPalette.NormalizeColorOrDefault(value, _color);
            if (SetProperty(ref _color, normalized))
            {
                OnPropertyChanged(nameof(PreviewText));
            }
        }
    }

    /// <summary>可选预设颜色。</summary>
    public IReadOnlyList<PriorityColorOption> ColorOptions => Presets;

    /// <summary>颜色预览文案，用于让用户直观看到该层级的文字效果。</summary>
    public string PreviewText => $"{Code} 示例：这条待办的标题与优先级文字使用该颜色";
}

/// <summary>
/// 优先级设置页视图模型：管理层数（2–6）与每层颜色，保存后写入 app_settings 并立即生效。
/// 保存成功后通过回调让主窗口刷新待办、消息流与日程，避免出现“设置已改、界面没变”的错觉。
/// </summary>
public sealed class PrioritySettingsViewModel : ObservableObject
{
    private readonly IPrioritySettingsRepository _repository;
    private readonly Func<Task> _onApplied;
    private int _levelCount = PriorityPalette.DefaultLevelCount;
    private string _statusText = "正在读取优先级设置...";
    private bool _isBusy;

    /// <summary>创建设置页视图模型。</summary>
    /// <param name="repository">颜色配置仓储。</param>
    /// <param name="onApplied">保存成功后的刷新回调。</param>
    public PrioritySettingsViewModel(IPrioritySettingsRepository repository, Func<Task> onApplied)
    {
        _repository = repository;
        _onApplied = onApplied;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => !_isBusy);
        ResetCommand = new AsyncRelayCommand(_ => ResetAsync(), _ => !_isBusy);
        BuildRows(PriorityPalette.DefaultLevelCount, PriorityPalette.DefaultColors);
    }

    /// <summary>层级颜色行，按优先级从高到低排列。</summary>
    public ObservableCollection<PriorityLevelRowViewModel> Levels { get; } = new();

    /// <summary>可选层数：2–6。</summary>
    public IReadOnlyList<int> LevelCountOptions { get; } =
        Enumerable.Range(PriorityPalette.MinLevelCount, PriorityPalette.MaxLevelCount - PriorityPalette.MinLevelCount + 1).ToArray();

    /// <summary>可选层数数量；修改后立即增删行，保留已有颜色。</summary>
    public int LevelCount
    {
        get => _levelCount;
        set
        {
            var clamped = Math.Clamp(value, PriorityPalette.MinLevelCount, PriorityPalette.MaxLevelCount);
            if (SetProperty(ref _levelCount, clamped))
            {
                // 增减层数时保留已有层级的颜色，新增层级先用默认色，用户可再改。
                BuildRows(clamped, Levels.OrderBy(row => row.Level).Select(row => row.Color).ToArray());
            }
        }
    }

    /// <summary>保存/恢复结果提示。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>保存按钮。</summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>恢复默认按钮。</summary>
    public AsyncRelayCommand ResetCommand { get; }

    /// <summary>从仓储读取配置并应用到全局色板；读取失败时回退默认 4 级。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var colors = await _repository.GetColorsAsync(cancellationToken);
            if (PriorityPalette.Create(colors) is { } palette)
            {
                Apply(palette);
                StatusText = $"当前 {palette.Count} 级优先级。修改后点击“保存设置”立即生效。";
                return;
            }
        }
        catch (Exception exception)
        {
            StatusText = $"优先级设置读取失败，本次使用默认 4 级配色：{exception.Message}";
            Apply(PriorityPalette.Default);
            return;
        }

        StatusText = "已保存的优先级颜色无效，本次使用默认 4 级配色。";
        Apply(PriorityPalette.Default);
    }

    /// <summary>校验并保存当前行，成功后立即应用色板并触发刷新。</summary>
    public async Task SaveAsync()
    {
        var colors = Levels.OrderBy(row => row.Level).Select(row => row.Color).ToArray();
        var palette = PriorityPalette.Create(colors);
        if (palette is null)
        {
            StatusText = $"保存失败：需要 {PriorityPalette.MinLevelCount}–{PriorityPalette.MaxLevelCount} 个 #RRGGBB 颜色。";
            return;
        }

        IsBusy = true;
        try
        {
            await _repository.SaveColorsAsync(palette.Colors, CancellationToken.None);
            Apply(palette);
            StatusText = $"已保存：{palette.Count} 级优先级，颜色已应用到消息流、待办列表与日程卡片。";
            await _onApplied();
        }
        catch (Exception exception)
        {
            StatusText = $"保存失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>恢复默认 4 级配色并立即保存。</summary>
    public async Task ResetAsync()
    {
        LevelCount = PriorityPalette.DefaultLevelCount;
        // 清空后再重建，否则 BuildRows 会优先保留用户当前的改色结果。
        Levels.Clear();
        BuildRows(PriorityPalette.DefaultLevelCount, PriorityPalette.DefaultColors);
        await SaveAsync();
    }

    private bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                ResetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>把色板应用到全局，并同步行与层数选择，保持界面与生效值一致。</summary>
    private void Apply(PriorityPalette palette)
    {
        PrioritySettings.Apply(palette);
        _levelCount = palette.Count;
        OnPropertyChanged(nameof(LevelCount));
        // 已应用的色板是唯一事实来源，先清空再重建，否则会保留内存里的旧颜色。
        Levels.Clear();
        BuildRows(palette.Count, palette.Colors);
    }

    /// <summary>
    /// 按指定层数重建行：保留已有层级的颜色，新增层级依次取 <paramref name="colors"/> 或默认色板，
    /// 保证“先加层再改色”和“直接改层数”都不会把已选颜色丢掉。
    /// </summary>
    private void BuildRows(int count, IReadOnlyList<string> colors)
    {
        var existing = Levels.ToDictionary(row => row.Level, row => row.Color);
        var target = Math.Clamp(count, PriorityPalette.MinLevelCount, PriorityPalette.MaxLevelCount);
        Levels.Clear();
        for (var index = 0; index < target; index++)
        {
            var level = (PriorityLevel)index;
            var color = existing.TryGetValue(level, out var kept)
                ? kept
                : index < colors.Count ? colors[index] : PriorityPalette.DefaultColors[index];
            Levels.Add(new PriorityLevelRowViewModel(level, color));
        }
    }
}
