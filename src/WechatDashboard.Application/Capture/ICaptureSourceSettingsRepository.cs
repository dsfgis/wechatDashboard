using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

public interface ICaptureSourceSettingsRepository
{
    Task<IReadOnlyList<CaptureSourceSettings>> GetAllAsync(CancellationToken cancellationToken);
    Task SaveAsync(CaptureSourceSettings settings, CancellationToken cancellationToken);
    Task SaveAllAsync(IReadOnlyList<CaptureSourceSettings> settings, CancellationToken cancellationToken);
    Task DeleteAllAsync(CancellationToken cancellationToken);
}
