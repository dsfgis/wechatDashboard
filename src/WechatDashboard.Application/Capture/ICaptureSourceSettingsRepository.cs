using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

/// <summary>
/// 采集源设置仓储接口：持久化采集源的启用状态等配置。
/// 实现见 SqliteCaptureSourceSettingsRepository。
/// </summary>
public interface ICaptureSourceSettingsRepository
{
    /// <summary>读取所有采集源设置。</summary>
    Task<IReadOnlyList<CaptureSourceSettings>> GetAllAsync(CancellationToken cancellationToken);
    /// <summary>保存单条设置。</summary>
    Task SaveAsync(CaptureSourceSettings settings, CancellationToken cancellationToken);
    /// <summary>批量保存设置（覆盖）。</summary>
    Task SaveAllAsync(IReadOnlyList<CaptureSourceSettings> settings, CancellationToken cancellationToken);
    /// <summary>清空所有设置。</summary>
    Task DeleteAllAsync(CancellationToken cancellationToken);
}
