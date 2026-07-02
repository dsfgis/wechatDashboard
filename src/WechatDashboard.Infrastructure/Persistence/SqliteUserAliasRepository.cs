using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Mentions;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的用户别名仓储实现。
/// 别名按文本唯一约束存储，重复保存会重新激活（软删除后恢复）。
/// </summary>
public sealed class SqliteUserAliasRepository : IUserAliasRepository
{
    private readonly string _databasePath;

    public SqliteUserAliasRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取所有启用状态的别名，按 ID 排序。</summary>
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

    /// <summary>
    /// 保存别名：存在则重新激活，不存在则新增。返回最新状态的别名实体。
    /// </summary>
    public async Task<UserAlias> SaveAsync(string alias, CancellationToken cancellationToken)
    {
        var normalized = alias.Trim();
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        // 利用 ON CONFLICT 处理重复别名：重新激活
        command.CommandText = """
            INSERT INTO user_aliases (alias, is_active, created_at)
            VALUES ($alias, 1, $createdAt)
            ON CONFLICT(alias)
            DO UPDATE SET is_active = 1;
            """;
        command.Parameters.AddWithValue("$alias", normalized);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 回读最新状态
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

    /// <summary>按 ID 物理删除别名。</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM user_aliases WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>从 DataReader 映射为 UserAlias 实体。</summary>
    private static UserAlias ReadAlias(SqliteDataReader reader)
    {
        return new UserAlias(
            Id: reader.GetInt64(0),
            Alias: reader.GetString(1),
            IsActive: reader.GetInt32(2) == 1,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(3)));
    }
}
