using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的待办仓储实现。
/// 支持保存待办与查询待办列表（按优先级排序）。
/// </summary>
public sealed class SqliteTodoRepository : ITodoRepository
{
    private readonly string _databasePath;

    public SqliteTodoRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>插入一条待办，返回带自增 Id 的实体。</summary>
    public async Task<TodoItem> SaveAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await InsertAsync(connection, transaction: null, todo, cancellationToken);
    }

    /// <summary>在指定连接和事务内插入待办，供消息工作单元复用。</summary>
    internal static async Task<TodoItem> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TodoItem todo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO todo_items (
                source_message_id,
                project_id,
                title,
                description,
                status,
                priority,
                due_at,
                created_at,
                updated_at,
                completed_at,
                is_auto_created,
                is_pinned,
                group_id,
                managed_project_id
            )
            VALUES (
                $sourceMessageId,
                $projectId,
                $title,
                $description,
                $status,
                $priority,
                $dueAt,
                $createdAt,
                $updatedAt,
                $completedAt,
                $isAutoCreated,
                $isPinned,
                $groupId,
                $managedProjectId
            )
            RETURNING id;
            """;
        AddTodoParameters(command, todo);
        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("SQLite did not return the inserted Todo id."),
            System.Globalization.CultureInfo.InvariantCulture);

        return todo with { Id = id };
    }

    /// <summary>
    /// 查询所有待办理状态的待办，按原消息发送时间倒序排列。
    /// </summary>
    public async Task<IReadOnlyList<TodoItem>> GetPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        // CASE 表达式将优先级映射为排序权重
        command.CommandText = """
            SELECT
                id,
                source_message_id,
                project_id,
                title,
                description,
                status,
                priority,
                due_at,
                created_at,
                updated_at,
                completed_at,
                is_auto_created,
                is_pinned,
                group_id,
                managed_project_id
            FROM todo_items
            WHERE status = $status
            ORDER BY
                is_pinned DESC,
                COALESCE(
                    (SELECT julianday(sent_at) FROM messages WHERE messages.id = todo_items.source_message_id),
                    julianday(created_at)
                ) DESC,
                CASE priority
                    WHEN 'P0' THEN 0
                    WHEN 'P1' THEN 1
                    WHEN 'P2' THEN 2
                    WHEN 'P3' THEN 3
                    WHEN 'P4' THEN 4
                    WHEN 'P5' THEN 5
                    ELSE 3
                END;
            """;
        command.Parameters.AddWithValue("$status", TodoStatus.Pending.ToString());

        var todos = new List<TodoItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            todos.Add(ReadTodo(reader));
        }

        return todos;
    }

    public async Task<IReadOnlyList<TodoItem>> GetActiveAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_message_id, project_id, title, description, status,
                   priority, due_at, created_at, updated_at, completed_at, is_auto_created, is_pinned, group_id, managed_project_id
            FROM todo_items
            WHERE status IN ($pending, $inProgress, $waiting)
            ORDER BY is_pinned DESC, due_at IS NULL, due_at,
                CASE priority WHEN 'P0' THEN 0 WHEN 'P1' THEN 1 WHEN 'P2' THEN 2
                    WHEN 'P3' THEN 3 WHEN 'P4' THEN 4 WHEN 'P5' THEN 5 ELSE 3 END,
                updated_at DESC;
            """;
        command.Parameters.AddWithValue("$pending", TodoStatus.Pending.ToString());
        command.Parameters.AddWithValue("$inProgress", TodoStatus.InProgress.ToString());
        command.Parameters.AddWithValue("$waiting", TodoStatus.Waiting.ToString());
        var result = new List<TodoItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadTodo(reader));
        }

        return result;
    }

    public async Task<TodoItem?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await GetByIdAsync(connection, transaction: null, id, cancellationToken);
    }

    internal static async Task<TodoItem?> GetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, source_message_id, project_id, title, description, status,
                   priority, due_at, created_at, updated_at, completed_at, is_auto_created, is_pinned, group_id, managed_project_id
            FROM todo_items WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTodo(reader) : null;
    }

    public async Task<TodoItem?> GetBySourceMessageIdAsync(long sourceMessageId, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await GetBySourceMessageIdAsync(connection, transaction: null, sourceMessageId, cancellationToken);
    }

    internal static async Task<TodoItem?> GetBySourceMessageIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long sourceMessageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, source_message_id, project_id, title, description, status,
                   priority, due_at, created_at, updated_at, completed_at, is_auto_created, is_pinned, group_id, managed_project_id
            FROM todo_items
            WHERE source_message_id = $sourceMessageId
            ORDER BY created_at, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sourceMessageId", sourceMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTodo(reader) : null;
    }

    public async Task<IReadOnlyDictionary<long, TodoItem>> GetBySourceMessageIdsAsync(
        IReadOnlyCollection<long> sourceMessageIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceMessageIds.Where(id => id > 0).Distinct().ToArray();
        var todosByMessageId = new Dictionary<long, TodoItem>(ids.Length);
        if (ids.Length == 0)
        {
            return todosByMessageId;
        }

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        foreach (var batch in ids.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var placeholders = AddIdParameters(command, batch);
            // 通过关联表查询，使被合并的第二条消息同样能显示"已转待办"状态。
            command.CommandText = $"""
                SELECT t.id, t.source_message_id, t.project_id, t.title, t.description, t.status,
                       t.priority, t.due_at, t.created_at, t.updated_at, t.completed_at,
                       t.is_auto_created, t.is_pinned, t.group_id, t.managed_project_id, l.message_id
                FROM todo_source_messages l
                JOIN todo_items t ON t.id = l.todo_id
                WHERE l.message_id IN ({placeholders})
                ORDER BY l.message_id, t.created_at, t.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var todo = ReadTodo(reader);
                todosByMessageId.TryAdd(reader.GetInt64(15), todo);
            }
        }

        // 兼容未写入关联表的历史行：按主来源消息补齐缺失状态。
        var missing = ids.Where(id => !todosByMessageId.ContainsKey(id)).ToArray();
        foreach (var batch in missing.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var placeholders = AddIdParameters(command, batch);
            command.CommandText = $"""
                SELECT id, source_message_id, project_id, title, description, status,
                       priority, due_at, created_at, updated_at, completed_at,
                       is_auto_created, is_pinned, group_id, managed_project_id
                FROM todo_items
                WHERE source_message_id IN ({placeholders})
                ORDER BY created_at, id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var todo = ReadTodo(reader);
                if (todo.SourceMessageId is { } sourceMessageId)
                {
                    todosByMessageId.TryAdd(sourceMessageId, todo);
                }
            }
        }

        return todosByMessageId;
    }

    /// <summary>查询任意一条来源消息（主来源或被合并来源）关联的第一个待办。</summary>
    public async Task<TodoItem?> GetByAnySourceMessageIdAsync(long sourceMessageId, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await GetByAnySourceMessageIdAsync(connection, transaction: null, sourceMessageId, cancellationToken);
    }

    internal static async Task<TodoItem?> GetByAnySourceMessageIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long sourceMessageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.id, t.source_message_id, t.project_id, t.title, t.description, t.status,
                   t.priority, t.due_at, t.created_at, t.updated_at, t.completed_at,
                   t.is_auto_created, t.is_pinned, t.group_id, t.managed_project_id
            FROM todo_items t
            WHERE t.source_message_id = $sourceMessageId
               OR EXISTS (
                    SELECT 1 FROM todo_source_messages l
                    WHERE l.todo_id = t.id AND l.message_id = $sourceMessageId)
            ORDER BY t.created_at, t.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sourceMessageId", sourceMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTodo(reader) : null;
    }

    /// <summary>批量查询每条待办关联的全部来源消息，主来源排在首位。</summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<Message>>> GetSourceMessagesForTodosAsync(
        IReadOnlyCollection<long> todoIds,
        CancellationToken cancellationToken)
    {
        var ids = todoIds.Where(id => id > 0).Distinct().ToArray();
        var result = new Dictionary<long, List<Message>>(ids.Length);
        if (ids.Length == 0)
        {
            return new Dictionary<long, IReadOnlyList<Message>>(0);
        }

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        foreach (var batch in ids.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var placeholders = AddIdParameters(command, batch);
            command.CommandText = $"""
                SELECT l.todo_id, m.id, m.source, m.source_message_key, m.chat_session_id, m.chat_name,
                       m.sender_name, m.content, m.message_type, m.sent_at, m.captured_at, m.is_mention_me
                FROM todo_source_messages l
                JOIN messages m ON m.id = l.message_id
                WHERE l.todo_id IN ({placeholders})
                ORDER BY l.todo_id, l.is_primary DESC, m.sent_at, m.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var todoId = reader.GetInt64(0);
                // 跳过 todo_id 列后复用消息表的列映射（0..10）。
                var message = SqliteMessageRepository.ReadMessage(reader, offset: 1);
                if (!result.TryGetValue(todoId, out var list))
                {
                    list = new List<Message>();
                    result[todoId] = list;
                }

                list.Add(message);
            }
        }

        // 兼容未写入关联表的历史行：按主来源消息回退，保证原文仍可展示。
        var missing = ids.Where(id => !result.TryGetValue(id, out var list) || list.Count == 0).ToArray();
        foreach (var batch in missing.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            var placeholders = AddIdParameters(command, batch);
            command.CommandText = $"""
                SELECT t.id, m.id, m.source, m.source_message_key, m.chat_session_id, m.chat_name,
                       m.sender_name, m.content, m.message_type, m.sent_at, m.captured_at, m.is_mention_me
                FROM todo_items t
                JOIN messages m ON m.id = t.source_message_id
                WHERE t.id IN ({placeholders})
                ORDER BY t.id, m.sent_at, m.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var todoId = reader.GetInt64(0);
                var message = SqliteMessageRepository.ReadMessage(reader, offset: 1);
                if (!result.TryGetValue(todoId, out var list))
                {
                    list = new List<Message>();
                    result[todoId] = list;
                }

                list.Add(message);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<Message>)pair.Value);
    }

    /// <summary>写入待办与来源消息的关联；已存在时返回 false。</summary>
    internal static async Task<bool> InsertSourceMessageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long todoId,
        long messageId,
        bool isPrimary,
        DateTimeOffset linkedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO todo_source_messages (todo_id, message_id, linked_at, is_primary)
            VALUES ($todoId, $messageId, $linkedAt, $isPrimary);
            """;
        command.Parameters.AddWithValue("$todoId", todoId);
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$linkedAt", linkedAt.ToString("O"));
        command.Parameters.AddWithValue("$isPrimary", isPrimary ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>查询所有已办理的待办，按完成时间倒序排列。</summary>
    public async Task<IReadOnlyList<TodoItem>> GetCompletedAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                source_message_id,
                project_id,
                title,
                description,
                status,
                priority,
                due_at,
                created_at,
                updated_at,
                completed_at,
                is_auto_created,
                is_pinned,
                group_id,
                managed_project_id
            FROM todo_items
            WHERE status IN ($done, $ignored)
            ORDER BY completed_at DESC, updated_at DESC;
            """;
        command.Parameters.AddWithValue("$done", TodoStatus.Done.ToString());
        command.Parameters.AddWithValue("$ignored", TodoStatus.Ignored.ToString());

        var todos = new List<TodoItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            todos.Add(ReadTodo(reader));
        }

        return todos;
    }

    /// <summary>将一条待办理记录更新为已办理，并写入完成时间。</summary>
    public async Task<bool> MarkCompletedAsync(
        long id,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE todo_items
            SET
                status = $completedStatus,
                updated_at = $completedAt,
                completed_at = $completedAt,
                is_pinned = 0
            WHERE id = $id
              AND status = $pendingStatus;
            """;
        command.Parameters.AddWithValue("$completedStatus", TodoStatus.Done.ToString());
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$pendingStatus", TodoStatus.Pending.ToString());

        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated)
        {
            await CancelOpenRemindersAsync(connection, transaction, id, completedAt, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <summary>更新活动待办的置顶状态；已办和已忽略记录不能重新置顶。</summary>
    public async Task<bool> SetPinnedAsync(
        long id,
        bool isPinned,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_items
            SET is_pinned = $isPinned, updated_at = $updatedAt
            WHERE id = $id
              AND status IN ($pending, $inProgress, $waiting);
            """;
        command.Parameters.AddWithValue("$isPinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$pending", TodoStatus.Pending.ToString());
        command.Parameters.AddWithValue("$inProgress", TodoStatus.InProgress.ToString());
        command.Parameters.AddWithValue("$waiting", TodoStatus.Waiting.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>仅完成调用方明确选择的活动待办，并在同一事务中取消对应提醒。</summary>
    public async Task<int> MarkSelectedCompletedAsync(
        IReadOnlyCollection<long> ids,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var selectedIds = ids.Where(id => id > 0).Distinct().ToArray();
        if (selectedIds.Length == 0)
        {
            return 0;
        }

        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        var updatedIds = new List<long>(selectedIds.Length);
        foreach (var batch in selectedIds.Chunk(500))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var placeholders = AddIdParameters(command, batch);
            command.CommandText = $"""
                UPDATE todo_items
                SET status = $completedStatus,
                    updated_at = $completedAt,
                    completed_at = $completedAt,
                    is_pinned = 0
                WHERE id IN ({placeholders})
                  AND status IN ($pending, $inProgress, $waiting)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$completedStatus", TodoStatus.Done.ToString());
            command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
            command.Parameters.AddWithValue("$pending", TodoStatus.Pending.ToString());
            command.Parameters.AddWithValue("$inProgress", TodoStatus.InProgress.ToString());
            command.Parameters.AddWithValue("$waiting", TodoStatus.Waiting.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                updatedIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var batch in updatedIds.Chunk(500))
        {
            await using var cancel = connection.CreateCommand();
            cancel.Transaction = transaction;
            var placeholders = AddIdParameters(cancel, batch);
            cancel.CommandText = $"""
                UPDATE todo_reminders
                SET status = $cancelled, updated_at = $completedAt
                WHERE todo_id IN ({placeholders})
                  AND status IN ($scheduled, $dispatching);
                """;
            cancel.Parameters.AddWithValue("$cancelled", ReminderStatus.Cancelled.ToString());
            cancel.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
            cancel.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
            cancel.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updatedIds.Count;
    }

    /// <summary>将全部活动状态记录原子更新为已办理，并返回更新数量。</summary>
    public async Task<int> MarkAllCompletedAsync(
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE todo_items
            SET
                status = $completedStatus,
                updated_at = $completedAt,
                completed_at = $completedAt,
                is_pinned = 0
            WHERE status IN ($pendingStatus, $inProgressStatus, $waitingStatus);
            """;
        command.Parameters.AddWithValue("$completedStatus", TodoStatus.Done.ToString());
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$pendingStatus", TodoStatus.Pending.ToString());
        command.Parameters.AddWithValue("$inProgressStatus", TodoStatus.InProgress.ToString());
        command.Parameters.AddWithValue("$waitingStatus", TodoStatus.Waiting.ToString());

        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated > 0)
        {
            await using var cancel = connection.CreateCommand();
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE todo_reminders
                SET status = $cancelled, updated_at = $completedAt
                WHERE status IN ($scheduled, $dispatching)
                  AND todo_id IN (SELECT id FROM todo_items WHERE status = $completedStatus AND completed_at = $completedAt);
                """;
            cancel.Parameters.AddWithValue("$cancelled", ReminderStatus.Cancelled.ToString());
            cancel.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
            cancel.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
            cancel.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
            cancel.Parameters.AddWithValue("$completedStatus", TodoStatus.Done.ToString());
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    /// <summary>删除全部已办理记录，并返回删除数量。</summary>
    public async Task<int> DeleteCompletedAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM todo_items WHERE status IN ($done, $ignored);";
        command.Parameters.AddWithValue("$done", TodoStatus.Done.ToString());
        command.Parameters.AddWithValue("$ignored", TodoStatus.Ignored.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CancelOpenRemindersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long todoId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var cancel = connection.CreateCommand();
        cancel.Transaction = transaction;
        cancel.CommandText = """
            UPDATE todo_reminders
            SET status = $cancelled, updated_at = $completedAt
            WHERE todo_id = $todoId AND status IN ($scheduled, $dispatching);
            """;
        cancel.Parameters.AddWithValue("$cancelled", ReminderStatus.Cancelled.ToString());
        cancel.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
        cancel.Parameters.AddWithValue("$todoId", todoId);
        cancel.Parameters.AddWithValue("$scheduled", ReminderStatus.Scheduled.ToString());
        cancel.Parameters.AddWithValue("$dispatching", ReminderStatus.Dispatching.ToString());
        await cancel.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>绑定待办参数到 INSERT 命令。</summary>
    private static void AddTodoParameters(SqliteCommand command, TodoItem todo)
    {
        command.Parameters.AddWithValue("$sourceMessageId", ToDbValue(todo.SourceMessageId));
        command.Parameters.AddWithValue("$projectId", ToDbValue(todo.ProjectId));
        command.Parameters.AddWithValue("$title", todo.Title);
        command.Parameters.AddWithValue("$description", ToDbValue(todo.Description));
        // 枚举以字符串形式存储
        command.Parameters.AddWithValue("$status", todo.Status.ToString());
        command.Parameters.AddWithValue("$priority", todo.Priority.ToString());
        // 可空时间以 ISO-8601 存储
        command.Parameters.AddWithValue("$dueAt", ToDbValue(todo.DueAt?.ToString("O")));
        command.Parameters.AddWithValue("$createdAt", todo.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", todo.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", ToDbValue(todo.CompletedAt?.ToString("O")));
        command.Parameters.AddWithValue("$isAutoCreated", todo.IsAutoCreated ? 1 : 0);
        command.Parameters.AddWithValue("$isPinned", todo.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$groupId", ToDbValue(todo.GroupId));
        command.Parameters.AddWithValue("$managedProjectId", ToDbValue(todo.ManagedProjectId));
    }

    /// <summary>从 DataReader 映射为 TodoItem 实体。</summary>
    internal static TodoItem ReadTodo(SqliteDataReader reader)
    {
        return new TodoItem(
            Id: reader.GetInt64(0),
            SourceMessageId: GetNullableInt64(reader, 1),
            ProjectId: GetNullableInt64(reader, 2),
            Title: reader.GetString(3),
            Description: reader.IsDBNull(4) ? null : reader.GetString(4),
            Status: Enum.Parse<TodoStatus>(reader.GetString(5)),
            Priority: Enum.Parse<PriorityLevel>(reader.GetString(6)),
            DueAt: GetNullableDateTimeOffset(reader, 7),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(8)),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(9)),
            CompletedAt: GetNullableDateTimeOffset(reader, 10),
            IsAutoCreated: reader.GetInt32(11) == 1)
        {
            IsPinned = reader.GetInt32(12) == 1,
            GroupId = GetNullableInt64(reader, 13),
            ManagedProjectId = GetNullableInt64(reader, 14)
        };
    }

    private static string AddIdParameters(SqliteCommand command, IReadOnlyList<long> ids)
    {
        var placeholders = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
        {
            var parameterName = $"$id{index}";
            placeholders[index] = parameterName;
            command.Parameters.AddWithValue(parameterName, ids[index]);
        }

        return string.Join(", ", placeholders);
    }

    /// <summary>将可空值转换为数据库参数（null -> DBNull）。</summary>
    private static object ToDbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }

    /// <summary>读取可空的 long 值。</summary>
    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    /// <summary>读取可空的 DateTimeOffset 值。</summary>
    private static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }
}
