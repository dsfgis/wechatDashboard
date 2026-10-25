using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 项目工具路径定位器：统一管理项目根目录、工具目录、结果目录等路径。
/// 开发环境通过向上查找解决方案文件定位项目根目录；安装环境不依赖源码布局。
/// </summary>
public static class ProjectToolPaths
{
    /// <summary>已安装只读程序根目录。</summary>
    public static string InstallRoot => Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary>当前用户的可变应用数据根目录。</summary>
    public static string UserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WechatDashboard");

    /// <summary>原生微信读取器的无密钥状态目录。</summary>
    public static string WeChatNativeReaderDirectory => Path.Combine(UserDataRoot, "Reader");

    /// <summary>当前 Windows 用户可解密的微信 Key 密文文件。</summary>
    public static string WeChatProtectedKeyPath => Path.Combine(
        WeChatNativeReaderDirectory,
        "protected-wechat-key.bin");

    /// <summary>当前 Windows 用户可解密的 DeepSeek API Key 密文文件。</summary>
    public static string DeepSeekProtectedApiKeyPath => Path.Combine(
        UserDataRoot,
        "Agent",
        "protected-deepseek-api-key.bin");

    /// <summary>原生微信读取器的稳定快照目录。</summary>
    public static string WeChatNativeSnapshotDirectory => Path.Combine(
        WeChatNativeReaderDirectory,
        "Snapshots");

    /// <summary>原生微信读取器的临时目录。</summary>
    public static string WeChatNativeTemporaryDirectory => Path.Combine(
        WeChatNativeReaderDirectory,
        "Temp");

    /// <summary>项目根目录（含 .sln 的目录）。</summary>
    public static string ProjectRoot => ResolveProjectRoot();

    /// <summary>tools 目录。</summary>
    public static string ToolsDirectory => Path.Combine(ProjectRoot, "tools");

    /// <summary>tools/result 结果输出目录。</summary>
    public static string ResultDirectory => Path.Combine(ToolsDirectory, "result");

    /// <summary>微信本地读取器工具目录。</summary>
    public static string WeChatLocalReaderToolDirectory => Path.Combine(ToolsDirectory, "wechat-local-reader");

    /// <summary>微信本地读取器结果目录。</summary>
    public static string WeChatLocalReaderResultDirectory => Path.Combine(ResultDirectory, "wechat-local-reader");

    /// <summary>微信密钥工具目录。</summary>
    public static string WxKeyToolsDirectory => Path.Combine(ToolsDirectory, "wx-key-tools");

    /// <summary>采集收件箱目录（待导入的 JSONL 文件放置处）。</summary>
    public static string CaptureInboxDirectory => Path.Combine(ResultDirectory, "capture-inbox");

    /// <summary>数据目录（数据库等）。</summary>
    public static string DataDirectory => Path.Combine(ResultDirectory, "data");

    /// <summary>
    /// 定位项目根目录：依次从当前工作目录、基目录向上查找解决方案文件。
    /// </summary>
    private static string ResolveProjectRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var found = SearchUpwards(start);
            if (found is not null)
            {
                return found;
            }
        }

        // 兜底：使用当前工作目录
        return Environment.CurrentDirectory;
    }

    /// <summary>从 startDirectory 向上逐级查找包含 .sln 和微信读取脚本的目录。</summary>
    private static string? SearchUpwards(string startDirectory)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch
        {
            return null;
        }

        while (directory is not null)
        {
            var solutionPath = Path.Combine(directory.FullName, "WechatDashboard.sln");
            if (File.Exists(solutionPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
