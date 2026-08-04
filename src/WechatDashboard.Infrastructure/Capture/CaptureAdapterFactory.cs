using WechatDashboard.Application.Capture;
using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 采集适配器工厂：根据采集源定义创建对应的适配器实例，并提供默认采集源配置。
/// 支持的来源：微信、飞书、石化通、钉钉；微信又细分本地导出/本地数据库/可见窗口三种方式。
/// </summary>
public static class CaptureAdapterFactory
{
    /// <summary>创建默认的 JSONL 目录采集源（微信/飞书/石化通/钉钉）。</summary>
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

    /// <summary>创建默认的实时采集源（含 JSONL、微信本地源和石化通本地数据库）。</summary>
    public static IReadOnlyList<CaptureSourceDefinition> CreateDefaultLiveSources(string captureRootPath)
    {
        var readerService = new WeChatLocalReaderService();
        return CreateDefaultLiveSources(captureRootPath, readerService);
    }

    /// <summary>创建默认的实时采集源（带自定义读取服务，便于注入测试）。</summary>
    public static IReadOnlyList<CaptureSourceDefinition> CreateDefaultLiveSources(
        string captureRootPath,
        WeChatLocalReaderService readerService)
    {
        return CreateDefaultJsonlSources(captureRootPath)
            .Concat(new[]
            {
                CreateWeChatLocalExportSource(captureRootPath),
                CreateWeChatLocalDatabaseSource(readerService),
                CreateShihuatongLocalDatabaseSource(),
            })
            .ToArray();
    }

    /// <summary>根据采集源定义集合创建启用的适配器实例。</summary>
    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(IEnumerable<CaptureSourceDefinition> sources)
    {
        return CreateAdapters(sources, null);
    }

    /// <summary>
    /// 根据采集源定义集合创建启用的适配器实例。
    /// WindowText 类型需要外部提供 IWindowTextSnapshotProvider。
    /// </summary>
    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(
        IEnumerable<CaptureSourceDefinition> sources,
        IWindowTextSnapshotProvider? windowTextSnapshotProvider)
    {
        return sources
            // 仅保留启用项
            .Where(source => source.IsEnabled)
            // WindowText 必须有 provider 才能创建
            .Where(source => source.Kind != CaptureSourceKind.WindowText || windowTextSnapshotProvider is not null)
            .Select(source => CreateAdapter(source, windowTextSnapshotProvider))
            .ToArray();
    }

    /// <summary>创建微信可见窗口采集源定义。</summary>
    public static CaptureSourceDefinition CreateWeChatWindowTextSource()
    {
        return new CaptureSourceDefinition(
            Source: "WeChat",
            DisplayName: "微信可见窗口",
            Kind: CaptureSourceKind.WindowText,
            Location: "微信",
            IsEnabled: false);
    }

    /// <summary>创建微信本地导出采集源定义。</summary>
    public static CaptureSourceDefinition CreateWeChatLocalExportSource(string captureRootPath)
    {
        return new CaptureSourceDefinition(
            Source: "WeChat",
            DisplayName: "微信本地导出",
            Kind: CaptureSourceKind.WeChatLocalExport,
            Location: Path.Combine(captureRootPath, "WeChatLocalExport"),
            IsEnabled: true);
    }

    /// <summary>创建微信本地数据库采集源定义（自动发现读取器）。</summary>
    public static CaptureSourceDefinition CreateWeChatLocalDatabaseSource()
    {
        return CreateWeChatLocalDatabaseSource(new WeChatLocalReaderService());
    }

    /// <summary>创建微信本地数据库采集源定义（带自定义读取服务）。</summary>
    public static CaptureSourceDefinition CreateWeChatLocalDatabaseSource(WeChatLocalReaderService readerService)
    {
        return new CaptureSourceDefinition(
            Source: "WeChat",
            DisplayName: "微信本地数据库",
            Kind: CaptureSourceKind.WeChatLocalCommand,
            // 仅当读取器可用且已初始化时启用
            Location: readerService.ReaderExecutablePath ?? "",
            IsEnabled: readerService.IsAvailable && readerService.IsInitialized);
    }

    /// <summary>创建石化通本地数据库采集源；读取时不依赖可见窗口。</summary>
    public static CaptureSourceDefinition CreateShihuatongLocalDatabaseSource()
    {
        return new CaptureSourceDefinition(
            Source: "Shihuatong",
            DisplayName: "石化通本地数据库",
            Kind: CaptureSourceKind.ShihuatongLocalDatabase,
            Location: "自动发现（只读本地数据库）",
            IsEnabled: true);
    }

    /// <summary>获取微信本地数据库配置文件路径。</summary>
    public static string GetWeChatLocalDatabaseConfigPath()
    {
        return WeChatLocalReaderService.DefaultConfigPath;
    }

    /// <summary>创建单个 JSONL 目录采集源定义。</summary>
    private static CaptureSourceDefinition CreateJsonlSource(string source, string displayName, string captureRootPath)
    {
        return new CaptureSourceDefinition(
            Source: source,
            DisplayName: displayName,
            Kind: CaptureSourceKind.JsonlDirectory,
            Location: Path.Combine(captureRootPath, source),
            IsEnabled: true);
    }

    /// <summary>按采集源类型创建对应适配器。</summary>
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
            CaptureSourceKind.ShihuatongLocalDatabase => new ShihuatongLocalDatabaseCaptureAdapter(),
            _ => throw new NotSupportedException($"Capture source kind '{source.Kind}' is not implemented for source '{source.Source}'.")
        };
    }

    /// <summary>
    /// 创建微信本地命令适配器：若可执行文件是 .py，则使用系统 Python 解释器运行。
    /// </summary>
    private static IMessageCaptureAdapter CreateLocalCommandAdapter(CaptureSourceDefinition source)
    {
        var configPath = GetWeChatLocalDatabaseConfigPath();
        var executable = source.Location;
        IReadOnlyList<string> arguments;

        // .py 脚本需要用 Python 解释器执行
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

    /// <summary>查找系统可用的 Python 解释器（python/python3/py），找不到则回退到 python。</summary>
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
                // 忽略，尝试下一个候选
            }
        }

        return "python";
    }

    /// <summary>创建微信可见窗口文本采集适配器。</summary>
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
                // 微信来源忽略本项目自身的窗口标题，避免自我采集
                IgnoreWindowTitleContains = CreateDefaultIgnoredWindowTitles(source.Source)
            },
            windowTextSnapshotProvider);
    }

    /// <summary>返回默认忽略的窗口标题列表（避免采集到本应用自身）。</summary>
    private static IReadOnlyList<string> CreateDefaultIgnoredWindowTitles(string source)
    {
        return string.Equals(source, "WeChat", StringComparison.OrdinalIgnoreCase)
            ? new[] { "微信项目消息看板", "WeChat Dashboard" }
            : Array.Empty<string>();
    }

}
