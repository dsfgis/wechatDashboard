namespace WechatDashboard.Application.Capture;

/// <summary>
/// 采集源定义：描述一个采集适配器的静态配置。
/// </summary>
/// <param name="Source">来源标识。</param>
/// <param name="DisplayName">显示名称。</param>
/// <param name="Kind">适配器类型。</param>
/// <param name="Location">采集路径或参数。</param>
/// <param name="IsEnabled">是否启用。</param>
public sealed record CaptureSourceDefinition(
    string Source,
    string DisplayName,
    CaptureSourceKind Kind,
    string Location,
    bool IsEnabled = true);
