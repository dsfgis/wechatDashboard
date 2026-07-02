using System.Globalization;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的消息仓储实现。
/// 负责消息的插入、去重判断、最近查询与分页查询。
/// </summary>
public sealed class SqliteMessageRepository : IMessageRepository
{
    private readonly string _databasePath;

    public SqliteMessageRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>插入一条消息，返回带自增 Id 的实体。</summary>
    public async Task<Message> SaveAsync(Message message, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO messages (
                source,
                source_message_key,
                chat_session_id,
                chat_name,
                sender_name,
                content,
                message_type,
                sent_at,
                captured_at,
                is_mention_me,
                raw_excerpt
            )
            VALUES (
                $source,
                $sourceMessageKey,
                $chatSessionId,
                $chatName,
                $senderName,
                $content,
                $messageType,
                $sentAt,
                $capturedAt,
                $isMentionMe,
                $rawExcerpt
            );
            """;
        AddMessageParameters(command, message);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 读取自增主键并回填到实体
        command.Parameters.Clear();
        command.CommandText = "SELECT last_insert_rowid();";
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return message with { Id = id };
    }

    /// <summary>判断指定来源+消息键是否已存在（去重）。</summary>
    public async Task<bool> ExistsAsync(string source, string sourceMessageKey, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM messages
            WHERE source = $source AND source_message_key = $sourceMessageKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$sourceMessageKey", sourceMessageKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    /// <summary>获取最近 N 条消息（按采集时间倒序）。</summary>
    public async Task<IReadOnlyList<Message>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                source,
                source_message_key,
                chat_session_id,
                chat_name,
                sender_name,
                content,
                message_type,
                sent_at,
                captured_at,
                is_mention_me
            FROM messages
            ORDER BY captured_at DESC, id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var messages = new List<Message>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }

    /// <summary>
    /// 分页查询消息（按采集时间倒序）。
    /// 在后台线程执行以避免阻塞 UI；内部先 COUNT 再取页数据。
    /// </summary>
    public Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            // 页码与每页大小做边界保护
            var safePage = Math.Max(1, pageNumber);
            var safeSize = Math.Clamp(pageSize, 1, 200);
            var offset = (safePage - 1) * safeSize;

            await using var connection = SqliteConnectionFactory.Open(_databasePath);

            // 先查总数
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM messages;";
            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            // 再查本页
            var messages = await ReadPageAsync(connection, safeSize, offset, cancellationToken);
            return new MessagePage(messages, totalCount, safePage, safeSize);
        }, cancellationToken);
    }

    /// <summary>
    /// 分页查询消息，但跳过 COUNT 查询（总数由调用方传入）。
    /// 适用于已知总数、仅需翻页的高频场景。
    /// </summary>
    public Task<MessagePage> GetPageWithKnownCountAsync(int pageNumber, int pageSize, int totalCount, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var safePage = Math.Max(1, pageNumber);
            var safeSize = Math.Clamp(pageSize, 1, 200);
            var offset = (safePage - 1) * safeSize;

            await using var connection = SqliteConnectionFactory.Open(_databasePath);
            var messages = await ReadPageAsync(connection, safeSize, offset, cancellationToken);
            return new MessagePage(messages, totalCount, safePage, safeSize);
        }, cancellationToken);
    }

    /// <summary>获取消息总数。</summary>
    public Task<int> GetMessageCountAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            await using var connection = SqliteConnectionFactory.Open(_databasePath);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM messages;";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    /// <summary>
    /// 读取指定页数据（LIMIT/OFFSET），按采集时间倒序。
    /// </summary>
    private static async Task<List<Message>> ReadPageAsync(SqliteConnection connection, int limit, int offset, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                source,
                source_message_key,
                chat_session_id,
                chat_name,
                sender_name,
                content,
                message_type,
                sent_at,
                captured_at,
                is_mention_me
            FROM messages
            ORDER BY captured_at DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var messages = new List<Message>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(ReadMessage(reader));
        }
        return messages;
    }

    /// <summary>绑定消息参数到 INSERT 命令。</summary>
    private static void AddMessageParameters(SqliteCommand command, Message message)
    {
        command.Parameters.AddWithValue("$source", message.Source);
        command.Parameters.AddWithValue("$sourceMessageKey", message.SourceMessageKey);
        command.Parameters.AddWithValue("$chatSessionId", message.ChatSessionId);
        command.Parameters.AddWithValue("$chatName", message.ChatName);
        command.Parameters.AddWithValue("$senderName", message.SenderName);
        command.Parameters.AddWithValue("$content", message.Content);
        // 枚举以字符串形式存储
        command.Parameters.AddWithValue("$messageType", message.MessageType.ToString());
        // 时间以 ISO-8601（"O"）格式存储，保证可往返
        command.Parameters.AddWithValue("$sentAt", message.SentAt.ToString("O"));
        command.Parameters.AddWithValue("$capturedAt", message.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$isMentionMe", message.IsMentionMe ? 1 : 0);
        // 原始摘要截断到 200 字
        command.Parameters.AddWithValue("$rawExcerpt", message.Content.Length <= 200 ? message.Content : message.Content[..200]);
    }

    /// <summary>从 DataReader 映射为 Message 实体。</summary>
    private static Message ReadMessage(SqliteDataReader reader)
    {
        return new Message(
            Id: reader.GetInt64(0),
            Source: reader.GetString(1),
            SourceMessageKey: reader.GetString(2),
            ChatSessionId: reader.GetInt64(3),
            ChatName: reader.GetString(4),
            SenderName: reader.GetString(5),
            Content: reader.GetString(6),
            MessageType: Enum.Parse<MessageType>(reader.GetString(7)),
            SentAt: DateTimeOffset.Parse(reader.GetString(8)),
            CapturedAt: DateTimeOffset.Parse(reader.GetString(9)),
            IsMentionMe: reader.GetInt32(10) == 1);
    }
}
