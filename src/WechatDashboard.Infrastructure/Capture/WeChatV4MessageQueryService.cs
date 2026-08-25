using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

public sealed record WeChatV4QueryResult(
    IReadOnlyList<CapturedMessage> Messages,
    int TotalCount,
    string ProposedNextOffset,
    IReadOnlyDictionary<string, long> Counts);

/// <summary>读取微信 V4 SessionTable、联系人、Name2Id 和跨分片 Msg 表。</summary>
public sealed class WeChatV4MessageQueryService
{
    private readonly IWeChatEncryptedDatabaseAccessor _accessor;
    private readonly WeChatMessageContentDecoder _contentDecoder;

    public WeChatV4MessageQueryService(
        IWeChatEncryptedDatabaseAccessor? accessor = null,
        WeChatMessageContentDecoder? contentDecoder = null)
    {
        _accessor = accessor ?? new SqlCipherDatabaseAccessor();
        _contentDecoder = contentDecoder ?? new WeChatMessageContentDecoder();
    }

    public WeChatV4QueryResult Read(
        WeChatDatabaseSnapshot snapshot,
        WeChatDatabaseKeyLease masterKeyLease,
        WeChatReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(masterKeyLease);
        ArgumentNullException.ThrowIfNull(request);

        var startTimestamp = ResolveStartTimestamp(request);
        var endTimestamp = request.End?.ToUnixTimeSeconds();
        var contactDatabase = GetRequired(snapshot, "contact/contact.db");
        var sessionDatabase = GetRequired(snapshot, "session/session.db");
        var messageDatabases = snapshot.Databases
            .Where(database => WeChatDatabaseDiscovery.IsMessageDatabase(database.RelativePath))
            .OrderBy(database => database.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        var contactNames = ReadContactNames(contactDatabase.FullPath, masterKeyLease);
        var sessions = ReadSessions(sessionDatabase.FullPath, masterKeyLease, contactNames);
        var messages = new List<CapturedMessage>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        long matchedTables = 0;
        long queryErrors = 0;

        foreach (var shard in messageDatabases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var connection = _accessor.OpenReadOnly(shard.FullPath, masterKeyLease);
                var shardNameMap = new Dictionary<string, string>(contactNames, StringComparer.Ordinal);
                foreach (var item in ReadName2IdMap(connection))
                {
                    shardNameMap[item.Key] = item.Value;
                }

                foreach (var session in sessions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tableName = "Msg_" + Md5HexLower(session.Username);
                    if (!TableExists(connection, tableName))
                    {
                        continue;
                    }

                    matchedTables++;
                    foreach (var message in ReadSessionMessages(
                                 connection,
                                 shard,
                                 session,
                                 tableName,
                                 shardNameMap,
                                 startTimestamp,
                                 endTimestamp,
                                 cancellationToken))
                    {
                        if (seenKeys.Add(message.SourceMessageKey))
                        {
                            messages.Add(message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WeChatReaderException)
            {
                queryErrors++;
            }
            catch (SqliteException)
            {
                queryErrors++;
            }
        }

        if (messageDatabases.Length > 0 && queryErrors == messageDatabases.Length)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.Query,
                WeChatReaderErrorCode.QueryFailed,
                "Every WeChat message shard failed to open or query.");
        }

        var ordered = messages
            .OrderByDescending(message => message.SentAt)
            .ThenByDescending(message => message.SourceMessageKey, StringComparer.Ordinal)
            .ToArray();
        var totalCount = ordered.Length;
        var safeOffset = Math.Max(0, request.Offset);
        var safeLimit = request.Limit <= 0 ? 0 : Math.Clamp(request.Limit, 1, 200);
        IReadOnlyList<CapturedMessage> resultMessages = safeLimit == 0
            ? ordered.Skip(safeOffset).ToArray()
            : ordered.Skip(safeOffset).Take(safeLimit).ToArray();
        var maxTimestamp = ordered.Length == 0
            ? startTimestamp
            : Math.Max(startTimestamp, ordered.Max(message => message.SentAt.ToUnixTimeSeconds()));

        return new WeChatV4QueryResult(
            resultMessages,
            totalCount,
            maxTimestamp.ToString(CultureInfo.InvariantCulture),
            new Dictionary<string, long>
            {
                ["sessions"] = sessions.Count,
                ["matched_tables"] = matchedTables,
                ["shards_scanned"] = messageDatabases.Length,
                ["rows_read"] = totalCount,
                ["query_errors"] = queryErrors
            });
    }

    private IReadOnlyList<CapturedMessage> ReadSessionMessages(
        SqliteConnection connection,
        WeChatDatabaseDescriptor shard,
        WeChatSession session,
        string tableName,
        IReadOnlyDictionary<string, string> nameMap,
        long startTimestamp,
        long? endTimestamp,
        CancellationToken cancellationToken)
    {
        var columns = ReadColumns(connection, tableName);
        var localIdColumn = PickColumn(columns, "local_id", "localId", "msgId");
        var serverIdColumn = PickColumn(columns, "server_id", "MsgSvrID", "msg_svr_id");
        var localTypeColumn = PickColumn(columns, "local_type", "type", "Type");
        var realSenderIdColumn = PickColumn(columns, "real_sender_id", "TalkerId");
        var createTimeColumn = PickColumn(columns, "create_time", "CreateTime", "nTime");
        var messageContentColumn = PickColumn(columns, "message_content", "StrContent", "content");
        var compressedContentColumn = PickColumn(columns, "compress_content", "CompressContent");
        if (createTimeColumn is null)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.Schema,
                WeChatReaderErrorCode.SchemaUnsupported,
                "A WeChat message table is missing its timestamp column.");
        }

        var select = new[]
        {
            localIdColumn is null ? "rowid AS local_id" : SelectExpression(localIdColumn, "local_id"),
            SelectExpression(serverIdColumn, "server_id"),
            SelectExpression(localTypeColumn, "local_type", "1"),
            SelectExpression(realSenderIdColumn, "real_sender_id"),
            SelectExpression(createTimeColumn, "create_time"),
            SelectExpression(messageContentColumn, "message_content", "''"),
            SelectExpression(compressedContentColumn, "compress_content")
        };
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM {QuoteIdentifier(tableName)} " +
                              $"WHERE {QuoteIdentifier(createTimeColumn)} >= $start";
        command.Parameters.AddWithValue("$start", startTimestamp);
        if (endTimestamp.HasValue)
        {
            command.CommandText += $" AND {QuoteIdentifier(createTimeColumn)} < $end";
            command.Parameters.AddWithValue("$end", endTimestamp.Value);
        }
        command.CommandText += $" ORDER BY {QuoteIdentifier(createTimeColumn)} DESC";

        var messages = new List<CapturedMessage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localId = ReadInvariantString(reader, "local_id");
            var serverId = ReadInvariantString(reader, "server_id");
            var localType = ReadInt32(reader, "local_type", 1);
            var createTime = ReadInt64(reader, "create_time", 0);
            var realSenderId = ReadInvariantString(reader, "real_sender_id");
            var messageContent = reader["message_content"];
            var compressedContent = reader["compress_content"];
            var decoded = _contentDecoder.DecodeContent(messageContent, 0);
            if (decoded.Length == 0 && compressedContent is not DBNull)
            {
                decoded = _contentDecoder.DecodeContent(compressedContent, 1);
            }

            var isGroup = session.Username.Contains("@chatroom", StringComparison.Ordinal);
            var (senderFromContent, content) = _contentDecoder.ExtractSenderAndContent(
                decoded,
                isGroup,
                nameMap);
            content = _contentDecoder.Summarize(content, localType);

            var senderName = ResolveSenderName(
                realSenderId,
                senderFromContent,
                session.DisplayName,
                nameMap);
            var stableId = $"{session.Username}:{Path.GetFileName(shard.FullPath)}:" +
                           $"{(serverId.Length > 0 ? serverId : localId)}:{createTime}";
            messages.Add(new CapturedMessage(
                Source: "WeChat",
                SourceMessageKey: "WeChat:local:" + stableId,
                ChatId: session.Username,
                ChatName: session.DisplayName.Length > 0 ? session.DisplayName : session.Username,
                SenderName: senderName.Length > 0 ? senderName : "Unknown sender",
                Content: content,
                SentAt: DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, createTime)),
                MessageType: WeChatMessageContentDecoder.ToMessageType(localType)));
        }

        return messages;
    }

    private IReadOnlyDictionary<string, string> ReadContactNames(
        string path,
        WeChatDatabaseKeyLease keyLease)
    {
        using var connection = _accessor.OpenReadOnly(path, keyLease);
        var tableName = TableExists(connection, "contact")
            ? "contact"
            : TableExists(connection, "Contact") ? "Contact" : null;
        if (tableName is null)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.Schema,
                WeChatReaderErrorCode.SchemaUnsupported,
                "The WeChat contact table was not found.");
        }

        var columns = ReadColumns(connection, tableName);
        var usernameColumn = PickColumn(columns, "username", "UserName", "user_name");
        var remarkColumn = PickColumn(columns, "remark", "Remark");
        var nicknameColumn = PickColumn(columns, "nick_name", "NickName", "nickname");
        if (usernameColumn is null)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.Schema,
                WeChatReaderErrorCode.SchemaUnsupported,
                "The WeChat contact table is missing its username column.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectExpression(usernameColumn, "username")}, " +
                              $"{SelectExpression(remarkColumn, "remark", "''")}, " +
                              $"{SelectExpression(nicknameColumn, "nick_name", "''")} " +
                              $"FROM {QuoteIdentifier(tableName)}";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var username = ReadInvariantString(reader, "username");
            if (username.Length == 0)
            {
                continue;
            }

            var remark = ReadInvariantString(reader, "remark");
            var nickname = ReadInvariantString(reader, "nick_name");
            result[username] = remark.Length > 0 ? remark : nickname.Length > 0 ? nickname : username;
        }

        return result;
    }

    private IReadOnlyList<WeChatSession> ReadSessions(
        string path,
        WeChatDatabaseKeyLease keyLease,
        IReadOnlyDictionary<string, string> contactNames)
    {
        using var connection = _accessor.OpenReadOnly(path, keyLease);
        if (!TableExists(connection, "SessionTable"))
        {
            throw new WeChatReaderException(
                WeChatReaderStage.Schema,
                WeChatReaderErrorCode.SchemaUnsupported,
                "The WeChat SessionTable was not found.");
        }

        var columns = ReadColumns(connection, "SessionTable");
        var usernameColumn = PickColumn(columns, "username", "strUsrName", "user_name");
        var timestampColumn = PickColumn(columns, "last_timestamp", "sort_timestamp", "nTime");
        var displayColumn = PickColumn(
            columns,
            "display_name",
            "last_sender_display_name",
            "strNickName",
            "nick_name");
        if (usernameColumn is null)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.Schema,
                WeChatReaderErrorCode.SchemaUnsupported,
                "The WeChat SessionTable is missing its username column.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectExpression(usernameColumn, "username")}, " +
                              $"{SelectExpression(timestampColumn, "last_timestamp", "0")}, " +
                              $"{SelectExpression(displayColumn, "display_name", "''")} " +
                              $"FROM {QuoteIdentifier("SessionTable")} " +
                              $"ORDER BY {QuoteIdentifier(timestampColumn ?? usernameColumn)} DESC";
        var result = new List<WeChatSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var username = ReadInvariantString(reader, "username");
            if (username.Length == 0)
            {
                continue;
            }

            var displayName = contactNames.TryGetValue(username, out var contactName)
                ? contactName
                : ReadInvariantString(reader, "display_name");
            result.Add(new WeChatSession(
                username,
                displayName,
                ReadInt64(reader, "last_timestamp", 0)));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ReadName2IdMap(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TableExists(connection, "Name2Id"))
        {
            return result;
        }

        var columns = ReadColumns(connection, "Name2Id");
        var idColumn = PickColumn(columns, "id");
        var nameColumn = PickColumn(columns, "user_name", "username", "name", "UsrName");
        if (nameColumn is null)
        {
            return result;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid AS __rowid, {SelectExpression(nameColumn, "name")}, " +
                              $"{SelectExpression(idColumn, "id")} FROM {QuoteIdentifier("Name2Id")}";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = ReadInvariantString(reader, "name");
            if (name.Length == 0)
            {
                continue;
            }

            result[ReadInvariantString(reader, "__rowid")] = name;
            var id = ReadInvariantString(reader, "id");
            if (id.Length > 0)
            {
                result[id] = name;
            }
        }

        return result;
    }

    private static long ResolveStartTimestamp(WeChatReadRequest request)
    {
        if (request.Start.HasValue)
        {
            return Math.Max(0, request.Start.Value.ToUnixTimeSeconds());
        }

        var committed = long.TryParse(
            request.CommittedHighWatermark,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? Math.Max(0, parsed)
            : DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var lookback = request.IncrementalLookback ?? TimeSpan.FromSeconds(10);
        return Math.Max(0, committed - Math.Max(0, (long)lookback.TotalSeconds));
    }

    private static WeChatDatabaseDescriptor GetRequired(
        WeChatDatabaseSnapshot snapshot,
        string relativePath) =>
        snapshot.Databases.FirstOrDefault(database =>
            string.Equals(database.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
        ?? throw new WeChatReaderException(
            WeChatReaderStage.PathDiscovery,
            WeChatReaderErrorCode.RequiredDatabaseMissing,
            $"The snapshot is missing {relativePath}.");

    private static string ResolveSenderName(
        string realSenderId,
        string senderFromContent,
        string displayName,
        IReadOnlyDictionary<string, string> nameMap)
    {
        if (realSenderId.Length == 0)
        {
            return senderFromContent.Length > 0 ? senderFromContent : displayName;
        }

        if (!nameMap.TryGetValue(realSenderId, out var resolvedUser))
        {
            return senderFromContent.Length > 0 ? senderFromContent : displayName;
        }

        return nameMap.TryGetValue(resolvedUser, out var resolvedName) && resolvedName.Length > 0
            ? resolvedName
            : resolvedUser.Length > 0 ? resolvedUser : senderFromContent.Length > 0 ? senderFromContent : displayName;
    }

    private static string Md5HexLower(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static IReadOnlyList<string> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        var columns = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string? PickColumn(IReadOnlyList<string> columns, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var column = columns.FirstOrDefault(existing =>
                string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase));
            if (column is not null)
            {
                return column;
            }
        }

        return null;
    }

    private static string SelectExpression(string? column, string alias, string fallback = "NULL") =>
        column is null
            ? $"{fallback} AS {QuoteIdentifier(alias)}"
            : $"{QuoteIdentifier(column)} AS {QuoteIdentifier(alias)}";

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string ReadInvariantString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return "";
        }

        return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)?.Trim() ?? "";
    }

    private static int ReadInt32(SqliteDataReader reader, string name, int fallback)
    {
        var value = ReadInt64(reader, name, fallback);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : fallback;
    }

    private static long ReadInt64(SqliteDataReader reader, string name, long fallback)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return fallback;
        }

        var value = reader.GetValue(ordinal);
        if (value is long longValue)
        {
            return longValue;
        }
        if (value is int intValue)
        {
            return intValue;
        }

        return long.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    private sealed record WeChatSession(
        string Username,
        string DisplayName,
        long LastTimestamp);
}
