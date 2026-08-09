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
                is_auto_created
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
                $isAutoCreated
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
                is_auto_created
            FROM todo_items
            WHERE status = $status
            ORDER BY
                COALESCE(
                    (SELECT julianday(sent_at) FROM messages WHERE messages.id = todo_items.source_message_id),
                    julianday(created_at)
                ) DESC,
                CASE priority
                    WHEN 'P0' THEN 0
                    WHEN 'P1' THEN 1
                    WHEN 'P2' THEN 2
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
                is_auto_created
            FROM todo_items
            WHERE status = $status
            ORDER BY completed_at DESC, updated_at DESC;
            """;
        command.Parameters.AddWithValue("$status", TodoStatus.Done.ToString());

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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_items
            SET
                status = $completedStatus,
                updated_at = $completedAt,
                completed_at = $completedAt
            WHERE id = $id
              AND status = $pendingStatus;
            """;
        command.Parameters.AddWithValue("$completedStatus", TodoStatus.Done.ToString());
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$pendingStatus", TodoStatus.Pending.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>将全部待办理记录原子更新为已办理，并返回更新数量。</summary>
    public async Task<int> MarkAllCompletedAsync(
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE todo_items
            SET
                status = $completedStatus,
                updated_at = $completedAt,
                completed_at = $completedAt
            WHERE status = $pendingStatus;
            """;
        command.Parameters.AddWithValue("$completedStatus", TodoStatus.Done.ToString());
        command.Parameters.AddWithValue("$completedAt", completedAt.ToString("O"));
        command.Parameters.AddWithValue("$pendingStatus", TodoStatus.Pending.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>删除全部已办理记录，并返回删除数量。</summary>
    public async Task<int> DeleteCompletedAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM todo_items WHERE status = $status;";
        command.Parameters.AddWithValue("$status", TodoStatus.Done.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken);
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
    }

    /// <summary>从 DataReader 映射为 TodoItem 实体。</summary>
    private static TodoItem ReadTodo(SqliteDataReader reader)
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
            IsAutoCreated: reader.GetInt32(11) == 1);
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
