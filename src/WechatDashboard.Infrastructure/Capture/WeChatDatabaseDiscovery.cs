using System.IO;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

public sealed record WeChatDatabaseDescriptor(
    string RelativePath,
    string FullPath,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

/// <summary>发现微信 V4 会话、联系人和消息分片数据库。</summary>
public sealed class WeChatDatabaseDiscovery
{
    public IReadOnlyList<WeChatDatabaseDescriptor> Discover(string databaseRoot)
    {
        if (string.IsNullOrWhiteSpace(databaseRoot))
        {
            throw new WeChatReaderException(
                WeChatReaderStage.PathDiscovery,
                WeChatReaderErrorCode.DatabaseDirectoryMissing,
                "WeChat database directory was not specified.");
        }

        var normalizedRoot = NormalizeRoot(databaseRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new WeChatReaderException(
                WeChatReaderStage.PathDiscovery,
                WeChatReaderErrorCode.DatabaseDirectoryMissing,
                "WeChat database directory was not found.");
        }

        var databases = Directory.EnumerateFiles(normalizedRoot, "*.db", SearchOption.AllDirectories)
            .Select(path =>
            {
                var fullPath = Path.GetFullPath(path);
                var info = new FileInfo(fullPath);
                return new WeChatDatabaseDescriptor(
                    NormalizeRelativePath(Path.GetRelativePath(normalizedRoot, fullPath)),
                    fullPath,
                    info.Length,
                    info.LastWriteTimeUtc);
            })
            .OrderBy(database => database.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var paths = databases.Select(database => database.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        if (!paths.Contains("session/session.db"))
        {
            missing.Add("session/session.db");
        }
        if (!paths.Contains("contact/contact.db"))
        {
            missing.Add("contact/contact.db");
        }
        if (!paths.Any(IsMessageDatabase))
        {
            missing.Add("message/message_N.db");
        }

        if (missing.Count > 0)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.PathDiscovery,
                WeChatReaderErrorCode.RequiredDatabaseMissing,
                $"Required WeChat databases are missing: {string.Join(", ", missing)}.");
        }

        return databases;
    }

    public static string NormalizeRoot(string databaseRoot)
    {
        var fullPath = Path.GetFullPath(databaseRoot);
        var nested = Path.Combine(fullPath, "db_storage");
        return Directory.Exists(nested) ? nested : fullPath;
    }

    public static bool IsMessageDatabase(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fileName = Path.GetFileName(normalized);
        return normalized.StartsWith("message/", StringComparison.OrdinalIgnoreCase) &&
               (string.Equals(fileName, "message.db", StringComparison.OrdinalIgnoreCase) ||
                (fileName.StartsWith("message_", StringComparison.OrdinalIgnoreCase) &&
                 fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)));
    }

    public static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');
}
