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

    /// <summary>
    /// 读取所有启用状态的关注项目。
    /// 排序按项目状态：在建 → 运维 → 移交，同状态内按创建时间与 ID 保持稳定顺序。
    /// </summary>
    public async Task<IReadOnlyList<FollowedProject>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_name, is_active, created_at, color, stage
            FROM followed_projects
            WHERE is_active = 1
            ORDER BY CASE stage
                        WHEN 'Construction' THEN 0
                        WHEN 'Operation' THEN 1
                        WHEN 'Handover' THEN 2
                        ELSE 3
                     END,
                     created_at,
                     id;
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
    public Task<FollowedProject> SaveAsync(string projectName, CancellationToken cancellationToken) =>
        SaveAsync(projectName, ProjectColorPolicy.DefaultColor, cancellationToken);

    public async Task<FollowedProject> SaveAsync(string projectName, string color, CancellationToken cancellationToken)
    {
        var normalized = projectName.Trim();
        var normalizedColor = ProjectColorPolicy.Normalize(color);
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        // 利用 ON CONFLICT 处理重复项目名：重新激活
        command.CommandText = """
            INSERT INTO followed_projects (project_name, is_active, created_at, color)
            VALUES ($projectName, 1, $createdAt, $color)
            ON CONFLICT(project_name)
            DO UPDATE SET is_active = 1;
            """;
        command.Parameters.AddWithValue("$projectName", normalized);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$color", normalizedColor);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 回读最新状态
        command.Parameters.Clear();
        command.CommandText = "SELECT id, project_name, is_active, created_at, color, stage FROM followed_projects WHERE project_name = $projectName;";
        command.Parameters.AddWithValue("$projectName", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadProject(reader);
        }

        return new FollowedProject(0, normalized, true, now) { Color = normalizedColor };
    }

    public async Task<UpdateFollowedProjectColorOutcome> UpdateColorAsync(
        long id,
        string color,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return UpdateFollowedProjectColorOutcome.Missing;
        }

        var normalizedColor = ProjectColorPolicy.Normalize(color);
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT color FROM followed_projects WHERE id = $id AND is_active = 1;";
        read.Parameters.AddWithValue("$id", id);
        var currentColor = await read.ExecuteScalarAsync(cancellationToken) as string;
        if (currentColor is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectColorOutcome.Missing;
        }

        if (string.Equals(currentColor, normalizedColor, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectColorOutcome.Unchanged;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE followed_projects SET color = $color WHERE id = $id AND is_active = 1;";
        update.Parameters.AddWithValue("$color", normalizedColor);
        update.Parameters.AddWithValue("$id", id);
        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectColorOutcome.Missing;
        }

        await transaction.CommitAsync(cancellationToken);
        return UpdateFollowedProjectColorOutcome.Updated;
    }

    /// <summary>
    /// 按项目 ID 更新生命周期状态（在建/运维/移交）。
    /// 状态变化会影响 GetAllAsync 的排序，但不会触碰项目名、颜色或关键字。
    /// </summary>
    public async Task<UpdateFollowedProjectStageOutcome> UpdateStageAsync(
        long id,
        ProjectStage stage,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return UpdateFollowedProjectStageOutcome.Missing;
        }

        var normalizedStage = ProjectStagePolicy.ToStorageValue(stage);
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT stage FROM followed_projects WHERE id = $id AND is_active = 1;";
        read.Parameters.AddWithValue("$id", id);
        var currentStage = await read.ExecuteScalarAsync(cancellationToken) as string;
        if (currentStage is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectStageOutcome.Missing;
        }

        if (string.Equals(currentStage, normalizedStage, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectStageOutcome.Unchanged;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE followed_projects SET stage = $stage WHERE id = $id AND is_active = 1;";
        update.Parameters.AddWithValue("$stage", normalizedStage);
        update.Parameters.AddWithValue("$id", id);
        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectStageOutcome.Missing;
        }

        await transaction.CommitAsync(cancellationToken);
        return UpdateFollowedProjectStageOutcome.Updated;
    }

    /// <summary>
    /// 在同一事务内更新项目状态与其固定颜色。
    /// 状态与颜色在界面上已合并为一个下拉框，分开写会出现状态已改、颜色未改的中间态，
    /// 因此这里一次性写入；两者都与库里一致时返回 Unchanged，不做无意义写入。
    /// </summary>
    public async Task<UpdateFollowedProjectStageOutcome> UpdateStageAndColorAsync(
        long id,
        ProjectStage stage,
        string color,
        CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return UpdateFollowedProjectStageOutcome.Missing;
        }

        var normalizedStage = ProjectStagePolicy.ToStorageValue(stage);
        var normalizedColor = ProjectColorPolicy.Normalize(color);
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var transaction = connection.BeginTransaction();
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT stage, color FROM followed_projects WHERE id = $id AND is_active = 1;";
        read.Parameters.AddWithValue("$id", id);
        string? currentStage;
        string? currentColor;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return UpdateFollowedProjectStageOutcome.Missing;
            }

            currentStage = reader.IsDBNull(0) ? null : reader.GetString(0);
            currentColor = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (string.Equals(currentStage, normalizedStage, StringComparison.Ordinal) &&
            string.Equals(currentColor, normalizedColor, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectStageOutcome.Unchanged;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE followed_projects
            SET stage = $stage, color = $color
            WHERE id = $id AND is_active = 1;
            """;
        update.Parameters.AddWithValue("$stage", normalizedStage);
        update.Parameters.AddWithValue("$color", normalizedColor);
        update.Parameters.AddWithValue("$id", id);
        var affected = await update.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return UpdateFollowedProjectStageOutcome.Missing;
        }

        await transaction.CommitAsync(cancellationToken);
        return UpdateFollowedProjectStageOutcome.Updated;
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
            CreatedAt: DateTimeOffset.Parse(reader.GetString(3)))
        {
            Color = reader.GetString(4),
            Stage = ProjectStagePolicy.Parse(reader.IsDBNull(5) ? null : reader.GetString(5))
        };
    }
}
