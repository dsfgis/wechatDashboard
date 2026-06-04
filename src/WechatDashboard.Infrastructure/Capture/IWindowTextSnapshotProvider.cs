namespace WechatDashboard.Infrastructure.Capture;

public interface IWindowTextSnapshotProvider
{
    Task<IReadOnlyList<WindowTextSnapshot>> GetSnapshotsAsync(WindowTextCaptureOptions options, CancellationToken cancellationToken);
}
