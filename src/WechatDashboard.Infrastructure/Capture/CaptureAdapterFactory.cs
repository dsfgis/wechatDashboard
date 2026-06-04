using WechatDashboard.Application.Capture;
using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

public static class CaptureAdapterFactory
{
    public static IReadOnlyList<CaptureSourceDefinition> CreateDefaultJsonlSources(string captureRootPath)
    {
        return new[]
        {
            CreateJsonlSource("WeChat", "微信", captureRootPath),
            CreateJsonlSource("Feishu", "飞书", captureRootPath),
            CreateJsonlSource("Shihuatong", "石化通", captureRootPath),
            CreateJsonlSource("DingTalk", "钉钉", captureRootPath)
        };
    }

    public static IReadOnlyList<CaptureSourceDefinition> CreateDefaultLiveSources(string captureRootPath)
    {
        return CreateDefaultJsonlSources(captureRootPath)
            .Concat(new[] { CreateWeChatWindowTextSource() with { IsEnabled = true } })
            .ToArray();
    }

    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(IEnumerable<CaptureSourceDefinition> sources)
    {
        return CreateAdapters(sources, null);
    }

    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(
        IEnumerable<CaptureSourceDefinition> sources,
        IWindowTextSnapshotProvider? windowTextSnapshotProvider)
    {
        return sources
            .Where(source => source.IsEnabled)
            .Select(source => CreateAdapter(source, windowTextSnapshotProvider))
            .ToArray();
    }

    public static CaptureSourceDefinition CreateWeChatWindowTextSource()
    {
        return new CaptureSourceDefinition(
            Source: "WeChat",
            DisplayName: "微信可见窗口",
            Kind: CaptureSourceKind.WindowText,
            Location: "微信",
            IsEnabled: false);
    }

    private static CaptureSourceDefinition CreateJsonlSource(string source, string displayName, string captureRootPath)
    {
        return new CaptureSourceDefinition(
            Source: source,
            DisplayName: displayName,
            Kind: CaptureSourceKind.JsonlDirectory,
            Location: Path.Combine(captureRootPath, source),
            IsEnabled: true);
    }

    private static IMessageCaptureAdapter CreateAdapter(CaptureSourceDefinition source, IWindowTextSnapshotProvider? windowTextSnapshotProvider)
    {
        return source.Kind switch
        {
            CaptureSourceKind.JsonlDirectory => new JsonlDirectoryCaptureAdapter(source.Source, source.Location),
            CaptureSourceKind.WindowText => CreateWindowTextAdapter(source, windowTextSnapshotProvider),
            _ => throw new NotSupportedException($"Capture source kind '{source.Kind}' is not implemented for source '{source.Source}'.")
        };
    }

    private static IMessageCaptureAdapter CreateWindowTextAdapter(
        CaptureSourceDefinition source,
        IWindowTextSnapshotProvider? windowTextSnapshotProvider)
    {
        if (windowTextSnapshotProvider is null)
        {
            throw new InvalidOperationException($"Window text source '{source.Source}' requires an IWindowTextSnapshotProvider.");
        }

        return new WindowTextCaptureAdapter(
            new WindowTextCaptureOptions(
                Source: source.Source,
                DisplayName: source.DisplayName,
                WindowTitleContains: source.Location,
                ChatId: source.Source,
                ChatName: source.DisplayName)
            {
                IgnoreWindowTitleContains = CreateDefaultIgnoredWindowTitles(source.Source)
            },
            windowTextSnapshotProvider);
    }

    private static IReadOnlyList<string> CreateDefaultIgnoredWindowTitles(string source)
    {
        return string.Equals(source, "WeChat", StringComparison.OrdinalIgnoreCase)
            ? new[] { "微信项目消息看板", "WeChat Dashboard" }
            : Array.Empty<string>();
    }
}
