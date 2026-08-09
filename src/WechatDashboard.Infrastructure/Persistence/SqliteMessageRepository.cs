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
        return await TryInsertAsync(connection, transaction: null, message, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Message '{message.Source}:{message.SourceMessageKey}' already exists.");
    }

    /// <summary>
    /// 在指定连接/事务内原子插入消息；唯一键已存在时返回 null。
    /// </summary>
    internal static async Task<Message?> TryInsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Message message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
            )
            ON CONFLICT(source, source_message_key) DO NOTHING
            RETURNING id;
            """;
        AddMessageParameters(command, message);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return null;
        }

        return message with { Id = Convert.ToInt64(result, CultureInfo.InvariantCulture) };
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

    /// <summary>获取最近 N 条消息（按发送时间倒序）。</summary>
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
            ORDER BY sent_at DESC, id DESC
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

    /// <summary>按消息 ID 集合精确查询消息，自动分批避免 SQLite 参数数量限制。</summary>
    public async Task<IReadOnlyList<Message>> GetByIdsAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        var distinctIds = ids
            .Where(id => id > 0)
            .Distinct()
            .ToArray();

        if (distinctIds.Length == 0)
        {
            return Array.Empty<Message>();
        }

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        var messages = new List<Message>(distinctIds.Length);

        foreach (var batch in distinctIds.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var placeholders = batch.Select((_, index) => $"$id{index}").ToArray();

            command.CommandText = $"""
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
                WHERE id IN ({string.Join(", ", placeholders)});
                """;

            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue(placeholders[index], batch[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                messages.Add(ReadMessage(reader));
            }
        }

        return messages;
    }

    public async Task<IReadOnlyList<Message>> GetBySourceKeysAsync(
        IReadOnlyCollection<MessageIdentity> identities,
        CancellationToken cancellationToken)
    {
        var distinct = identities
            .Where(item => !string.IsNullOrWhiteSpace(item.Source) && !string.IsNullOrWhiteSpace(item.SourceMessageKey))
            .Distinct()
            .ToArray();
        if (distinct.Length == 0)
        {
            return Array.Empty<Message>();
        }

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        var result = new List<Message>(distinct.Length);
        foreach (var batch in distinct.Chunk(200))
        {
            await using var command = connection.CreateCommand();
            var predicates = new List<string>(batch.Length);
            for (var index = 0; index < batch.Length; index++)
            {
                predicates.Add($"(source = $source{index} AND source_message_key = $key{index})");
                command.Parameters.AddWithValue($"$source{index}", batch[index].Source);
                command.Parameters.AddWithValue($"$key{index}", batch[index].SourceMessageKey);
            }

            command.CommandText = $"""
                SELECT id, source, source_message_key, chat_session_id, chat_name,
                       sender_name, content, message_type, sent_at, captured_at, is_mention_me
                FROM messages
                WHERE {string.Join(" OR ", predicates)};
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(ReadMessage(reader));
            }
        }

        return result;
    }

    public async Task<Message?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await GetByIdAsync(connection, transaction: null, id, cancellationToken);
    }

    internal static async Task<Message?> GetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, source, source_message_key, chat_session_id, chat_name,
                   sender_name, content, message_type, sent_at, captured_at, is_mention_me
            FROM messages
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMessage(reader) : null;
    }

    public async Task<MessageContext?> GetContextAsync(
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        var anchor = await GetByIdAsync(connection, transaction: null, messageId, cancellationToken);
        if (anchor is null)
        {
            return null;
        }

        var messages = new List<Message> { anchor };
        messages.AddRange(await ReadRelativeAsync(connection, anchor, Math.Max(0, after), newer: true, cancellationToken));
        messages.AddRange(await ReadRelativeAsync(connection, anchor, Math.Max(0, before), newer: false, cancellationToken));
        var ordered = messages
            .DistinctBy(message => message.Id)
            .OrderByDescending(message => message.SentAt)
            .ThenByDescending(message => message.Id)
            .ToArray();
        return new MessageContext(anchor, ordered);
    }

    private static async Task<IReadOnlyList<Message>> ReadRelativeAsync(
        SqliteConnection connection,
        Message anchor,
        int limit,
        bool newer,
        CancellationToken cancellationToken)
    {
        if (limit == 0)
        {
            return Array.Empty<Message>();
        }

        await using var command = connection.CreateCommand();
        var comparison = newer ? ">" : "<";
        var order = newer ? "ASC" : "DESC";
        command.CommandText = $"""
            SELECT id, source, source_message_key, chat_session_id, chat_name,
                   sender_name, content, message_type, sent_at, captured_at, is_mention_me
            FROM messages
            WHERE sent_at {comparison} $sentAt
               OR (sent_at = $sentAt AND id {comparison} $id)
            ORDER BY sent_at {order}, id {order}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sentAt", anchor.SentAt.ToString("O"));
        command.Parameters.AddWithValue("$id", anchor.Id);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<Message>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadMessage(reader));
        }

        return result;
    }

    /// <summary>
    /// 分页查询消息（按发送时间倒序）。
    /// 在后台线程执行以避免阻塞 UI；内部先 COUNT 再取页数据。
    /// </summary>
    public Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        return GetPageAsync(pageNumber, pageSize, chatNames: null, cancellationToken);
    }

    /// <summary>
    /// 分页查询消息并按指定群名过滤（仅返回关注群的消息），按发送时间倒序。
    /// chatNames 为 null 或空时退化为不过滤。
    /// </summary>
    public Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, IReadOnlyCollection<string>? chatNames, CancellationToken cancellationToken)
    {
        return GetPageAsync(pageNumber, pageSize, chatNames, include: true, cancellationToken);
    }

    /// <summary>
    /// 分页查询消息并按指定群名过滤，按发送时间倒序。
    /// include=true 时仅返回列表内群（白名单），include=false 时排除列表内群（黑名单）。
    /// chatNames 为 null 或空时退化为不过滤。
    /// </summary>
    public Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, IReadOnlyCollection<string>? chatNames, bool include, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            // 页码与每页大小做边界保护
            var safePage = Math.Max(1, pageNumber);
            var safeSize = Math.Clamp(pageSize, 1, 200);
            var offset = (safePage - 1) * safeSize;

            await using var connection = SqliteConnectionFactory.Open(_databasePath);
            var whereClause = BuildChatNameFilter(chatNames, include);

            // 先查总数
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = string.IsNullOrEmpty(whereClause)
                ? "SELECT COUNT(*) FROM messages;"
                : $"SELECT COUNT(*) FROM messages WHERE {whereClause};";
            AddChatNameParameters(countCommand, chatNames);
            var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            // 再查本页
            var messages = await ReadPageAsync(connection, safeSize, offset, chatNames, include, cancellationToken);
            return new MessagePage(messages, totalCount, safePage, safeSize);
        }, cancellationToken);
    }

    /// <summary>
    /// 分页查询消息，但跳过 COUNT 查询（总数由调用方传入）。
    /// 适用于已知总数、仅需翻页的高频场景。
    /// </summary>
    public Task<MessagePage> GetPageWithKnownCountAsync(int pageNumber, int pageSize, int totalCount, CancellationToken cancellationToken)
    {
        return GetPageWithKnownCountAsync(pageNumber, pageSize, totalCount, chatNames: null, include: true, cancellationToken);
    }

    /// <summary>
    /// 分页查询消息并按指定群名过滤，跳过 COUNT 查询（总数由调用方传入）。
    /// include=true 时仅返回列表内群（白名单），include=false 时排除列表内群（黑名单）。
    /// </summary>
    public Task<MessagePage> GetPageWithKnownCountAsync(int pageNumber, int pageSize, int totalCount, IReadOnlyCollection<string>? chatNames, bool include, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var safePage = Math.Max(1, pageNumber);
            var safeSize = Math.Clamp(pageSize, 1, 200);
            var offset = (safePage - 1) * safeSize;

            await using var connection = SqliteConnectionFactory.Open(_databasePath);
            var messages = await ReadPageAsync(connection, safeSize, offset, chatNames, include, cancellationToken);
            return new MessagePage(messages, totalCount, safePage, safeSize);
        }, cancellationToken);
    }

    /// <summary>获取消息总数。</summary>
    public Task<int> GetMessageCountAsync(CancellationToken cancellationToken)
    {
        return GetMessageCountAsync(chatNames: null, cancellationToken);
    }

    /// <summary>获取指定群名集合内的消息总数。chatNames 为 null 或空时返回全部消息总数。</summary>
    public Task<int> GetMessageCountAsync(IReadOnlyCollection<string>? chatNames, CancellationToken cancellationToken)
    {
        return GetMessageCountAsync(chatNames, include: true, cancellationToken);
    }

    /// <summary>获取消息总数。include=true 时统计列表内群，false 时统计列表外群。</summary>
    public Task<int> GetMessageCountAsync(IReadOnlyCollection<string>? chatNames, bool include, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            await using var connection = SqliteConnectionFactory.Open(_databasePath);
            var whereClause = BuildChatNameFilter(chatNames, include);
            await using var command = connection.CreateCommand();
            command.CommandText = string.IsNullOrEmpty(whereClause)
                ? "SELECT COUNT(*) FROM messages;"
                : $"SELECT COUNT(*) FROM messages WHERE {whereClause};";
            AddChatNameParameters(command, chatNames);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    /// <summary>
    /// 构建 chat_name 过滤子句。
    /// include=true：chat_name IN (...)（白名单）
    /// include=false：chat_name NOT IN (...)（黑名单）
    /// chatNames 为空或 null 时返回空字符串（不过滤）。
    /// </summary>
    private static string BuildChatNameFilter(IReadOnlyCollection<string>? chatNames, bool include)
    {
        if (chatNames is null || chatNames.Count == 0)
        {
            return string.Empty;
        }

        var names = chatNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            return string.Empty;
        }

        var placeholders = new List<string>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            placeholders.Add($"$cn{i}");
        }
        var op = include ? "IN" : "NOT IN";
        return $"chat_name {op} ({string.Join(", ", placeholders)})";
    }

    /// <summary>将 chatNames 按占位符顺序绑定到命令参数。</summary>
    private static void AddChatNameParameters(SqliteCommand command, IReadOnlyCollection<string>? chatNames)
    {
        if (chatNames is null || chatNames.Count == 0)
        {
            return;
        }

        var idx = 0;
        foreach (var name in chatNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            command.Parameters.AddWithValue($"$cn{idx}", name);
            idx++;
        }
    }

    /// <summary>
    /// 读取指定页数据（LIMIT/OFFSET），按发送时间倒序。
    /// </summary>
    private static async Task<List<Message>> ReadPageAsync(SqliteConnection connection, int limit, int offset, IReadOnlyCollection<string>? chatNames, bool include, CancellationToken cancellationToken)
    {
        var whereClause = BuildChatNameFilter(chatNames, include);
        await using var command = connection.CreateCommand();
        var hasFilter = !string.IsNullOrEmpty(whereClause);
        command.CommandText = hasFilter
            ? $"""
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
            WHERE {whereClause}
            ORDER BY sent_at DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """
            : """
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
            ORDER BY sent_at DESC, id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        AddChatNameParameters(command, chatNames);

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
