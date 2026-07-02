using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 项目工具路径定位器：统一管理项目根目录、工具目录、结果目录等路径。
/// 通过向上查找解决方案文件（.sln）与微信读取脚本定位项目根目录。
/// </summary>
public static class ProjectToolPaths
{
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
    /// 定位项目根目录：依次从当前工作目录、基目录向上查找 .sln + 微信读取脚本。
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
            var readerPath = Path.Combine(
                directory.FullName,
                "tools",
                "wechat-local-reader",
                "wechat_local_reader.py");

            // 同时存在 .sln 和读取脚本即认为是项目根
            if (File.Exists(solutionPath) && File.Exists(readerPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
