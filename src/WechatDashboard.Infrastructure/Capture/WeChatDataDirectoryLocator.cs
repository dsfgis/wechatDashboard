using System.IO;

namespace WechatDashboard.Infrastructure.Capture;

public static class WeChatDataDirectoryLocator
{
    private static readonly string[] CandidateDbStoragePaths =
    {
        Path.Combine("cache", "xwechat_files"),
        Path.Combine("WeChat Files"),
        Path.Combine("Documents", "WeChat Files")
    };

    public static string? Locate()
    {
        foreach (var root in GetSearchRoots())
        {
            var found = SearchUnderRoot(root);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetSearchRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.IsReady)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private static string? SearchUnderRoot(string root)
    {
        foreach (var candidate in CandidateDbStoragePaths)
        {
            var candidatePath = Path.Combine(root, candidate);
            if (!Directory.Exists(candidatePath))
            {
                continue;
            }

            try
            {
                foreach (var userDir in Directory.EnumerateDirectories(candidatePath))
                {
                    var dbStorage = Path.Combine(userDir, "db_storage");
                    if (!Directory.Exists(dbStorage))
                    {
                        continue;
                    }

                    var messageDir = Path.Combine(dbStorage, "message");
                    if (!Directory.Exists(messageDir))
                    {
                        continue;
                    }

                    var hasDbFiles = Directory.EnumerateFiles(dbStorage, "*.db", SearchOption.AllDirectories).Any();
                    if (hasDbFiles)
                    {
                        return dbStorage;
                    }
                }
            }
            catch
            {
                // Skip inaccessible directories
            }
        }

        return null;
    }
}
