using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Todos;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteTodoGroupRepository : ITodoGroupRepository
{
    private readonly string _databasePath;

    public SqliteTodoGroupRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<TodoGroup>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, normalized_name, color, created_at FROM todo_groups ORDER BY name COLLATE NOCASE, id;";
        var groups = new List<TodoGroup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(ReadGroup(reader));
        }

        return groups;
    }

    public async Task<TodoGroup?> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        return await GetByIdAsync(connection, transaction: null, id, cancellationToken);
    }

    internal static async Task<TodoGroup?> GetByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name, normalized_name, color, created_at FROM todo_groups WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGroup(reader) : null;
    }

    public async Task<CreateTodoGroupResult> CreateAsync(
        string name,
        string normalizedName,
        string color,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        try
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO todo_groups(name, normalized_name, color, created_at)
                VALUES ($name, $normalizedName, $color, $createdAt)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$normalizedName", normalizedName);
            insert.Parameters.AddWithValue("$color", color);
            insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            var id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            return new CreateTodoGroupResult(
                CreateTodoGroupOutcome.Created,
                new TodoGroup(id, name, normalizedName, color, now));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            var existing = await GetByNormalizedNameAsync(connection, normalizedName, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return new CreateTodoGroupResult(CreateTodoGroupOutcome.Existing, existing);
        }
    }

    public async Task<DeleteTodoGroupOutcome> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM todo_groups
            WHERE id = $id
              AND NOT EXISTS (
                  SELECT 1
                  FROM todo_items
                  WHERE group_id = $id
              );
            """;
        command.Parameters.AddWithValue("$id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            return DeleteTodoGroupOutcome.Deleted;
        }

        return await GetByIdAsync(connection, transaction: null, id, cancellationToken) is null
            ? DeleteTodoGroupOutcome.Missing
            : DeleteTodoGroupOutcome.InUse;
    }

    private static async Task<TodoGroup?> GetByNormalizedNameAsync(
        SqliteConnection connection,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, normalized_name, color, created_at FROM todo_groups WHERE normalized_name = $normalizedName;";
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGroup(reader) : null;
    }

    private static TodoGroup ReadGroup(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4)));
}
