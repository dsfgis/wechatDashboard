using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的处理偏移量仓储实现。
/// 记录每个适配器上次采集到的位置，支持增量采集。
/// </summary>
public sealed class SqliteProcessingOffsetRepository : IProcessingOffsetRepository
{
    private readonly string _databasePath;

    public SqliteProcessingOffsetRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取所有适配器的偏移量（适配器名 -> 偏移值）。</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT adapter_name, offset_value FROM processing_offsets;";

        var offsets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            offsets[reader.GetString(0)] = reader.GetString(1);
        }

        return offsets;
    }

    /// <summary>
    /// 保存（覆盖）指定适配器的偏移量。使用 UPSERT 语义：存在则更新，不存在则插入。
    /// </summary>
    public async Task SaveAsync(string adapterName, string offsetValue, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_offsets (adapter_name, offset_value, updated_at)
            VALUES ($adapterName, $offsetValue, $updatedAt)
            ON CONFLICT(adapter_name)
            DO UPDATE SET offset_value = excluded.offset_value, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$adapterName", adapterName);
        command.Parameters.AddWithValue("$offsetValue", offsetValue);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
