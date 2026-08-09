using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>SQLite 提醒仓储，保留延期历史并支持后台任务原子领取。</summary>
public sealed class SqliteReminderRepository : IReminderRepository
{
    private readonly string _databasePath;

    public SqliteReminderRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    internal static async Task<TodoReminder> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TodoReminder reminder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO todo_reminders (
                todo_id, scheduled_at, status, parent_reminder_id, delivered_at,
                snoozed_at, dismissed_at, attempt_count, last_error, created_at, updated_at)
            VALUES (
                $todoId, $scheduledAt, $status, $parentReminderId, $deliveredAt,
                $snoozedAt, $dismissedAt, $attemptCount, $lastError, $createdAt, $updatedAt)
            RETURNING id;
            """;
        AddParameters(command, reminder);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return reminder with { Id = id };
    }

    public async Task<TodoReminder?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await GetByIdAsync(connection, transaction: null, id, cancellationToken);
    }

    internal static async Task<TodoReminder?> GetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{SelectColumns} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReminder(reader) : null;
    }

    public async Task<IReadOnlyList<TodoReminder>> GetForTodoAsync(long todoId, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE todo_id = $todoId ORDER BY created_at, id;";
        command.Parameters.AddWithValue("$todoId", todoId);
        var result = new List<TodoReminder>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadReminder(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<ReminderDispatchItem>> GetDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.todo_id, r.scheduled_at, r.status, r.parent_reminder_id,
                   r.delivered_at, r.snoozed_at, r.dismissed_at, r.attempt_count,
                   r.last_error, r.created_at, r.updated_at, t.title, t.description
            FROM todo_reminders r
            JOIN todo_items t ON t.id = r.todo_id
            WHERE r.status = $scheduled
              AND r.scheduled_at <= $now
              AND t.status IN ($pending, $inProgress, $waiting)
            ORDER BY r.scheduled_at, r.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$pending", TodoStatus.Pending.ToString());
        command.Parameters.AddWithValue("$inProgress", TodoStatus.InProgress.ToString());
        command.Parameters.AddWithValue("$waiting", TodoStatus.Waiting.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        var result = new List<ReminderDispatchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReminderDispatchItem(
                ReadReminder(reader),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }

        return result;
    }

    public async Task<bool> TryClaimAsync(long id, DateTimeOffset claimedAt, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_reminders
            SET status = $dispatching, updated_at = $claimedAt
            WHERE id = $id AND status = $scheduled;
            """;
        command.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        command.Parameters.AddWithValue("$claimedAt", claimedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> RecoverStaleClaimsAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_reminders
            SET status = $scheduled, attempt_count = attempt_count + 1,
                last_error = $lastError, updated_at = $recoveredAt
            WHERE status = $dispatching AND updated_at <= $staleBefore;
            """;
        command.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        command.Parameters.AddWithValue("$lastError", "Recovered stale dispatch claim after application restart.");
        command.Parameters.AddWithValue("$recoveredAt", recoveredAt.ToString("O"));
        command.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        command.Parameters.AddWithValue("$staleBefore", staleBefore.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkDeliveredAsync(long id, DateTimeOffset deliveredAt, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_reminders
            SET status = $delivered, delivered_at = $deliveredAt, updated_at = $deliveredAt, last_error = NULL
            WHERE id = $id AND status = $dispatching;
            """;
        command.Parameters.AddWithValue("$delivered", ReminderStatus.Delivered.ToString());
        command.Parameters.AddWithValue("$deliveredAt", deliveredAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RescheduleAfterFailureAsync(
        long id,
        DateTimeOffset retryAt,
        string sanitizedError,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_reminders
            SET status = $scheduled, scheduled_at = $retryAt, attempt_count = attempt_count + 1,
                last_error = $lastError, updated_at = $updatedAt
            WHERE id = $id AND status = $dispatching;
            """;
        command.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        command.Parameters.AddWithValue("$retryAt", retryAt.ToString("O"));
        command.Parameters.AddWithValue("$lastError", sanitizedError.Length <= 300 ? sanitizedError : sanitizedError[..300]);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectColumns = """
        SELECT id, todo_id, scheduled_at, status, parent_reminder_id, delivered_at,
               snoozed_at, dismissed_at, attempt_count, last_error, created_at, updated_at
        FROM todo_reminders
        """;

    private static void AddParameters(SqliteCommand command, TodoReminder reminder)
    {
        command.Parameters.AddWithValue("$todoId", reminder.TodoId);
        command.Parameters.AddWithValue("$scheduledAt", reminder.ScheduledAt.ToString("O"));
        command.Parameters.AddWithValue("$status", reminder.Status.ToString());
        command.Parameters.AddWithValue("$parentReminderId", reminder.ParentReminderId is null ? DBNull.Value : reminder.ParentReminderId.Value);
        command.Parameters.AddWithValue("$deliveredAt", reminder.DeliveredAt is null ? DBNull.Value : reminder.DeliveredAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$snoozedAt", reminder.SnoozedAt is null ? DBNull.Value : reminder.SnoozedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$dismissedAt", reminder.DismissedAt is null ? DBNull.Value : reminder.DismissedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$attemptCount", reminder.AttemptCount);
        command.Parameters.AddWithValue("$lastError", reminder.LastError is null ? DBNull.Value : reminder.LastError);
        command.Parameters.AddWithValue("$createdAt", reminder.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", reminder.UpdatedAt.ToString("O"));
    }

    internal static TodoReminder ReadReminder(SqliteDataReader reader)
    {
        return new TodoReminder(
            reader.GetInt64(0),
            reader.GetInt64(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            Enum.Parse<ReminderStatus>(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)));
    }
}
