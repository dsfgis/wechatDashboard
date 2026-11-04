using System.IO;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;
using WechatDashboard.Infrastructure.Persistence;

namespace WechatDashboard.Tests;

/// <summary>
/// 关注项目生命周期状态的集成测试：v10 迁移默认值、状态持久化幂等性，
/// 以及“在建 → 运维 → 转交”的排序契约（周报与简报项目下拉直接依赖该顺序）。
/// </summary>
public static class FollowedProjectStageTests
{
    /// <summary>v10 只追加 stage 列，历史项目一律回落为“在建”，且迁移可重复执行。</summary>
    public static async Task SchemaV10DefaultsToConstructionAsync()
    {
        var databasePath = CreateDatabasePath();
        var initializer = new SqliteDatabaseInitializer(databasePath);
        await initializer.InitializeAsync(CancellationToken.None);

        // 模拟 v9 旧库：删掉 stage 列与 v10 记录，再写入一条历史项目。
        await ExecuteAsync(databasePath, """
            DELETE FROM schema_migration_reports WHERE version = 10;
            DELETE FROM schema_migrations WHERE version = 10;
            ALTER TABLE followed_projects DROP COLUMN stage;
            INSERT INTO followed_projects (project_name, is_active, created_at, color)
            VALUES ('历史项目', 1, '2026-09-01T09:00:00.0000000+08:00', '#4B5563');
            """);

        await initializer.InitializeAsync(CancellationToken.None);
        // 重复初始化必须保持幂等：v10 只能有一条迁移记录。
        await initializer.InitializeAsync(CancellationToken.None);

        AssertEqual(1L, await ScalarAsync<long>(
            databasePath,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = 10 AND checksum = 'followed-project-stage-v1';"),
            "Migration v10 should be recorded exactly once.");

        var project = (await new SqliteFollowedProjectRepository(databasePath)
            .GetAllAsync(CancellationToken.None)).Single();
        AssertEqual(ProjectStage.Construction, project.Stage, "Historic projects must fall back to Construction.");
        AssertEqual("在建 · 历史项目", project.ProjectDisplayName, "Dropdown text must carry the stage prefix.");
    }

    /// <summary>状态更新需要落库、幂等，并在读取时按阶段重新排序。</summary>
    public static async Task StageUpdatePersistsAndReordersAsync()
    {
        var databasePath = CreateDatabasePath();
        await new SqliteDatabaseInitializer(databasePath).InitializeAsync(CancellationToken.None);
        var repository = new SqliteFollowedProjectRepository(databasePath);
        var first = await repository.SaveAsync("甲项目", ProjectColorPolicy.Red, CancellationToken.None);
        var second = await repository.SaveAsync("乙项目", ProjectColorPolicy.Blue, CancellationToken.None);
        var third = await repository.SaveAsync("丙项目", ProjectColorPolicy.Green, CancellationToken.None);

        AssertEqual(
            "甲项目,乙项目,丙项目",
            Join(await repository.GetAllAsync(CancellationToken.None)),
            "Fresh projects all start as Construction and keep creation order.");

        // 甲转入运维后必须排到仍为在建的乙、丙之后，但早于转交项目。
        AssertEqual(
            UpdateFollowedProjectStageOutcome.Updated,
            await repository.UpdateStageAsync(first.Id, ProjectStage.Operation, CancellationToken.None),
            "First stage change should report Updated.");
        AssertEqual(
            UpdateFollowedProjectStageOutcome.Unchanged,
            await repository.UpdateStageAsync(first.Id, ProjectStage.Operation, CancellationToken.None),
            "Repeating the same stage must be a no-op.");
        AssertEqual(
            UpdateFollowedProjectStageOutcome.Missing,
            await repository.UpdateStageAsync(999_999, ProjectStage.Handover, CancellationToken.None),
            "Unknown project ids must report Missing instead of throwing.");

        AssertEqual(
            UpdateFollowedProjectStageOutcome.Updated,
            await repository.UpdateStageAsync(third.Id, ProjectStage.Handover, CancellationToken.None),
            "Handover should be accepted.");

        var ordered = await repository.GetAllAsync(CancellationToken.None);
        AssertEqual("乙项目,甲项目,丙项目", Join(ordered), "Ordering must be Construction, Operation, then Handover.");
        AssertEqual("运维 · 甲项目", ordered.Single(project => project.Id == first.Id).ProjectDisplayName,
            "Repository reads must expose the saved stage in the display text.");
        AssertEqual(ProjectStage.Handover, ordered.Single(project => project.Id == third.Id).Stage,
            "Handover must survive a fresh read.");

        // 颜色更新不能顺手把状态改回在建。
        AssertEqual(
            UpdateFollowedProjectColorOutcome.Updated,
            await repository.UpdateColorAsync(second.Id, ProjectColorPolicy.Purple, CancellationToken.None),
            "Color update should still work after the stage column exists.");
        AssertEqual(
            ProjectStage.Construction,
            (await repository.GetAllAsync(CancellationToken.None)).Single(project => project.Id == second.Id).Stage,
            "Color changes must not touch the stage column.");

        // 停用（删除）项目后，状态行也随之从下拉数据源消失。
        await repository.DeleteAsync(first.Id, CancellationToken.None);
        AssertEqual("乙项目,丙项目", Join(await repository.GetAllAsync(CancellationToken.None)),
            "Deleted projects must not linger in the picker source.");
    }

    private static string Join(IReadOnlyList<FollowedProject> projects) =>
        string.Join(",", projects.Select(project => project.ProjectName));

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WechatDashboard.Tests", "followed-project-stage");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<T> ScalarAsync<T>(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}, actual: {actual}.");
        }
    }
}
