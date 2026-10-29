using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WechatDashboard.App.Views.Reminders;
using WechatDashboard.Application.Reminders;
using Forms = System.Windows.Forms;

namespace WechatDashboard.App.Services;

/// <summary>
/// 右下角提醒队列：同一时刻最多叠放 <see cref="MaxVisibleCount"/> 条，其余按到期顺序排队，
/// 处理掉一条后自动补位。提醒发布方只负责入队，不再被用户处理动作阻塞。
/// </summary>
public sealed class ReminderNotificationQueue : IDisposable
{
    /// <summary>同时可见的提醒条数上限，超出部分排队等待。</summary>
    public const int MaxVisibleCount = 3;

    /// <summary>卡片与屏幕工作区边缘的间距（DIP）。</summary>
    private const double ScreenEdgeMargin = 24;

    /// <summary>相邻卡片之间的间距（DIP）。</summary>
    private const double StackGap = 12;

    /// <summary>卡片高度下限；低于该值内容与按钮会挤在一起。</summary>
    private const double MinCardHeight = 170;

    private readonly Func<ReminderDispatchItem, ReminderNotificationAction, Task> _onResolved;
    private readonly Queue<ReminderDispatchItem> _pending = new();
    private readonly List<ReminderNotificationWindow> _visible = new();
    private readonly HashSet<long> _knownReminderIds = new();
    private bool _disposed;

    public ReminderNotificationQueue(
        Func<ReminderDispatchItem, ReminderNotificationAction, Task> onResolved)
    {
        _onResolved = onResolved;
    }

    /// <summary>当前排队（尚未显示）的提醒条数。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>当前屏幕上可见的提醒条数。</summary>
    public int VisibleCount => _visible.Count;

    /// <summary>入队并按 FIFO 规则尽量显示；同一提醒重复入队时忽略。</summary>
    public void Enqueue(ReminderDispatchItem item)
    {
        if (_disposed || !_knownReminderIds.Add(item.Reminder.Id))
        {
            return;
        }

        _pending.Enqueue(item);
        ShowPending();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending.Clear();
        foreach (var window in _visible.ToArray())
        {
            window.Close();
        }

        _visible.Clear();
        _knownReminderIds.Clear();
    }

    private void ShowPending()
    {
        while (!_disposed && _visible.Count < MaxVisibleCount && _pending.Count > 0)
        {
            Show(_pending.Dequeue());
        }

        UpdateQueueIndicators();
    }

    private void Show(ReminderDispatchItem item)
    {
        var window = new ReminderNotificationWindow(item.TodoTitle, item.TodoDescription);
        window.Closed += (_, _) => OnWindowClosed(window, item);
        window.Loaded += (_, _) => Relayout();
        // SizeToContent 的高度在首帧之后才稳定，尺寸变化时重排，避免卡片压出工作区。
        window.SizeChanged += (_, _) => Relayout();
        window.ContentRendered += (_, _) =>
        {
            // 内容渲染完成后尺寸与位置都已确定，此时再淡入，用户看到的第一帧就是最终位置。
            Relayout();
            window.Opacity = 1;
        };

        _visible.Add(window);
        window.Show();
    }

    private void OnWindowClosed(ReminderNotificationWindow window, ReminderDispatchItem item)
    {
        _visible.Remove(window);
        _knownReminderIds.Remove(item.Reminder.Id);

        // 应用退出时窗口会被统一关闭，此时不再补位，否则会在关闭流程里反复创建新窗口。
        if (_disposed || IsApplicationShuttingDown())
        {
            return;
        }

        Relayout();
        ShowPending();

        // 按钮与标题栏关闭统一按窗口记录的 Action 处理，点 X 等价于“知道了”。
        _ = HandleResolvedAsync(item, window.Action);
    }

    private static bool IsApplicationShuttingDown()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.HasShutdownStarted;
    }

    private async Task HandleResolvedAsync(ReminderDispatchItem item, ReminderNotificationAction action)
    {
        try
        {
            await _onResolved(item, action);
        }
        catch (Exception ex)
        {
            // 提醒已送达，后续写库失败不应拖垮 UI 线程；保留诊断输出供排障。
            System.Diagnostics.Debug.WriteLine($"处理提醒 {item.Reminder.Id} 失败：{ex}");
        }
    }

    /// <summary>按“最新在底部、旧卡向上让位”的顺序重排所有可见卡片。</summary>
    private void Relayout()
    {
        if (_visible.Count == 0)
        {
            return;
        }

        var workArea = ResolveWorkArea(_visible[0]);
        var maxCardHeight = ResolveMaxCardHeight(workArea);

        // 先收紧单卡高度上限，保证叠放放得进工作区；尺寸变化会再次触发重排修正位置。
        foreach (var window in _visible)
        {
            if (!double.IsNaN(maxCardHeight) && Math.Abs(window.MaxHeight - maxCardHeight) > 0.5)
            {
                window.MaxHeight = maxCardHeight;
            }
        }

        var bottom = workArea.Bottom - ScreenEdgeMargin;

        for (var index = _visible.Count - 1; index >= 0; index--)
        {
            var window = _visible[index];
            if (!window.IsVisible)
            {
                continue;
            }

            var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            var height = window.ActualHeight;
            if (double.IsNaN(width) || double.IsNaN(height) || height <= 0)
            {
                // 尚未完成布局的卡片跳过，等待其 Loaded 事件再次触发重排。
                continue;
            }

            var top = Math.Max(workArea.Top, bottom - height);
            window.Left = workArea.Right - width - ScreenEdgeMargin;
            window.Top = top;
            bottom = top - StackGap;
        }
    }

    /// <summary>单卡高度上限：扣除上下边距与卡片间距后，均分给同时可见的卡片。</summary>
    private static double ResolveMaxCardHeight(Rect workArea)
    {
        var available = workArea.Height
            - (2 * ScreenEdgeMargin)
            - ((MaxVisibleCount - 1) * StackGap);
        return Math.Max(MinCardHeight, available / MaxVisibleCount);
    }

    private void UpdateQueueIndicators()
    {
        foreach (var window in _visible)
        {
            window.UpdateQueueStatus(_pending.Count);
        }
    }

    private static Rect ResolveWorkArea(Window reference)
    {
        var screen = ResolveTargetScreen(reference);
        var transform = PresentationSource.FromVisual(reference)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>优先跟随主窗口所在显示器；主窗口不可见时使用参考窗口或鼠标所在显示器。</summary>
    private static Forms.Screen ResolveTargetScreen(Window reference)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            var ownerHandle = new WindowInteropHelper(owner).Handle;
            if (ownerHandle != IntPtr.Zero)
            {
                return Forms.Screen.FromHandle(ownerHandle);
            }
        }

        var referenceHandle = new WindowInteropHelper(reference).Handle;
        if (referenceHandle != IntPtr.Zero)
        {
            return Forms.Screen.FromHandle(referenceHandle);
        }

        return Forms.Screen.FromPoint(Forms.Cursor.Position);
    }
}
