using Microsoft.Data.Sqlite;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 把新消息合并进既有待办的共享逻辑：关联来源消息，
/// 并在新提醒/截止时间更晚时向后平移；消息把时间“确定下来”（那就5点半/改到16点）时，
/// 允许按确认时间改早，取大家最终认同的时间作为事件提醒时间。
/// </summary>
internal static class SqliteTodoMerge
{
    internal sealed record MergeOutcome(bool Linked, bool ReminderRescheduled, bool DueAtUpdated)
    {
        internal static MergeOutcome Empty { get; } = new(false, false, false);
    }

    /// <summary>
    /// 在既有事务内执行合并。
    /// </summary>
    /// <param name="reminderAt">新消息推导出的提醒时间；为空表示本次不调整提醒。</param>
    /// <param name="dueAt">新消息识别出的截止时间；普通消息仅在更晚时更新。</param>
    /// <param name="timeConfirmed">消息是否包含确认表达；为 true 时提醒与截止按确认时间无条件覆盖（仍须晚于当前时间）。</param>
    internal static async Task<MergeOutcome> ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        long messageId,
        DateTimeOffset? reminderAt,
        DateTimeOffset? dueAt,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool timeConfirmed = false)
    {
        var linked = await SqliteTodoRepository.InsertSourceMessageAsync(
            connection,
            transaction,
            todoId,
            messageId,
            isPrimary: false,
            now,
            cancellationToken);

        // 追加来源消息本身就要刷新待办更新时间，保证列表排序与"最近被提醒"一致。
        await TouchTodoAsync(connection, transaction, todoId, now, cancellationToken);

        var reminderRescheduled = reminderAt is { } candidateReminder
            && await RescheduleAsync(connection, transaction, todoId, candidateReminder, now, timeConfirmed, cancellationToken);

        var dueAtUpdated = dueAt is { } candidateDueAt
            && await ApplyDueAtAsync(connection, transaction, todoId, candidateDueAt, timeConfirmed, cancellationToken);

        return new MergeOutcome(linked, reminderRescheduled, dueAtUpdated);
    }

    /// <summary>
    /// 普通消息只在 新提醒严格晚于当前未触发提醒 时改期；
    /// 确认时间（那就5点半/改到16点）代表大家最终认同的事件时间，允许改早，仅在晚于当前时间时写入。
    /// </summary>
    private static async Task<bool> RescheduleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        DateTimeOffset candidate,
        DateTimeOffset now,
        bool timeConfirmed,
        CancellationToken cancellationToken)
    {
        var current = await ReadOpenReminderAsync(connection, transaction, todoId, cancellationToken);
        if (current is null)
        {
            // 没有待触发提醒时，只有在未来才补建，避免登记立即触发或已过期的提醒。
            if (candidate <= now)
            {
                return false;
            }

            await SqliteReminderRepository.InsertAsync(
                connection,
                transaction,
                NewReminder(todoId, candidate, now),
                cancellationToken);
            return true;
        }

        if (!timeConfirmed && candidate <= current.ScheduledAt)
        {
            return false;
        }

        if (timeConfirmed && candidate <= now)
        {
            // 确认的时间已经来不及提醒时保持原提醒，不让待办失去提醒。
            return false;
        }

        await CancelOpenRemindersAsync(connection, transaction, todoId, now, cancellationToken);
        await SqliteReminderRepository.InsertAsync(
            connection,
            transaction,
            NewReminder(todoId, candidate, now),
            cancellationToken);
        return true;
    }

    private static async Task<TodoReminder?> ReadOpenReminderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, todo_id, scheduled_at, status, parent_reminder_id, delivered_at,
                   snoozed_at, dismissed_at, attempt_count, last_error, created_at, updated_at
            FROM todo_reminders
            WHERE todo_id = $todoId AND status IN ($scheduled, $dispatching)
            ORDER BY scheduled_at DESC, id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$todoId", todoId);
        command.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        command.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TodoReminder(
            Id: reader.GetInt64(0),
            TodoId: reader.GetInt64(1),
            ScheduledAt: DateTimeOffset.Parse(reader.GetString(2)),
            Status: Enum.Parse<ReminderStatus>(reader.GetString(3)),
            ParentReminderId: reader.IsDBNull(4) ? null : reader.GetInt64(4),
            DeliveredAt: reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            SnoozedAt: reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            DismissedAt: reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            AttemptCount: reader.GetInt32(8),
            LastError: reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(10)),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(11)));
    }

    private static async Task CancelOpenRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE todo_reminders
            SET status = $cancelled, updated_at = $now
            WHERE todo_id = $todoId AND status IN ($scheduled, $dispatching);
            """;
        command.Parameters.AddWithValue("$cancelled", ReminderStatus.Cancelled.ToString());
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$todoId", todoId);
        command.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        command.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>普通消息仅在更晚时延长截止；确认时间代表大家认同的事件时间，允许改早。</summary>
    private static async Task<bool> ApplyDueAtAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        DateTimeOffset candidate,
        bool timeConfirmed,
        CancellationToken cancellationToken)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT due_at FROM todo_items WHERE id = $id;";
        read.Parameters.AddWithValue("$id", todoId);
        var raw = await read.ExecuteScalarAsync(cancellationToken);
        if (!timeConfirmed
            && raw is string existing
            && DateTimeOffset.TryParse(existing, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var current)
            && candidate <= current)
        {
            return false;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE todo_items SET due_at = $dueAt WHERE id = $id;";
        update.Parameters.AddWithValue("$dueAt", candidate.ToString("O"));
        update.Parameters.AddWithValue("$id", todoId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static async Task TouchTodoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE todo_items SET updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$id", todoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TodoReminder NewReminder(long todoId, DateTimeOffset scheduledAt, DateTimeOffset now) => new(
        0,
        todoId,
        scheduledAt,
        ReminderStatus.Scheduled,
        null,
        null,
        null,
        null,
        0,
        null,
        now,
        now);
}
