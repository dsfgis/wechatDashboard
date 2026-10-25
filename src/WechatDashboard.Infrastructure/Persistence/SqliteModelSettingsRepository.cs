using Microsoft.Data.Sqlite;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 模型功能的本地开关仓储。
/// 仅复用 app_settings 表，不保存 API Key，也不需要新增数据库结构。
/// </summary>
public sealed class SqliteModelSettingsRepository
{
    // 该键只表示是否分析之后新处理的消息内容。
    private const string AnalyzeMessageContentKey = "model_analyze_message_content";
    private readonly string _databasePath;

    /// <summary>使用指定 SQLite 数据库创建模型设置仓储。</summary>
    public SqliteModelSettingsRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取消息分析开关；缺失、空白或非法值均按关闭处理。</summary>
    public async Task<bool> GetAnalyzeMessageContentAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", AnalyzeMessageContentKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        // 只承认明确的 "1"，避免旧值或损坏值意外开启模型调用。
        return result is string value && value == "1";
    }

    /// <summary>以 UPSERT 保存消息分析开关，供界面勾选后立即持久化。</summary>
    public async Task SetAnalyzeMessageContentAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", AnalyzeMessageContentKey);
        // 使用固定文本值，与现有应用设置的存储方式保持一致。
        command.Parameters.AddWithValue("$value", enabled ? "1" : "0");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
