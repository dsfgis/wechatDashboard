namespace WechatDashboard.Infrastructure.Capture;

public sealed class WindowsUiAutomationSnapshotProvider : IWindowTextSnapshotProvider
{
    private readonly IWindowAutomationReader _reader;

    public WindowsUiAutomationSnapshotProvider(IWindowAutomationReader reader)
    {
        _reader = reader;
    }

    public async Task<IReadOnlyList<WindowTextSnapshot>> GetSnapshotsAsync(
        WindowTextCaptureOptions options,
        CancellationToken cancellationToken)
    {
        var result = await _reader.ReadTopLevelWindowsAsync(cancellationToken);
        return result.Windows
            .Where(window => MatchesWindow(window, options))
            .Select(window => new WindowTextSnapshot(
                WindowTitle: window.Name,
                Text: string.Join(Environment.NewLine, FlattenText(window).Where(line => !string.IsNullOrWhiteSpace(line))),
                CapturedAt: result.CapturedAt))
            .ToArray();
    }

    private static bool MatchesWindow(WindowAutomationElement window, WindowTextCaptureOptions options)
    {
        return (string.IsNullOrWhiteSpace(options.WindowTitleContains) ||
                window.Name.Contains(options.WindowTitleContains, StringComparison.OrdinalIgnoreCase)) &&
               !options.IgnoreWindowTitleContains.Any(ignored =>
                   window.Name.Contains(ignored, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> FlattenText(WindowAutomationElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.Text))
        {
            yield return element.Text.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(element.Name))
        {
            yield return element.Name.Trim();
        }

        foreach (var child in element.Children)
        {
            foreach (var line in FlattenText(child))
            {
                yield return line;
            }
        }
    }
}
