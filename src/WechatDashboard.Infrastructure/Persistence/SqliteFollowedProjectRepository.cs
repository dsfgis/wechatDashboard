using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的关注项目仓储实现。
/// 项目名按文本唯一约束存储，重复保存会重新激活（软删除后恢复）。
/// </summary>
public sealed class SqliteFollowedProjectRepository : IFollowedProjectRepository
{
    private readonly string _databasePath;

    public SqliteFollowedProjectRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取所有启用状态的关注项目，按 ID 排序。</summary>
    public async Task<IReadOnlyList<FollowedProject>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_name, is_active, created_at
            FROM followed_projects
            WHERE is_active = 1
            ORDER BY id;
            """;

        var projects = new List<FollowedProject>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    /// <summary>
    /// 保存关注项目：存在则重新激活，不存在则新增。返回最新状态的实体。
    /// </summary>
    public async Task<FollowedProject> SaveAsync(string projectName, CancellationToken cancellationToken)
    {
        var normalized = projectName.Trim();
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        // 利用 ON CONFLICT 处理重复项目名：重新激活
        command.CommandText = """
            INSERT INTO followed_projects (project_name, is_active, created_at)
            VALUES ($projectName, 1, $createdAt)
            ON CONFLICT(project_name)
            DO UPDATE SET is_active = 1;
            """;
        command.Parameters.AddWithValue("$projectName", normalized);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 回读最新状态
        command.Parameters.Clear();
        command.CommandText = "SELECT id, project_name, is_active, created_at FROM followed_projects WHERE project_name = $projectName;";
        command.Parameters.AddWithValue("$projectName", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadProject(reader);
        }

        return new FollowedProject(0, normalized, true, now);
    }

    /// <summary>按 ID 物理删除关注项目。</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM followed_projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>从 DataReader 映射为 FollowedProject 实体。</summary>
    private static FollowedProject ReadProject(SqliteDataReader reader)
    {
        return new FollowedProject(
            Id: reader.GetInt64(0),
            ProjectName: reader.GetString(1),
            IsActive: reader.GetInt32(2) == 1,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(3)));
    }
}
