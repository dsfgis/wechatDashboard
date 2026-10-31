using WechatDashboard.Application.Capture;
using WechatDashboard.Application.Common;
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
        return CreateDefaultLiveSources(captureRootPath, readerService)
            .Select(source => source.Kind == CaptureSourceKind.WeChatLocalDatabase
                ? source with { IsEnabled = false }
                : source)
            .ToArray();
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
                CreateSinopecMailArchiveSource(),
                CreateSinopecMailSource(),
            })
            .ToArray();
    }

    /// <summary>根据采集源定义集合创建启用的适配器实例。</summary>
    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(IEnumerable<CaptureSourceDefinition> sources)
    {
        return CreateAdapters(sources, null, null);
    }

    /// <summary>
    /// 根据采集源定义集合创建启用的适配器实例。
    /// WindowText 类型需要外部提供 IWindowTextSnapshotProvider。
    /// </summary>
    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(
        IEnumerable<CaptureSourceDefinition> sources,
        IWindowTextSnapshotProvider? windowTextSnapshotProvider)
    {
        return CreateAdapters(sources, windowTextSnapshotProvider, null);
    }

    /// <summary>
    /// 根据采集源定义集合创建启用的适配器实例；原生微信数据库源必须使用已经初始化的共享 reader。
    /// </summary>
    public static IReadOnlyList<IMessageCaptureAdapter> CreateAdapters(
        IEnumerable<CaptureSourceDefinition> sources,
        IWindowTextSnapshotProvider? windowTextSnapshotProvider,
        IWeChatLocalDatabaseReader? weChatLocalDatabaseReader)
    {
        return sources
            // 仅保留启用项
            .Where(source => source.IsEnabled)
            // WindowText 必须有 provider 才能创建
            .Where(source => source.Kind != CaptureSourceKind.WindowText || windowTextSnapshotProvider is not null)
            .Select(source => CreateAdapter(source, windowTextSnapshotProvider, weChatLocalDatabaseReader))
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
            Kind: CaptureSourceKind.WeChatLocalDatabase,
            // 读取器可用即保留该来源；未初始化由适配器返回独立失败，避免被静默跳过。
            Location: "原生 C#（只读本地数据库）",
            IsEnabled: readerService.IsAvailable);
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

    /// <summary>
    /// 创建石化邮本地缓存采集源：读取客户端已同步到本机的 mbox 收件箱，不需要账号口令。
    /// 未找到缓存文件时置为未启用，避免每轮采集都产生一条失败记录。
    /// </summary>
    public static CaptureSourceDefinition CreateSinopecMailArchiveSource()
    {
        var inbox = SinopecMailArchiveLocator.FindInbox();
        return new CaptureSourceDefinition(
            Source: MboxMailCaptureAdapter.AdapterKey,
            DisplayName: "石化邮（本地缓存）",
            Kind: CaptureSourceKind.SinopecMailArchive,
            Location: inbox ?? "未找到客户端邮件缓存",
            IsEnabled: inbox is not null);
    }

    /// <summary>
    /// 创建石化邮 IMAP 采集源。默认不启用：未保存账号密码时启用只会在每轮采集产生一条
    /// “尚未配置”失败记录，等用户在设置中填写后由界面改为启用。
    /// </summary>
    public static CaptureSourceDefinition CreateSinopecMailSource()
    {
        return new CaptureSourceDefinition(
            Source: MailSourcePolicy.SinopecMail,
            DisplayName: "石化邮（IMAP）",
            Kind: CaptureSourceKind.SinopecMail,
            Location: "amail.sinopec.com:993",
            IsEnabled: false);
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
    private static IMessageCaptureAdapter CreateAdapter(
        CaptureSourceDefinition source,
        IWindowTextSnapshotProvider? windowTextSnapshotProvider,
        IWeChatLocalDatabaseReader? weChatLocalDatabaseReader)
    {
        return source.Kind switch
        {
            CaptureSourceKind.JsonlDirectory => new JsonlDirectoryCaptureAdapter(source.Source, source.Location),
            CaptureSourceKind.WeChatLocalExport => new WeChatLocalExportCaptureAdapter(new WeChatLocalExportOptions(source.Location)
            {
                Source = source.Source
            }),
            CaptureSourceKind.WeChatLocalDatabase => weChatLocalDatabaseReader is null
                ? throw new InvalidOperationException("The native WeChat database source requires the shared initialized reader.")
                : new WeChatLocalDatabaseCaptureAdapter(weChatLocalDatabaseReader),
            CaptureSourceKind.WindowText => CreateWindowTextAdapter(source, windowTextSnapshotProvider),
            CaptureSourceKind.ShihuatongLocalDatabase => new ShihuatongLocalDatabaseCaptureAdapter(),
            CaptureSourceKind.SinopecMail => CreateSinopecMailAdapter(),
            CaptureSourceKind.SinopecMailArchive => CreateSinopecMailArchiveAdapter(),
            _ => throw new NotSupportedException($"Capture source kind '{source.Kind}' is not implemented for source '{source.Source}'.")
        };
    }

    /// <summary>
    /// 创建石化邮本地缓存适配器。每次创建时重新定位缓存文件，客户端换账户目录后无需重启。
    /// </summary>
    private static IMessageCaptureAdapter CreateSinopecMailArchiveAdapter()
    {
        var inbox = SinopecMailArchiveLocator.FindInbox()
            ?? throw new InvalidOperationException(
                "未找到石化邮客户端的本地邮件缓存，请先登录石化邮并完成一次收信。");
        return new MboxMailCaptureAdapter(new MboxMailArchiveReader(inbox), MailSourcePolicy.SinopecMail);
    }

    /// <summary>
    /// 创建石化邮适配器。凭据在每次采集时由密文存储读取，因此用户在运行期更换密码
    /// 只需重新保存凭据，无需重启应用或重建适配器。
    /// </summary>
    private static IMessageCaptureAdapter CreateSinopecMailAdapter()
    {
        var store = new ProtectedImapCredentialStore();
        var options = new SinopecMailOptions
        {
            Account = store.Load()?.Account ?? string.Empty
        };
        return new SinopecMailCaptureAdapter(new ImapMailInboxReader(options, store.Load), options);
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
            ? new[] { "IM消息看板", "微信项目消息看板", "WeChat Dashboard" }
            : Array.Empty<string>();
    }

}
