using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteMessageRepository : IMessageRepository
{
    private readonly string _databasePath;

    public SqliteMessageRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

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

        command.Parameters.Clear();
        command.CommandText = "SELECT last_insert_rowid();";
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return message with { Id = id };
    }

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

    private static void AddMessageParameters(SqliteCommand command, Message message)
    {
        command.Parameters.AddWithValue("$source", message.Source);
        command.Parameters.AddWithValue("$sourceMessageKey", message.SourceMessageKey);
        command.Parameters.AddWithValue("$chatSessionId", message.ChatSessionId);
        command.Parameters.AddWithValue("$chatName", message.ChatName);
        command.Parameters.AddWithValue("$senderName", message.SenderName);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$messageType", message.MessageType.ToString());
        command.Parameters.AddWithValue("$sentAt", message.SentAt.ToString("O"));
        command.Parameters.AddWithValue("$capturedAt", message.CapturedAt.ToString("O"));
        command.Parameters.AddWithValue("$isMentionMe", message.IsMentionMe ? 1 : 0);
        command.Parameters.AddWithValue("$rawExcerpt", message.Content.Length <= 200 ? message.Content : message.Content[..200]);
    }

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
