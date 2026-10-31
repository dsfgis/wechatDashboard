using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// mbox 文件游标的编解码。格式为 <c>v1:字节偏移</c>：记录下一次应该从文件的第几个字节开始解析。
/// 解析失败返回 null，由调用方按“首次运行”处理（从当前文件末尾开始，只采新增）。
/// </summary>
internal static class MboxCursor
{
    private const string Prefix = "v1:";

    public static long? Parse(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset))
        {
            return null;
        }

        var text = offset.StartsWith(Prefix, StringComparison.Ordinal) ? offset[Prefix.Length..] : offset;
        return long.TryParse(text, out var value) && value >= 0 ? value : null;
    }

    public static string Format(long offset) => Prefix + offset;
}

/// <summary>
/// 石化邮本地缓存定位器：在 <c>Documents\sinopec\*\profile\ImapMail\&lt;host&gt;\INBOX</c> 下
/// 查找客户端实际使用的收件箱 mbox 文件。石化邮客户端基于 Thunderbird 内核，路径结构稳定。
/// </summary>
public static class SinopecMailArchiveLocator
{
    /// <summary>返回找到的 INBOX 绝对路径；未安装或未同步过邮件时返回 null。</summary>
    public static string? FindInbox()
    {
        foreach (var root in CandidateRoots())
        {
            var found = SearchRoot(root);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 候选根目录：先取系统“我的文档”，再由 <c>%USERPROFILE%\Documents</c> 兜底。
    /// 部分机器把“我的文档”重定向到 OneDrive，此时客户端仍可能写在本地 Documents 下。
    /// </summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
        {
            var path = Path.Combine(documents, "sinopec");
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var path = Path.Combine(profile, "Documents", "sinopec");
            if (seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static string? SearchRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            foreach (var profileDirectory in Directory.EnumerateDirectories(root, "profile", SearchOption.AllDirectories))
            {
                var imapRoot = Path.Combine(profileDirectory, "ImapMail");
                if (!Directory.Exists(imapRoot))
                {
                    continue;
                }

                foreach (var hostDirectory in Directory.EnumerateDirectories(imapRoot))
                {
                    var inbox = Path.Combine(hostDirectory, "INBOX");
                    if (File.Exists(inbox) && new FileInfo(inbox).Length > 0)
                    {
                        return inbox;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
