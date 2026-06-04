using System.Windows.Automation;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class SystemWindowsAutomationReader : IWindowAutomationReader
{
    private readonly int _maxDepth;
    private readonly int _maxChildrenPerElement;
    private readonly bool _useRawView;

    public SystemWindowsAutomationReader(int maxDepth = 8, int maxChildrenPerElement = 500, bool useRawView = true)
    {
        _maxDepth = maxDepth;
        _maxChildrenPerElement = maxChildrenPerElement;
        _useRawView = useRawView;
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
        var windows = ReadTopLevelAutomationElements(root, cancellationToken)
            .Select(element => ReadElement(element, depth: 0, cancellationToken))
            .Where(element => !string.IsNullOrWhiteSpace(element.Name))
            .ToArray();

        return new WindowAutomationReadResult(windows, capturedAt);
    }

    private WindowAutomationElement ReadElement(AutomationElement element, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var name = SafeCurrentName(element);
        var nativeWindowHandle = SafeNativeWindowHandle(element);
        if (depth >= _maxDepth)
        {
            return new WindowAutomationElement(name, name, Array.Empty<WindowAutomationElement>(), nativeWindowHandle);
        }

        var children = new List<WindowAutomationElement>();
        foreach (var child in ReadChildAutomationElements(element, cancellationToken).Take(_maxChildrenPerElement))
        {
            var childSnapshot = ReadElement(child, depth + 1, cancellationToken);
            if (!string.IsNullOrWhiteSpace(childSnapshot.Name) ||
                !string.IsNullOrWhiteSpace(childSnapshot.Text) ||
                childSnapshot.Children.Count > 0)
            {
                children.Add(childSnapshot);
            }
        }

        return new WindowAutomationElement(name, name, children, nativeWindowHandle);
    }

    private IEnumerable<AutomationElement> ReadTopLevelAutomationElements(AutomationElement root, CancellationToken cancellationToken)
    {
        if (!_useRawView)
        {
            return FindChildElements(root);
        }

        return WalkRawChildren(root, cancellationToken);
    }

    private IEnumerable<AutomationElement> ReadChildAutomationElements(AutomationElement element, CancellationToken cancellationToken)
    {
        if (!_useRawView)
        {
            return FindChildElements(element);
        }

        return WalkRawChildren(element, cancellationToken);
    }

    private static IEnumerable<AutomationElement> FindChildElements(AutomationElement element)
    {
        try
        {
            return element
                .FindAll(TreeScope.Children, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .ToArray();
        }
        catch (ElementNotAvailableException)
        {
            return Array.Empty<AutomationElement>();
        }
    }

    private static IEnumerable<AutomationElement> WalkRawChildren(AutomationElement element, CancellationToken cancellationToken)
    {
        var walker = TreeWalker.RawViewWalker;
        AutomationElement? child;
        try
        {
            child = walker.GetFirstChild(element);
        }
        catch (ElementNotAvailableException)
        {
            yield break;
        }

        while (child is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return child;

            try
            {
                child = walker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                yield break;
            }
        }
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

    private static int SafeNativeWindowHandle(AutomationElement element)
    {
        try
        {
            return element.Current.NativeWindowHandle;
        }
        catch (ElementNotAvailableException)
        {
            return 0;
        }
    }
}
