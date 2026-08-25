using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.Infrastructure.Capture;

public interface IWeChatEncryptedDatabaseAccessor
{
    SqliteConnection OpenReadOnly(string databasePath, WeChatDatabaseKeyLease masterKeyLease);
}

/// <summary>通过现有 SQLCipher bundle 和二进制 sqlite3_key API 打开微信数据库快照。</summary>
public sealed class SqlCipherDatabaseAccessor : IWeChatEncryptedDatabaseAccessor
{
    public SqliteConnection OpenReadOnly(
        string databasePath,
        WeChatDatabaseKeyLease masterKeyLease)
    {
        ArgumentNullException.ThrowIfNull(masterKeyLease);
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new WeChatReaderException(
                WeChatReaderStage.DatabaseOpen,
                WeChatReaderErrorCode.RequiredDatabaseMissing,
                "A required WeChat snapshot database was not found.");
        }

        SqliteConnectionFactory.EnsureSqliteInitialized();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        var keyBytes = new byte[WeChatDatabaseKeyLease.KeyLength];
        try
        {
            connection.Open();
            masterKeyLease.CopyTo(keyBytes);
            var result = SQLitePCL.raw.sqlite3_key(connection.Handle, keyBytes);
            if (result != SQLitePCL.raw.SQLITE_OK)
            {
                throw new SqliteException("SQLCipher rejected the database key.", result);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=5000; SELECT count(*) FROM sqlite_master;";
                command.ExecuteScalar();
            }

            return connection;
        }
        catch (WeChatReaderException)
        {
            connection.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            connection.Dispose();
            throw new WeChatReaderException(
                WeChatReaderStage.DatabaseOpen,
                WeChatReaderErrorCode.CipherUnsupported,
                "Unable to open a WeChat database snapshot with the native SQLCipher reader.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
