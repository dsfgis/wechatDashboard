using System.Windows.Automation;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class SystemWindowsAutomationReader : IWindowAutomationReader
{
    private readonly int _maxDepth;
    private readonly int _maxChildrenPerElement;

    public SystemWindowsAutomationReader(int maxDepth = 8, int maxChildrenPerElement = 200)
    {
        _maxDepth = maxDepth;
        _maxChildrenPerElement = maxChildrenPerElement;
    }

    public Task<WindowAutomationReadResult> ReadTopLevelWindowsAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadTopLevelWindows(cancellationToken), cancellationToken);
    }

    private WindowAutomationReadResult ReadTopLevelWindows(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.Now;
        var root = AutomationElement.RootElement;
        var windows = root
            .FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Select(element => ReadElement(element, depth: 0, cancellationToken))
            .Where(element => !string.IsNullOrWhiteSpace(element.Name))
            .ToArray();

        return new WindowAutomationReadResult(windows, capturedAt);
    }

    private WindowAutomationElement ReadElement(AutomationElement element, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var name = SafeCurrentName(element);
        if (depth >= _maxDepth)
        {
            return new WindowAutomationElement(name, name, Array.Empty<WindowAutomationElement>());
        }

        var children = new List<WindowAutomationElement>();
        AutomationElementCollection childElements;
        try
        {
            childElements = element.FindAll(TreeScope.Children, Condition.TrueCondition);
        }
        catch (ElementNotAvailableException)
        {
            return new WindowAutomationElement(name, name, Array.Empty<WindowAutomationElement>());
        }

        foreach (AutomationElement child in childElements.Cast<AutomationElement>().Take(_maxChildrenPerElement))
        {
            var childSnapshot = ReadElement(child, depth + 1, cancellationToken);
            if (!string.IsNullOrWhiteSpace(childSnapshot.Name) ||
                !string.IsNullOrWhiteSpace(childSnapshot.Text) ||
                childSnapshot.Children.Count > 0)
            {
                children.Add(childSnapshot);
            }
        }

        return new WindowAutomationElement(name, name, children);
    }

    private static string SafeCurrentName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? "";
        }
        catch (ElementNotAvailableException)
        {
            return "";
        }
    }
}
