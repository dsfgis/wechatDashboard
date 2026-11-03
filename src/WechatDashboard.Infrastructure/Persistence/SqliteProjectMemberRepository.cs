using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>SQLite 项目成员仓储；成员姓名在同一项目内唯一。</summary>
public sealed class SqliteProjectMemberRepository : IProjectMemberRepository
{
    private readonly string _databasePath;

    public SqliteProjectMemberRepository(string databasePath) => _databasePath = databasePath;

    public async Task<IReadOnlyList<ProjectMember>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project_id, display_name, alias, email, is_reporting, "
            + "created_at, wechat_user_id FROM project_members ORDER BY project_id, id;";
        return await ReadAllAsync(command, cancellationToken);
    }

    public Task<ProjectMember> SaveAsync(
        long projectId,
        string displayName,
        string? alias,
        string? email,
        CancellationToken cancellationToken)
    {
        // 保留既有调用签名，避免尚未接入微信 ID 的调用方产生编译改动。
        return SaveAsync(projectId, displayName, alias, email, null, cancellationToken);
    }

    public async Task<ProjectMember> SaveAsync(
        long projectId,
        string displayName,
        string? alias,
        string? email,
        string? weChatUserId,
        CancellationToken cancellationToken)
    {
        var name = displayName.Trim();
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO project_members (project_id, display_name, alias, email, "
            + "wechat_user_id, is_reporting, created_at) VALUES ($projectId, $displayName, $alias, $email, "
            + "$weChatUserId, 1, $createdAt) "
            + "ON CONFLICT(project_id, display_name) DO UPDATE SET alias = excluded.alias, "
            + "email = excluded.email, wechat_user_id = excluded.wechat_user_id;";
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$displayName", name);
        command.Parameters.AddWithValue("$alias", (object?)alias?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$email", (object?)email?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$weChatUserId", (object?)weChatUserId?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        // 冲突更新明确不写 is_reporting，既有周报开关必须原样保留。
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.Parameters.Clear();
        command.CommandText = "SELECT id, project_id, display_name, alias, email, is_reporting, "
            + "created_at, wechat_user_id FROM project_members WHERE project_id = $projectId AND display_name = $displayName;";
        command.Parameters.AddWithValue("$projectId", projectId);
        command.Parameters.AddWithValue("$displayName", name);
        var results = await ReadAllAsync(command, cancellationToken);
        return results.SingleOrDefault()
            ?? new ProjectMember(0, projectId, name, alias, email, true, now, weChatUserId);
    }

    /// <summary>仅更新周报开关，避免覆盖并发维护的邮箱、微信 ID 或别名。</summary>
    public async Task SetReportingAsync(long id, bool isReporting, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE project_members SET is_reporting = $isReporting WHERE id = $id;";
        command.Parameters.AddWithValue("$isReporting", isReporting ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM project_members WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteByProjectIdAsync(long projectId, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM project_members WHERE project_id = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ProjectMember>> ReadAllAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<ProjectMember>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ProjectMember(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1,
                DateTimeOffset.Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return results;
    }
}
