using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 石化通本地消息数据库适配器。读取过程不控制窗口，只在后台读取进程内密钥，
/// 随后对数据库及 WAL/SHM 创建临时快照并执行只读查询。
/// </summary>
public sealed class ShihuatongLocalDatabaseCaptureAdapter : IMessageCaptureAdapter
{
    private readonly ShihuatongLocalDatabaseReader _reader;

    public ShihuatongLocalDatabaseCaptureAdapter()
        : this(new ShihuatongLocalDatabaseReader(new ShihuatongProcessDatabaseKeyProvider()))
    {
    }

    internal ShihuatongLocalDatabaseCaptureAdapter(ShihuatongLocalDatabaseReader reader)
    {
        _reader = reader;
    }

    public string Name => "Shihuatong.LocalDatabase";

    public static bool IsProcessRunning => new ShihuatongProcessDatabaseKeyProvider().IsProcessRunning;

    public Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken)
    {
        var currentOffset = context.GetOffset(Name);
        return Task.Run(() => _reader.Capture(currentOffset, cancellationToken), cancellationToken);
    }
}

internal sealed class ShihuatongLocalDatabaseReader
{
    private const int MaximumMessagesPerDatabase = 2000;
    private static readonly JsonSerializerOptions OffsetJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ShihuatongProcessDatabaseKeyProvider _keyProvider;

    public ShihuatongLocalDatabaseReader(ShihuatongProcessDatabaseKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public CaptureBatch Capture(string? currentOffset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var secret = _keyProvider.Acquire();
        var offset = ShihuatongCaptureOffset.Parse(currentOffset);
        var databaseFiles = Directory.EnumerateFiles(secret.DatabaseDirectory, "msg_*.db")
            .Where(path => IsMessageDatabaseName(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var changedFiles = databaseFiles
            .Select(path => new SourceDatabase(path, ShihuatongDatabaseSnapshot.GetSourceStamp(path)))
            .Where(source => !offset.Files.TryGetValue(source.Name, out var cursor) ||
                            !string.Equals(cursor.SourceStamp, source.Stamp, StringComparison.Ordinal))
            .ToArray();

        if (changedFiles.Length == 0)
        {
            return new CaptureBatch("Shihuatong.LocalDatabase", Array.Empty<CapturedMessage>(), offset.ToString());
        }

        using var snapshot = new ShihuatongDatabaseSnapshot();
        var keyText = secret.CreateKeyText();
        try
        {
            var directorySnapshot = snapshot.CopyStable(Path.Combine(secret.DatabaseDirectory, "mdb.db"), cancellationToken);
            var directory = LoadDirectory(directorySnapshot.Path, keyText);
            var messages = new List<CapturedMessage>();

            foreach (var source in changedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var databaseSnapshot = snapshot.CopyStable(source.Path, cancellationToken);
                offset.Files.TryGetValue(source.Name, out var previousCursor);
                var result = ReadMessages(
                    databaseSnapshot.Path,
                    keyText,
                    directory,
                    previousCursor,
                    cancellationToken);
                messages.AddRange(result.Messages);
                result.Cursor.SourceStamp = result.Messages.Count >= MaximumMessagesPerDatabase
                    ? ""
                    : databaseSnapshot.SourceStamp;
                offset.Files[source.Name] = result.Cursor;
            }

            return new CaptureBatch(
                "Shihuatong.LocalDatabase",
                messages.OrderBy(message => message.SentAt).ThenBy(message => message.SourceMessageKey).ToArray(),
                offset.ToString());
        }
        finally
        {
            keyText = "";
        }
    }

    private static ShihuatongMessageReadResult ReadMessages(
        string databasePath,
        string key,
        ShihuatongDirectory directory,
        ShihuatongFileCursor? previousCursor,
        CancellationToken cancellationToken)
    {
        using var connection = OpenEncryptedSnapshot(databasePath, key);
        var cursor = previousCursor is null
            ? CreateInitialCursor(connection)
            : new ShihuatongFileCursor
            {
                LastCreateTime = previousCursor.LastCreateTime,
                LastUuid = previousCursor.LastUuid,
                SourceStamp = previousCursor.SourceStamp
            };

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT uuid, cider, creator, msg_type, create_time,
                   msg_content, search_text, hint_content
            FROM imdb_message_models
            WHERE create_time > $createTime
               OR (create_time = $createTime AND uuid > $uuid)
            ORDER BY create_time, uuid
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$createTime", cursor.LastCreateTime);
        command.Parameters.AddWithValue("$uuid", cursor.LastUuid);
        command.Parameters.AddWithValue("$limit", MaximumMessagesPerDatabase);

        using var reader = command.ExecuteReader();
        var messages = new List<CapturedMessage>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uuid = ReadString(reader, 0);
            if (string.IsNullOrWhiteSpace(uuid)) continue;

            var cider = ReadString(reader, 1);
            var creator = ReadString(reader, 2);
            var nativeMessageType = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var createTime = reader.GetInt64(4);
            var payload = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5);
            var searchText = ReadString(reader, 6);
            var hintContent = ReadString(reader, 7);
            var content = ResolveContent(payload, searchText, hintContent, nativeMessageType);
            var chatName = directory.ResolveChatName(cider);
            var senderName = directory.ResolveSenderName(cider, creator, chatName);

            messages.Add(new CapturedMessage(
                Source: "Shihuatong",
                SourceMessageKey: $"Shihuatong:local:{uuid}",
                ChatId: string.IsNullOrWhiteSpace(cider) ? "unknown-chat" : cider,
                ChatName: chatName,
                SenderName: senderName,
                Content: content,
                SentAt: FromStoneTimestamp(createTime),
                MessageType: ResolveMessageType(content, hintContent)));

            cursor.LastCreateTime = createTime;
            cursor.LastUuid = uuid;
        }

        return new ShihuatongMessageReadResult(messages, cursor);
    }

    private static ShihuatongFileCursor CreateInitialCursor(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(create_time), 0) FROM imdb_message_models;";
        var maximum = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        var startOfToday = new DateTimeOffset(DateTime.Today).ToUniversalTime();
        var rawStart = maximum switch
        {
            > 99_999_999_999_999 => startOfToday.ToUnixTimeMilliseconds() * 1000,
            > 9_999_999_999 => startOfToday.ToUnixTimeMilliseconds(),
            _ => startOfToday.ToUnixTimeSeconds()
        };
        return new ShihuatongFileCursor { LastCreateTime = rawStart - 1, LastUuid = "" };
    }

