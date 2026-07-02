using System.Windows.Automation;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 基于 Windows UI Automation（UIA）的窗口元素读取器。
/// 通过自动化树遍历所有顶级窗口及其子元素，提取名称、句柄等信息，
/// 用于窗口文本快照采集与可见窗口诊断。支持递归深度与子节点数限制，
/// 以及 RawView/标准视图切换（RawView 能读取更多隐藏元素）。
/// </summary>
public sealed class SystemWindowsAutomationReader : IWindowAutomationReader
{
    // 最大递归深度，防止过深的控件树导致性能问题
    private readonly int _maxDepth;
    // 每个元素最多读取的子元素数量
    private readonly int _maxChildrenPerElement;
    // 是否使用 RawView 视图遍历（包含更多元素）
    private readonly bool _useRawView;

    /// <summary>构造读取器，可配置递归深度、子节点上限与是否使用 RawView。</summary>
    public SystemWindowsAutomationReader(int maxDepth = 8, int maxChildrenPerElement = 500, bool useRawView = true)
    {
        _maxDepth = maxDepth;
        _maxChildrenPerElement = maxChildrenPerElement;
        _useRawView = useRawView;
    }

    /// <summary>异步读取所有顶级窗口的自动化元素树。</summary>
    public Task<WindowAutomationReadResult> ReadTopLevelWindowsAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadTopLevelWindows(cancellationToken), cancellationToken);
    }

    /// <summary>同步读取顶级窗口：从桌面根元素遍历，过滤无名称元素。</summary>
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

    /// <summary>递归读取元素及其子树，受最大深度与子节点数限制。</summary>
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

    /// <summary>读取顶级窗口子元素：按配置选择 RawView 或标准 FindAll。</summary>
    private IEnumerable<AutomationElement> ReadTopLevelAutomationElements(AutomationElement root, CancellationToken cancellationToken)
    {
        if (!_useRawView)
        {
            return FindChildElements(root);
        }

        return WalkRawChildren(root, cancellationToken);
    }

    /// <summary>读取子元素：按配置选择 RawView 或标准 FindAll。</summary>
    private IEnumerable<AutomationElement> ReadChildAutomationElements(AutomationElement element, CancellationToken cancellationToken)
    {
        if (!_useRawView)
        {
            return FindChildElements(element);
        }

        return WalkRawChildren(element, cancellationToken);
    }

    /// <summary>使用 FindAll 获取直接子元素（标准视图）。</summary>
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

    /// <summary>使用 RawViewWalker 遍历子元素（可读隐藏元素），遇不可用元素时中止。</summary>
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

    /// <summary>安全读取元素名称，元素不可用时返回空字符串。</summary>
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

    /// <summary>安全读取元素原生窗口句柄，元素不可用时返回 0。</summary>
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
