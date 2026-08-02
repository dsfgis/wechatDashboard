using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>SQLite 项目关键字仓储，关键字在同一项目内唯一。</summary>
public sealed class SqliteFollowedProjectKeywordRepository : IFollowedProjectKeywordRepository
{
    private readonly string _databasePath;

    public SqliteFollowedProjectKeywordRepository(string databasePath) => _databasePath = databasePath;

    public async Task<IReadOnlyList<FollowedProjectKeyword>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.id, k.project_id, p.project_name, k.keyword, k.created_at
            FROM followed_project_keywords k
            INNER JOIN followed_projects p ON p.id = k.project_id
            WHERE p.is_active = 1
            ORDER BY p.id, k.id;
            """;
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<FollowedProjectKeyword> SaveAsync(long projectId, string keyword, CancellationToken cancellationToken)
    {
        var normalized = keyword.Trim();
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO followed_project_keywords (project_id, keyword, created_at)
            VALUES ($projectId, $keyword, $createdAt)
            ON CONFLICT(project_id, keyword) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$keyword", normalized);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = """
            SELECT k.id, k.project_id, p.project_name, k.keyword, k.created_at
            FROM followed_project_keywords k
            INNER JOIN followed_projects p ON p.id = k.project_id
            WHERE k.project_id = $projectId AND k.keyword = $keyword;
            """;
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$keyword", normalized);
        var results = await ReadAllAsync(command, cancellationToken);
        return results.SingleOrDefault() ?? new FollowedProjectKeyword(0, projectId, "", normalized, now);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM followed_project_keywords WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByProjectIdAsync(long projectId, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM followed_project_keywords WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<FollowedProjectKeyword>> ReadAllAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<FollowedProjectKeyword>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new FollowedProjectKeyword(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        }
        return results;
    }
}
