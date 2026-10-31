using System.Collections.ObjectModel;
using WechatDashboard.App.Presentation;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.App.ViewModels.Todos;

/// <summary>
/// “待办理 → 优先级”下的一个层级分组。
/// 层数与颜色来自“优先级设置”，因此分组是数据驱动的：用户把层数从 4 级改成 5 级后，
/// 这里会多出一个 P4 分组，不需要改界面。
/// </summary>
public sealed class PriorityGroupViewModel : ObservableObject
{
    private readonly Action<PriorityLevel, bool> _expandedChanged;
    private bool _isExpanded;

    /// <summary>创建层级分组。</summary>
    /// <param name="level">层级枚举值。</param>
    /// <param name="code">显示代码，如 P0。</param>
    /// <param name="color">该层级颜色。</param>
    /// <param name="isExpanded">初始展开状态，由工作台记忆上一次的选择。</param>
    /// <param name="expandedChanged">展开状态变化的回调，用于在刷新之间保持展开状态。</param>
    public PriorityGroupViewModel(
        PriorityLevel level,
        string code,
        string color,
        bool isExpanded,
        Action<PriorityLevel, bool> expandedChanged)
    {
        Level = level;
        Code = code;
        Color = color;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
    }

    /// <summary>层级枚举值。</summary>
    public PriorityLevel Level { get; }

    /// <summary>显示代码，如 P0。</summary>
    public string Code { get; }

    /// <summary>层级颜色，用于分组标题与卡片中的优先级文本。</summary>
    public string Color { get; }

    /// <summary>该层级下的活动待办，包含置顶项，不包含已办。</summary>
    public ObservableCollection<TodoListItemViewModel> Items { get; } = new();

    /// <summary>分组展开状态；刷新会重建分组，因此状态写回工作台以保持不变。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                _expandedChanged(Level, value);
            }
        }
    }
}
