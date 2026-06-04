namespace WechatDashboard.Infrastructure.Capture;

public sealed class WindowsOcrWindowTextSnapshotProvider : IWindowTextSnapshotProvider
{
    private readonly IWindowAutomationReader _reader;
    private readonly IScreenOcrReader _ocrReader;

    public WindowsOcrWindowTextSnapshotProvider(IWindowAutomationReader reader, IScreenOcrReader ocrReader)
    {
        _reader = reader;
        _ocrReader = ocrReader;
    }

    public async Task<IReadOnlyList<WindowTextSnapshot>> GetSnapshotsAsync(
        WindowTextCaptureOptions options,
        CancellationToken cancellationToken)
    {
        var result = await _reader.ReadTopLevelWindowsAsync(cancellationToken);
        var snapshots = new List<WindowTextSnapshot>();

        foreach (var window in result.Windows.Where(window => MatchesWindow(window, options)))
        {
            var automationText = string.Join(
                Environment.NewLine,
                FlattenText(window).Where(line => !string.IsNullOrWhiteSpace(line)));
            var ocrText = window.NativeWindowHandle == 0
                ? ""
                : await _ocrReader.ReadWindowTextAsync(window.NativeWindowHandle, cancellationToken);
            var combinedText = CombineText(automationText, ocrText);

            snapshots.Add(new WindowTextSnapshot(
                WindowTitle: window.Name,
                Text: combinedText,
                CapturedAt: result.CapturedAt));
        }

        return snapshots;
    }

    private static bool MatchesWindow(WindowAutomationElement window, WindowTextCaptureOptions options)
    {
        return string.IsNullOrWhiteSpace(options.WindowTitleContains) ||
               window.Name.Contains(options.WindowTitleContains, StringComparison.OrdinalIgnoreCase);
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

    private static string CombineText(string automationText, string ocrText)
    {
        return string.Join(
            Environment.NewLine,
            new[] { automationText, ocrText }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim()));
    }
}
