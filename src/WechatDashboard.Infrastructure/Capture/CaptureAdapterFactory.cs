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
        var readerService = new WeChatLocalReaderService();
        return CreateDefaultLiveSources(captureRootPath, readerService);
    }

    public static IReadOnlyList<CaptureSourceDefinition> CreateDefaultLiveSources(
        string captureRootPath,
        WeChatLocalReaderService readerService)
    {
        return CreateDefaultJsonlSources(captureRootPath)
            .Concat(new[]
            {
                CreateWeChatLocalExportSource(captureRootPath),
                CreateWeChatLocalDatabaseSource(readerService),
                CreateWeChatWindowTextSource()
            })
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
            .Where(source => source.Kind != CaptureSourceKind.WindowText || windowTextSnapshotProvider is not null)
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
            IsEnabled: true);
    }

    public static CaptureSourceDefinition CreateWeChatLocalExportSource(string captureRootPath)
    {
        return new CaptureSourceDefinition(
            Source: "WeChat",
            DisplayName: "微信本地导出",
            Kind: CaptureSourceKind.WeChatLocalExport,
            Location: Path.Combine(captureRootPath, "WeChatLocalExport"),
            IsEnabled: true);
    }

    public static CaptureSourceDefinition CreateWeChatLocalDatabaseSource()
    {
        return CreateWeChatLocalDatabaseSource(new WeChatLocalReaderService());
    }

    public static CaptureSourceDefinition CreateWeChatLocalDatabaseSource(WeChatLocalReaderService readerService)
    {
        return new CaptureSourceDefinition(
            Source: "WeChat",
            DisplayName: "微信本地数据库",
            Kind: CaptureSourceKind.WeChatLocalCommand,
            Location: readerService.ReaderExecutablePath ?? "",
            IsEnabled: readerService.IsAvailable && readerService.IsInitialized);
    }

    public static string GetWeChatLocalDatabaseConfigPath()
    {
        return WeChatLocalReaderService.DefaultConfigPath;
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
            CaptureSourceKind.WeChatLocalExport => new WeChatLocalExportCaptureAdapter(new WeChatLocalExportOptions(source.Location)
            {
                Source = source.Source
            }),
            CaptureSourceKind.WeChatLocalCommand => CreateLocalCommandAdapter(source),
            CaptureSourceKind.WindowText => CreateWindowTextAdapter(source, windowTextSnapshotProvider),
            _ => throw new NotSupportedException($"Capture source kind '{source.Kind}' is not implemented for source '{source.Source}'.")
        };
    }

    private static IMessageCaptureAdapter CreateLocalCommandAdapter(CaptureSourceDefinition source)
    {
        var configPath = GetWeChatLocalDatabaseConfigPath();
        var executable = source.Location;
        IReadOnlyList<string> arguments;

        if (executable.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            arguments = new[]
            {
                executable, "capture", "--config", configPath, "--format", "json"
            };
            executable = FindSystemPython();
        }
        else
        {
            arguments = new[] { "capture", "--config", configPath, "--format", "json" };
        }

        return new WeChatLocalCommandCaptureAdapter(
            new WeChatLocalCommandOptions(executable, arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(source.Location),
                TemporaryDirectory = Path.Combine(
                    ProjectToolPaths.ResultDirectory,
                    "temp",
                    "wechat-local-reader")
            },
            new ProcessExternalCommandRunner());
    }

    private static string FindSystemPython()
    {
        foreach (var name in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process is not null)
                {
                    process.WaitForExit(3000);
                    if (process.ExitCode == 0)
                    {
                        return name;
                    }
                }
            }
            catch
            {
            }
        }

        return "python";
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