    private static ShihuatongDirectory LoadDirectory(string databasePath, string key)
    {
        using var connection = OpenEncryptedSnapshot(databasePath, key);
        var chats = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, coid, ccid, ctype, name FROM conversation_db_models;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var nativeKey = ReadString(reader, 0);
                var coid = reader.GetInt64(1);
                var ccid = reader.GetInt64(2);
                var ctype = reader.GetInt64(3);
                var name = ReadString(reader, 4);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!string.IsNullOrWhiteSpace(nativeKey)) chats[nativeKey] = name;
                // 不同石化通版本的 Cider 三段顺序存在差异；同时建立所有无歧义排列。
                foreach (var cider in new[]
                {
                    $"{coid}_{ccid}_{ctype}", $"{ctype}_{coid}_{ccid}",
                    $"{coid}_{ctype}_{ccid}", $"{ccid}_{coid}_{ctype}",
                    $"{ccid}_{ctype}_{coid}", $"{ctype}_{ccid}_{coid}"
                })
                {
                    chats.TryAdd(cider, name);
                }
            }
        }

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, group_oid, group_gid, oid, uid, nick_name, name FROM group_member_db_models;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var nativeKey = ReadString(reader, 0);
                var groupKey = $"{reader.GetInt64(1)}_{reader.GetInt64(2)}_{reader.GetInt64(3)}_{reader.GetInt64(4)}";
                var userKey = $"{reader.GetInt64(3)}_{reader.GetInt64(4)}";
                var displayName = ReadString(reader, 5);
                if (string.IsNullOrWhiteSpace(displayName)) displayName = ReadString(reader, 6);
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                if (!string.IsNullOrWhiteSpace(nativeKey)) members[nativeKey] = displayName;
                members[groupKey] = displayName;
                users[userKey] = displayName;
            }
        }

        return new ShihuatongDirectory(chats, members, users);
    }

    private static SqliteConnection OpenEncryptedSnapshot(string databasePath, string key)
    {
        SqliteConnectionFactory.EnsureSqliteInitialized();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA key = \"{key}\"; PRAGMA cipher_compatibility = 3; PRAGMA query_only = ON;";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            command.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw new InvalidOperationException("石化通本地消息数据库解密失败，当前客户端版本可能已变化。");
        }
    }

    private static string ResolveContent(byte[]? payload, string searchText, string hintContent, int nativeMessageType)
    {
        var content = ShihuatongMessageContentDecoder.TryDecodeText(payload);
        if (string.IsNullOrWhiteSpace(content)) content = searchText.Trim();
        if (string.IsNullOrWhiteSpace(content)) content = hintContent.Trim();
        return string.IsNullOrWhiteSpace(content) ? $"[石化通消息 {nativeMessageType}]" : content;
    }

    private static MessageType ResolveMessageType(string content, string hintContent)
    {
        var value = $"{hintContent} {content}";
        if (value.Contains("图片", StringComparison.OrdinalIgnoreCase)) return MessageType.Image;
        if (value.Contains("文件", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("语音", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("视频", StringComparison.OrdinalIgnoreCase)) return MessageType.File;
        if (value.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("https://", StringComparison.OrdinalIgnoreCase)) return MessageType.Link;
        if (value.Contains("撤回", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("系统消息", StringComparison.OrdinalIgnoreCase)) return MessageType.System;
        return MessageType.Text;
    }

    private static DateTimeOffset FromStoneTimestamp(long value)
    {
        if (value > 99_999_999_999_999)
        {
            var milliseconds = Math.DivRem(value, 1000, out var remainingMicroseconds);
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).AddTicks(remainingMicroseconds * 10);
        }
        return value > 9_999_999_999
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    private static string ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal).Trim();

    private static bool IsMessageDatabaseName(string name) =>
        name.Length == "msg_1.db".Length && name.StartsWith("msg_", StringComparison.OrdinalIgnoreCase) &&
        name[4] is >= '1' and <= '5' && name.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    private sealed record SourceDatabase(string Path, string Stamp)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    private sealed record ShihuatongMessageReadResult(
        IReadOnlyList<CapturedMessage> Messages,
        ShihuatongFileCursor Cursor);
}

internal sealed class ShihuatongDirectory
{
    private readonly IReadOnlyDictionary<string, string> _chats;
    private readonly IReadOnlyDictionary<string, string> _members;

    private readonly IReadOnlyDictionary<string, string> _users;
    public ShihuatongDirectory(
        IReadOnlyDictionary<string, string> chats,
        IReadOnlyDictionary<string, string> members,
        IReadOnlyDictionary<string, string> users)
    {
        _chats = chats;
        _members = members;
        _users = users;
    }

    public string ResolveChatName(string cider) =>
        _chats.TryGetValue(cider, out var name) && !string.IsNullOrWhiteSpace(name) ? name : cider;

    public string ResolveSenderName(string cider, string creator, string chatName)
    {
        var chatParts = cider.Split('_');
        var creatorParts = creator.Split('_');
        if (chatParts.Length == 3 && creatorParts.Length == 3)
        {
            var memberKey = $"{chatParts[0]}_{chatParts[1]}_{creatorParts[1]}_{creatorParts[2]}";
            if (_members.TryGetValue(memberKey, out var name) && !string.IsNullOrWhiteSpace(name)) return name;
            var userKey = $"{creatorParts[1]}_{creatorParts[2]}";
            if (_users.TryGetValue(userKey, out var userName) && !string.IsNullOrWhiteSpace(userName)) return userName;
        }

        if (!string.IsNullOrWhiteSpace(creator)) return creator;
        return string.IsNullOrWhiteSpace(chatName) ? "未知发送人" : chatName;
    }
}

internal sealed class ShihuatongCaptureOffset
{
    public Dictionary<string, ShihuatongFileCursor> Files { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public static ShihuatongCaptureOffset Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new ShihuatongCaptureOffset();
        try
        {
            var parsed = JsonSerializer.Deserialize<ShihuatongCaptureOffset>(value, OffsetJsonOptions);
            if (parsed is not null)
            {
                parsed.Files = new Dictionary<string, ShihuatongFileCursor>(
                    parsed.Files ?? new Dictionary<string, ShihuatongFileCursor>(),
                    StringComparer.OrdinalIgnoreCase);
                return parsed;
            }
        }
        catch (JsonException)
        {
        }
        return new ShihuatongCaptureOffset();
    }

    public override string ToString() => JsonSerializer.Serialize(this, OffsetJsonOptions);

    private static readonly JsonSerializerOptions OffsetJsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed class ShihuatongFileCursor
{
    public long LastCreateTime { get; set; }
    public string LastUuid { get; set; } = "";
    public string SourceStamp { get; set; } = "";
}

internal sealed class ShihuatongDatabaseSnapshot : IDisposable
{
    private readonly string _baseDirectory;
    private readonly string _snapshotDirectory;

    public ShihuatongDatabaseSnapshot()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), "WechatDashboard", "ShihuatongSnapshots");
        _snapshotDirectory = Path.Combine(_baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_snapshotDirectory);
    }

    public SnapshotFile CopyStable(string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("石化通数据库文件不存在。", sourcePath);
        var targetPath = Path.Combine(_snapshotDirectory, Path.GetFileName(sourcePath));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = GetSourceStamp(sourcePath);
            CopyOne(sourcePath, targetPath);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sourceSidecar = sourcePath + suffix;
                var targetSidecar = targetPath + suffix;
                if (File.Exists(sourceSidecar)) CopyOne(sourceSidecar, targetSidecar);
                else if (File.Exists(targetSidecar)) File.Delete(targetSidecar);
            }
            var after = GetSourceStamp(sourcePath);
            if (string.Equals(before, after, StringComparison.Ordinal)) return new SnapshotFile(targetPath, after);
        }
        throw new IOException("石化通数据库正在持续写入，无法取得一致快照，请稍后重试。");
    }

    public static string GetSourceStamp(string sourcePath)
    {
        return string.Join("|", new[] { "", "-wal", "-shm" }.Select(suffix =>
        {
            var path = sourcePath + suffix;
            if (!File.Exists(path)) return "missing";
            var info = new FileInfo(path);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
    }

    private static void CopyOne(string sourcePath, string targetPath)
    {
        using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 128, FileOptions.SequentialScan);
        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128);
        input.CopyTo(output);
    }

    public void Dispose()
    {
        var resolvedBase = Path.GetFullPath(_baseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedSnapshot = Path.GetFullPath(_snapshotDirectory);
        if (resolvedSnapshot.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolvedSnapshot))
        {
            Directory.Delete(resolvedSnapshot, recursive: true);
        }
    }

    internal sealed record SnapshotFile(string Path, string SourceStamp);
}
