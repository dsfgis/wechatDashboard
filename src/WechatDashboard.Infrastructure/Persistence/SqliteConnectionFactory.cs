using Microsoft.Data.Sqlite;
using System.IO;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// SQLite 连接工厂：统一管理 SQLitePCL 初始化、目录创建、连接字符串与 busy_timeout。
/// 通过共享缓存模式 + 5 秒 busy_timeout 缓解多连接并发冲突。
/// </summary>
internal static class SqliteConnectionFactory
{
    // 保证 SQLitePCL 只初始化一次
    private static int _sqliteInitialized;
    // 并发时最多等待 5000 毫秒
    private const string BusyTimeoutPragma = "PRAGMA busy_timeout=5000;";

    /// <summary>
    /// 打开一个 SQLite 连接，并在连接上设置 busy_timeout。
    /// </summary>
    /// <param name="databasePath">数据库文件路径。</param>
    public static SqliteConnection Open(string databasePath)
    {
        EnsureSqliteInitialized();

        // 确保数据库目录存在
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 共享缓存 + 5 秒默认超时
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        // 设置 busy_timeout，避免写冲突时直接抛锁异常
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = BusyTimeoutPragma;
            pragmaCommand.ExecuteNonQuery();
        }
        return connection;
    }

    /// <summary>确保普通 SQLite 与 SQLCipher 共用的原生提供器仅初始化一次。</summary>
    internal static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }
}
