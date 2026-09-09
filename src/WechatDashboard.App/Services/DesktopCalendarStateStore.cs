using System.IO;
using System.Text.Json;

namespace WechatDashboard.App.Services;

public enum DesktopCalendarDockEdge
{
    None,
    Left,
    Top,
    Right,
    Bottom
}

public sealed record DesktopCalendarWindowState(
    double? Left = null,
    double? Top = null,
    double Width = DesktopCalendarStateStore.DefaultWidth,
    double Height = DesktopCalendarStateStore.DefaultHeight,
    double Opacity = 0.9,
    bool IsEdgeSnapEnabled = true,
    DesktopCalendarDockEdge DockEdge = DesktopCalendarDockEdge.None,
    bool IsDesktopEmbedded = false,
    bool IsDarkTheme = false);

/// <summary>在当前用户的本地应用数据目录保存桌面日历的位置与显示偏好。</summary>
public sealed class DesktopCalendarStateStore
{
    public const double DefaultWidth = 980;
    public const double DefaultHeight = 620;
    public const double MinimumWidth = 860;
    public const double MinimumHeight = 520;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public DesktopCalendarStateStore(string path)
    {
        _path = path;
    }

    public DesktopCalendarWindowState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new DesktopCalendarWindowState();
            }

            var state = JsonSerializer.Deserialize<DesktopCalendarWindowState>(File.ReadAllText(_path), JsonOptions);
            return state is null ? new DesktopCalendarWindowState() : Normalize(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DesktopCalendarWindowState();
        }
    }

    public void Save(DesktopCalendarWindowState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Normalize(state), JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 窗口偏好保存失败不应影响待办和应用退出。
        }
    }

    private static DesktopCalendarWindowState Normalize(DesktopCalendarWindowState state) => state with
    {
        Left = IsFinite(state.Left) ? state.Left : null,
        Top = IsFinite(state.Top) ? state.Top : null,
        Width = double.IsFinite(state.Width) ? Math.Max(MinimumWidth, state.Width) : DefaultWidth,
        Height = double.IsFinite(state.Height) ? Math.Max(MinimumHeight, state.Height) : DefaultHeight,
        DockEdge = state.IsDesktopEmbedded ? DesktopCalendarDockEdge.None : state.DockEdge,
        Opacity = double.IsFinite(state.Opacity) ? Math.Clamp(state.Opacity, 0.55, 1.0) : 0.9
    };

    private static bool IsFinite(double? value) => value is { } number && double.IsFinite(number);
}
