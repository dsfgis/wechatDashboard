using Microsoft.Data.Sqlite;
using System.IO;

namespace WechatDashboard.Infrastructure.Persistence;

internal static class SqliteConnectionFactory
{
    private static int _sqliteInitialized;
    private const string BusyTimeoutPragma = "PRAGMA busy_timeout=5000;";

    public static SqliteConnection Open(string databasePath)
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = BusyTimeoutPragma;
            pragmaCommand.ExecuteNonQuery();
        }
        return connection;
    }
}
