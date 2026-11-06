using Microsoft.Data.Sqlite;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 模型功能的本地开关仓储。
/// 仅复用 app_settings 表，不保存 API Key，也不需要新增数据库结构。
/// </summary>
public sealed class SqliteModelSettingsRepository
{
    // 该键只表示是否分析之后新处理的消息内容；是模型调用的总闸。
    private const string AnalyzeMessageContentKey = "model_analyze_message_content";
    // 该键把分析范围从 @我/邮件扩大到所有消息，仅在总闸开启时生效。
    private const string AnalyzeAllMessagesKey = "model_analyze_all_messages";
    private readonly string _databasePath;

    /// <summary>使用指定 SQLite 数据库创建模型设置仓储。</summary>
    public SqliteModelSettingsRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取消息分析总开关；缺失、空白或非法值均按关闭处理。</summary>
    public Task<bool> GetAnalyzeMessageContentAsync(CancellationToken cancellationToken) =>
        GetSwitchAsync(AnalyzeMessageContentKey, cancellationToken);

    /// <summary>以 UPSERT 保存消息分析总开关，供界面勾选后立即持久化。</summary>
    public Task SetAnalyzeMessageContentAsync(bool enabled, CancellationToken cancellationToken) =>
        SetSwitchAsync(AnalyzeMessageContentKey, enabled, cancellationToken);

    /// <summary>
    /// 读取“分析所有消息”范围开关；与总开关分开存储，
    /// 缺失、空白或非法值一律按关闭处理，避免升级后意外扩大模型调用范围。
    /// </summary>
    public Task<bool> GetAnalyzeAllMessagesAsync(CancellationToken cancellationToken) =>
        GetSwitchAsync(AnalyzeAllMessagesKey, cancellationToken);

    /// <summary>以 UPSERT 保存“分析所有消息”范围开关。</summary>
    public Task SetAnalyzeAllMessagesAsync(bool enabled, CancellationToken cancellationToken) =>
        SetSwitchAsync(AnalyzeAllMessagesKey, enabled, cancellationToken);

    private async Task<bool> GetSwitchAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        // 只承认明确的 "1"，避免旧值或损坏值意外开启模型调用。
        return result is string value && value == "1";
    }

    private async Task SetSwitchAsync(string key, bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", key);
        // 使用固定文本值，与现有应用设置的存储方式保持一致。
        command.Parameters.AddWithValue("$value", enabled ? "1" : "0");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
