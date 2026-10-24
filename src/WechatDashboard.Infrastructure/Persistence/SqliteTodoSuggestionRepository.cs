using System.Globalization;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Agent;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteTodoSuggestionRepository(string databasePath) : ITodoSuggestionRepository
{
    public async Task<TodoSuggestion?> GetAsync(long messageId, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT message_id, decision, priority, title, description, due_at, reason, question, review_status, analyzed_at FROM todo_suggestions WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyDictionary<long, TodoSuggestion>> GetByMessageIdsAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0) return new Dictionary<long, TodoSuggestion>();
        await using var connection = SqliteConnectionFactory.Open(databasePath);
        await using var command = connection.CreateCommand();
        var ids = AddIds(command, messageIds);
        command.CommandText = $"SELECT message_id, decision, priority, title, description, due_at, reason, question, review_status, analyzed_at FROM todo_suggestions WHERE message_id IN ({ids});";
        var result = new Dictionary<long, TodoSuggestion>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var suggestion = Read(reader);
            result[suggestion.MessageId] = suggestion;
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<long, PriorityLevel>> GetEffectivePrioritiesAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0) return new Dictionary<long, PriorityLevel>();
        await using var connection = SqliteConnectionFactory.Open(databasePath);
        await using var command = connection.CreateCommand();
        var ids = AddIds(command, messageIds);
        command.CommandText = $"""
            SELECT m.id, COALESCE(s.priority,
                (SELECT u.priority FROM urgency_scores u WHERE u.message_id = m.id ORDER BY u.id DESC LIMIT 1),
                'P3')
            FROM messages m LEFT JOIN todo_suggestions s ON s.message_id = m.id
            WHERE m.id IN ({ids});
            """;
        var result = new Dictionary<long, PriorityLevel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var raw = reader.GetString(1);
            result[reader.GetInt64(0)] = Enum.TryParse<PriorityLevel>(raw, out var priority)
                && Enum.IsDefined(priority) ? priority : PriorityLevel.P3;
        }
        return result;
    }

    public async Task SaveAsync(TodoSuggestion suggestion, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO todo_suggestions
                (message_id, decision, priority, title, description, due_at, reason, question, review_status, analyzed_at)
            VALUES ($id, $decision, $priority, $title, $description, $dueAt, $reason, $question, $status, $at)
            ON CONFLICT(message_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", suggestion.MessageId);
        command.Parameters.AddWithValue("$decision", suggestion.Decision.ToString());
        command.Parameters.AddWithValue("$priority", suggestion.Priority.ToString());
        command.Parameters.AddWithValue("$title", (object?)suggestion.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)suggestion.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$dueAt", (object?)suggestion.DueAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", suggestion.Reason);
        command.Parameters.AddWithValue("$question", (object?)suggestion.Question ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", suggestion.ReviewStatus.ToString());
        command.Parameters.AddWithValue("$at", suggestion.AnalyzedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplacePendingAsync(TodoSuggestion suggestion, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_suggestions SET decision = $decision, priority = $priority, title = $title,
                description = $description, due_at = $dueAt, reason = $reason, question = $question,
                analyzed_at = $at
            WHERE message_id = $id AND decision = 'AskUser' AND review_status = 'Pending';
            """;
        command.Parameters.AddWithValue("$id", suggestion.MessageId);
        command.Parameters.AddWithValue("$decision", suggestion.Decision.ToString());
        command.Parameters.AddWithValue("$priority", suggestion.Priority.ToString());
        command.Parameters.AddWithValue("$title", (object?)suggestion.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)suggestion.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$dueAt", (object?)suggestion.DueAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", suggestion.Reason);
        command.Parameters.AddWithValue("$question", (object?)suggestion.Question ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", suggestion.AnalyzedAt.ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("澄清问题已处理，请刷新消息后重试。");
    }

    public async Task SetReviewStatusAsync(long messageId, TodoSuggestionReviewStatus status, CancellationToken cancellationToken)
    {
        if (status == TodoSuggestionReviewStatus.Pending) throw new ArgumentOutOfRangeException(nameof(status));
        await using var connection = SqliteConnectionFactory.Open(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE todo_suggestions SET review_status = $status WHERE message_id = $id AND review_status = 'Pending';";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$id", messageId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("待办建议已处理或不存在。");
    }

    private static string AddIds(SqliteCommand command, IReadOnlyCollection<long> messageIds)
    {
        var names = new List<string>(messageIds.Count);
        var index = 0;
        foreach (var id in messageIds.Distinct())
        {
            var name = "$id" + index++;
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }
        return string.Join(",", names);
    }

    private static TodoSuggestion Read(SqliteDataReader reader)
    {
        return new TodoSuggestion(
            reader.GetInt64(0),
            Enum.Parse<MessageActionDecision>(reader.GetString(1)),
            Enum.Parse<PriorityLevel>(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            Enum.Parse<TodoSuggestionReviewStatus>(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));
    }
}
