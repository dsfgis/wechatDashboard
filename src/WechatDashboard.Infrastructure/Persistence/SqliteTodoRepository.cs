using Microsoft.Data.Sqlite;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteTodoRepository
{
    private readonly string _databasePath;

    public SqliteTodoRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<TodoItem> SaveAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
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
            );
            """;
        AddTodoParameters(command, todo);
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = "SELECT last_insert_rowid();";
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return todo with { Id = id };
    }

    public async Task<IReadOnlyList<TodoItem>> GetPendingAsync(CancellationToken cancellationToken)
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
            ORDER BY
                CASE priority
                    WHEN 'P0' THEN 0
                    WHEN 'P1' THEN 1
                    WHEN 'P2' THEN 2
                    ELSE 3
                END,
                created_at DESC;
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

    private static void AddTodoParameters(SqliteCommand command, TodoItem todo)
    {
        command.Parameters.AddWithValue("$sourceMessageId", ToDbValue(todo.SourceMessageId));
        command.Parameters.AddWithValue("$projectId", ToDbValue(todo.ProjectId));
        command.Parameters.AddWithValue("$title", todo.Title);
        command.Parameters.AddWithValue("$description", ToDbValue(todo.Description));
        command.Parameters.AddWithValue("$status", todo.Status.ToString());
        command.Parameters.AddWithValue("$priority", todo.Priority.ToString());
        command.Parameters.AddWithValue("$dueAt", ToDbValue(todo.DueAt?.ToString("O")));
        command.Parameters.AddWithValue("$createdAt", todo.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", todo.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", ToDbValue(todo.CompletedAt?.ToString("O")));
        command.Parameters.AddWithValue("$isAutoCreated", todo.IsAutoCreated ? 1 : 0);
    }

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

    private static object ToDbValue<T>(T? value)
    {
        return value is null ? DBNull.Value : value;
    }

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }
}
