namespace WechatDashboard.Domain.Entities;

/// <summary>
/// 采集源设置实体，对应 capture_source_settings 表。
/// 描述一个采集适配器的配置：来源、显示名、类型、路径、是否启用。
/// </summary>
/// <param name="Id">数据库自增主键。</param>
/// <param name="Source">来源标识，如 WeChat、Feishu。</param>
/// <param name="DisplayName">用于界面显示的名称。</param>
/// <param name="Kind">适配器类型，如 JsonlDirectory、LocalExport、WindowText。</param>
/// <param name="Location">采集路径或描述信息。</param>
/// <param name="IsEnabled">是否启用该采集源。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">最近更新时间。</param>
public sealed record CaptureSourceSettings(
    long Id,
    string Source,
    string DisplayName,
    string Kind,
    string Location,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
