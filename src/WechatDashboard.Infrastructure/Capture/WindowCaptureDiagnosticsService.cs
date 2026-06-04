namespace WechatDashboard.Infrastructure.Capture;

public sealed class WindowCaptureDiagnosticsService
{
    private readonly IWindowTextSnapshotProvider _snapshotProvider;
    private readonly int _previewLimit;

    public WindowCaptureDiagnosticsService(IWindowTextSnapshotProvider snapshotProvider, int previewLimit = 120)
    {
        _snapshotProvider = snapshotProvider;
        _previewLimit = previewLimit;
    }

    public async Task<IReadOnlyList<WindowCaptureDiagnosticRow>> ScanAsync(
        WindowTextCaptureOptions options,
        CancellationToken cancellationToken)
    {
        var snapshots = await _snapshotProvider.GetSnapshotsAsync(options, cancellationToken);
        return snapshots
            .Where(snapshot => string.IsNullOrWhiteSpace(options.WindowTitleContains) ||
                               snapshot.WindowTitle.Contains(options.WindowTitleContains, StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => new WindowCaptureDiagnosticRow(
                WindowTitle: snapshot.WindowTitle,
                CapturedAt: snapshot.CapturedAt,
                TextLength: snapshot.Text.Length,
                Preview: BuildPreview(snapshot.Text)))
            .ToArray();
    }

    private string BuildPreview(string value)
    {
        var normalized = string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= _previewLimit)
        {
            return normalized;
        }

        return normalized[.._previewLimit] + "...";
    }
}
