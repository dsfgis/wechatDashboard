using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Reminders;
using WechatDashboard.Application.Todos;
using WechatDashboard.Application.Urgency;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>手动 Todo 与提醒生命周期的 SQLite 原子边界。</summary>
public sealed class SqliteTodoUnitOfWork : ITodoUnitOfWork
{
    private readonly string _databasePath;

    public SqliteTodoUnitOfWork(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<CreateTodoResult> CreateFromMessageAsync(
        CreateTodoFromMessageRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        var message = await SqliteMessageRepository.GetByIdAsync(connection, transaction, request.MessageId, cancellationToken);
        if (message is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateTodoResult(CreateTodoOutcome.SourceMessageMissing, null);
        }

        var existing = await SqliteTodoRepository.GetBySourceMessageIdAsync(
            connection,
            transaction,
            request.MessageId,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CreateTodoResult(CreateTodoOutcome.ExistingTodo, existing);
        }

        // 仅手动点击携带 AgentPriority；评分与待办同事务写入，任何后续失败都会一起回滚。
        if (request.AgentPriority is { } agentPriority)
        {
            var agentUrgency = UrgencyRanker.ForManualAgentAnalysis(message.Id, agentPriority);
            await SqliteMessageRepository.InsertUrgencyAsync(
                connection,
                transaction,
                agentUrgency,
                cancellationToken);
        }

        var suggestedProjectId = await ReadSuggestedProjectIdAsync(connection, transaction, request.MessageId, cancellationToken);
        var suggestedPriority = await ReadSuggestedPriorityAsync(connection, transaction, request.MessageId, cancellationToken);
        var draft = TodoFactory.CreateFromMessage(
            message,
            suggestedProjectId,
            suggestedPriority,
            now,
            request.Title,
            request.Description,
            request.ProjectId,
            request.Priority,
            request.DueAt ?? request.AnalysisDueAt,
            isAutoCreated: false,
            managedProjectId: request.ManagedProjectId);
        var savedTodo = await SqliteTodoRepository.InsertAsync(connection, transaction, draft.Todo, cancellationToken);
        // 主来源消息写入关联表，后续跨群提醒可以通过同一张表追加更多来源。
        await SqliteTodoRepository.InsertSourceMessageAsync(
            connection,
            transaction,
            savedTodo.Id,
            message.Id,
            isPrimary: true,
            now,
            cancellationToken);

        // 手动创建时优先使用显式提醒时间；未显式设置但存在截止时间时，按会议/任务规则自动提醒。
        var reminderAt = request.FirstReminderAt ?? draft.FirstReminderAt ?? TodoService.ResolveAutomaticFirstReminderAt(
            message.Content,
            savedTodo.DueAt,
            now,
            TimeZoneInfo.Local);
        if (reminderAt is { } scheduledAt)
        {
            await SqliteReminderRepository.InsertAsync(
                connection,
                transaction,
                NewReminder(savedTodo.Id, scheduledAt, parentReminderId: null, now),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreateTodoResult(CreateTodoOutcome.Created, savedTodo);
    }

    public async Task<MergeTodoResult> MergeIntoExistingAsync(
        MergeTodoRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        var message = await SqliteMessageRepository.GetByIdAsync(connection, transaction, request.MessageId, cancellationToken);
        var todo = await SqliteTodoRepository.GetByIdAsync(connection, transaction, request.TodoId, cancellationToken);
        // 已办/忽略的待办不参与合并，调用方应改为新建待办。
        if (message is null || todo is null || todo.Status is TodoStatus.Done or TodoStatus.Ignored)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MergeTodoResult(false, false, null);
        }

        var outcome = await SqliteTodoMerge.ApplyAsync(
            connection,
            transaction,
            todo.Id,
            message.Id,
            request.ReminderAt,
            request.DueAt,
            now,
            cancellationToken);
        var updated = await SqliteTodoRepository.GetByIdAsync(connection, transaction, todo.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // 关联已存在（重复合并同一条消息）仍视为成功，只是不产生新的来源记录。
        return new MergeTodoResult(true, outcome.ReminderRescheduled, updated);
    }

    public async Task<CreateManualTodoResult> CreateManualAsync(
        CreateManualTodoRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        if (request.GroupId is { } groupId &&
            await SqliteTodoGroupRepository.GetByIdAsync(connection, transaction, groupId, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateManualTodoResult(CreateManualTodoOutcome.GroupMissing, null);
        }

        if (request.ManagedProjectId is { } managedProjectId &&
            !await FollowedProjectExistsAsync(connection, transaction, managedProjectId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CreateManualTodoResult(CreateManualTodoOutcome.ProjectMissing, null);
        }

        var todo = new TodoItem(
            Id: 0,
            SourceMessageId: null,
            ProjectId: null,
            Title: request.Title,
            Description: request.Description,
            Status: TodoStatus.Pending,
            Priority: request.Priority ?? PriorityLevel.P2,
            DueAt: request.DueAt,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null,
            IsAutoCreated: false)
        {
            GroupId = request.GroupId,
            ManagedProjectId = request.ManagedProjectId,
            IsPinned = false
        };
        var savedTodo = await SqliteTodoRepository.InsertAsync(connection, transaction, todo, cancellationToken);
        // 手动无来源待办只有用户显式设置时才创建提醒，不从标题猜测时间。
        if (request.FirstReminderAt is { } reminderAt)
        {
            await SqliteReminderRepository.InsertAsync(
                connection,
                transaction,
                NewReminder(savedTodo.Id, reminderAt, parentReminderId: null, now),
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreateManualTodoResult(CreateManualTodoOutcome.Created, savedTodo);
    }

    public async Task<SnoozeReminderResult> SnoozeReminderAsync(
        long reminderId,
        DateTimeOffset snoozeUntil,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        var current = await SqliteReminderRepository.GetByIdAsync(connection, transaction, reminderId, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SnoozeReminderResult(false, null, "提醒不存在。 ");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE todo_reminders
            SET status = $snoozed, snoozed_at = $now, updated_at = $now
            WHERE id = $id AND status IN ($scheduled, $dispatching, $delivered);
            """;
        update.Parameters.AddWithValue("$snoozed", ReminderStatus.Snoozed.ToString());
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$id", reminderId);
        update.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        update.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        update.Parameters.AddWithValue("$delivered", ReminderStatus.Delivered.ToString());
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new SnoozeReminderResult(false, null, "当前提醒状态不能延后。 ");
        }

        var replacement = await SqliteReminderRepository.InsertAsync(
            connection,
            transaction,
            NewReminder(current.TodoId, snoozeUntil, current.Id, now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SnoozeReminderResult(true, replacement, null);
    }

    public async Task<TodoItem?> UpdateTodoAsync(
        UpdateTodoRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        if (request.GroupId is { } groupId &&
            await SqliteTodoGroupRepository.GetByIdAsync(connection, transaction, groupId, cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }


        if (request.ManagedProjectId is { } managedProjectId &&
            !await FollowedProjectExistsAsync(connection, transaction, managedProjectId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE todo_items
            SET title = $title, description = $description, project_id = $projectId, group_id = $groupId,
                managed_project_id = $managedProjectId,
                priority = $priority, due_at = $dueAt, status = $status,
                updated_at = $now, completed_at = $completedAt,
                is_pinned = CASE WHEN $status IN ($done, $ignored) THEN 0 ELSE is_pinned END
            WHERE id = $id AND updated_at = $expectedUpdatedAt;
            """;
        update.Parameters.AddWithValue("$title", request.Title);
        update.Parameters.AddWithValue("$description", request.Description is null ? DBNull.Value : request.Description);
        update.Parameters.AddWithValue("$projectId", request.ProjectId is null ? DBNull.Value : request.ProjectId.Value);
        update.Parameters.AddWithValue("$groupId", request.GroupId is null ? DBNull.Value : request.GroupId.Value);
        update.Parameters.AddWithValue("$managedProjectId", request.ManagedProjectId is null ? DBNull.Value : request.ManagedProjectId.Value);
        update.Parameters.AddWithValue("$priority", request.Priority.ToString());
        update.Parameters.AddWithValue("$dueAt", request.DueAt is null ? DBNull.Value : request.DueAt.Value.ToString("O"));
        update.Parameters.AddWithValue("$status", request.Status.ToString());
        update.Parameters.AddWithValue("$done", TodoStatus.Done.ToString());
        update.Parameters.AddWithValue("$ignored", TodoStatus.Ignored.ToString());
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$completedAt", request.Status == TodoStatus.Done ? now.ToString("O") : DBNull.Value);
        update.Parameters.AddWithValue("$id", request.TodoId);
        update.Parameters.AddWithValue("$expectedUpdatedAt", request.ExpectedUpdatedAt.ToString("O"));
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (request.Status is TodoStatus.Done or TodoStatus.Ignored)
        {
            await CancelOpenRemindersAsync(connection, transaction, request.TodoId, now, cancellationToken);
        }

        var updated = await SqliteTodoRepository.GetByIdAsync(connection, transaction, request.TodoId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task<bool> FollowedProjectExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM followed_projects WHERE id = $id AND is_active = 1;";
        command.Parameters.AddWithValue("$id", projectId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<TodoReminder?> ScheduleReminderAsync(
        long todoId,
        DateTimeOffset scheduledAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        var todo = await SqliteTodoRepository.GetByIdAsync(connection, transaction, todoId, cancellationToken);
        if (todo is null || todo.Status is TodoStatus.Done or TodoStatus.Ignored)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await CancelOpenRemindersAsync(connection, transaction, todoId, now, cancellationToken);
        var reminder = await SqliteReminderRepository.InsertAsync(
            connection,
            transaction,
            NewReminder(todoId, scheduledAt, parentReminderId: null, now),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reminder;
    }

    private static async Task CancelOpenRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var cancel = connection.CreateCommand();
        cancel.Transaction = transaction;
        cancel.CommandText = """
            UPDATE todo_reminders
            SET status = $cancelled, updated_at = $now
            WHERE todo_id = $todoId AND status IN ($scheduled, $dispatching);
            """;
        cancel.Parameters.AddWithValue("$cancelled", ReminderStatus.Cancelled.ToString());
        cancel.Parameters.AddWithValue("$now", now.ToString("O"));
        cancel.Parameters.AddWithValue("$todoId", todoId);
        cancel.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        cancel.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        await cancel.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TodoReminder NewReminder(
        long todoId,
        DateTimeOffset scheduledAt,
        long? parentReminderId,
        DateTimeOffset now)
    {
        return new TodoReminder(
            0,
            todoId,
            scheduledAt,
            ReminderStatus.Scheduled,
            parentReminderId,
            null,
            null,
            null,
            0,
            null,
            now,
            now);
    }

    private static async Task<long?> ReadSuggestedProjectIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id FROM message_classifications
            WHERE message_id = $messageId
            ORDER BY created_at DESC, id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task<PriorityLevel> ReadSuggestedPriorityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT priority FROM urgency_scores
            WHERE message_id = $messageId
            ORDER BY id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        var result = await command.ExecuteScalarAsync(cancellationToken) as string;
        return Enum.TryParse<PriorityLevel>(result, out var priority) ? priority : PriorityLevel.P2;
    }
}
