using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteUserAliasRepository : IUserAliasRepository
{
    private readonly string _databasePath;

    public SqliteUserAliasRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<UserAlias>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, alias, is_active, created_at
            FROM user_aliases
            WHERE is_active = 1
            ORDER BY id;
            """;

        var aliases = new List<UserAlias>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            aliases.Add(ReadAlias(reader));
        }

        return aliases;
    }

    public async Task<UserAlias> SaveAsync(string alias, CancellationToken cancellationToken)
    {
        var normalized = alias.Trim();
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO user_aliases (alias, is_active, created_at)
            VALUES ($alias, 1, $createdAt)
            ON CONFLICT(alias)
            DO UPDATE SET is_active = 1;
            """;
        command.Parameters.AddWithValue("$alias", normalized);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = "SELECT id, alias, is_active, created_at FROM user_aliases WHERE alias = $alias;";
        command.Parameters.AddWithValue("$alias", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadAlias(reader);
        }

        return new UserAlias(0, normalized, true, now);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_aliases WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static UserAlias ReadAlias(SqliteDataReader reader)
    {
        return new UserAlias(
            Id: reader.GetInt64(0),
            Alias: reader.GetString(1),
            IsActive: reader.GetInt32(2) == 1,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(3)));
    }
}
