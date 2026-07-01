using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

public static class ProjectToolPaths
{
    public static string ProjectRoot => ResolveProjectRoot();

    public static string ToolsDirectory => Path.Combine(ProjectRoot, "tools");

    public static string ResultDirectory => Path.Combine(ToolsDirectory, "result");

    public static string WeChatLocalReaderToolDirectory => Path.Combine(ToolsDirectory, "wechat-local-reader");

    public static string WeChatLocalReaderResultDirectory => Path.Combine(ResultDirectory, "wechat-local-reader");

    public static string WxKeyToolsDirectory => Path.Combine(ToolsDirectory, "wx-key-tools");

    public static string CaptureInboxDirectory => Path.Combine(ResultDirectory, "capture-inbox");

    public static string DataDirectory => Path.Combine(ResultDirectory, "data");

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

        return Environment.CurrentDirectory;
    }

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

            if (File.Exists(solutionPath) && File.Exists(readerPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}