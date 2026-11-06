using Microsoft.Data.Sqlite;
using WechatDashboard.Application.TodoRules;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的自动待办规则仓储。
/// 规则共用既有数据库文件，只新增 todo_rules 表，不改动消息与待办结构。
/// </summary>
public sealed class SqliteTodoRuleRepository : ITodoRuleRepository
{
    private readonly string _databasePath;

    public SqliteTodoRuleRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>按创建时间正序读取全部规则，包含已停用项，供界面统一管理。</summary>
    public async Task<IReadOnlyList<TodoRule>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, rule_text, is_enabled, created_at
            FROM todo_rules
            ORDER BY id;
            """;

        var rules = new List<TodoRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(new TodoRule(
                Id: reader.GetInt64(0),
                Text: reader.GetString(1),
                IsEnabled: reader.GetInt32(2) == 1,
                CreatedAt: DateTimeOffset.Parse(reader.GetString(3))));
        }

        return rules;
    }

    /// <summary>新增规则：文本先归一化，重复文本不会产生第二条记录。</summary>
    public async Task<long> AddAsync(string text, CancellationToken cancellationToken)
    {
        var normalized = TodoRuleMatcher.Normalize(text);
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        // 同一规则重复添加时保持原记录，避免列表出现无法区分的重复行。
        command.CommandText = """
            INSERT INTO todo_rules (rule_text, is_enabled, created_at)
            VALUES ($text, 1, $createdAt)
            ON CONFLICT(rule_text) DO UPDATE SET is_enabled = 1;
            """;
        command.Parameters.AddWithValue("$text", normalized);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 回读主键：重复添加时返回既有记录，界面不会出现两条相同规则。
        command.Parameters.Clear();
        command.CommandText = "SELECT id FROM todo_rules WHERE rule_text = $text;";
        command.Parameters.AddWithValue("$text", normalized);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : 0;
    }

    /// <summary>切换规则的启用状态；记录不存在时静默忽略。</summary>
    public async Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE todo_rules SET is_enabled = $enabled WHERE id = $id;";
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>按 ID 物理删除规则；记录不存在时静默忽略。</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM todo_rules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
