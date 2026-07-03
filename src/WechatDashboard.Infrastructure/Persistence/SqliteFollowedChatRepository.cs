using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 基于 SQLite 的关注群仓储实现。
/// 群名按文本唯一约束存储，重复保存会重新激活（软删除后恢复）。
/// </summary>
public sealed class SqliteFollowedChatRepository : IFollowedChatRepository
{
    private readonly string _databasePath;

    public SqliteFollowedChatRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取所有启用状态的关注群，按 ID 排序。</summary>
    public async Task<IReadOnlyList<FollowedChat>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, chat_name, is_active, created_at
            FROM followed_chats
            WHERE is_active = 1
            ORDER BY id;
            """;

        var chats = new List<FollowedChat>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            chats.Add(ReadChat(reader));
        }

        return chats;
    }

    /// <summary>
    /// 保存关注群：存在则重新激活，不存在则新增。返回最新状态的实体。
    /// </summary>
    public async Task<FollowedChat> SaveAsync(string chatName, CancellationToken cancellationToken)
    {
        var normalized = chatName.Trim();
        var now = DateTimeOffset.Now;
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO followed_chats (chat_name, is_active, created_at)
            VALUES ($chatName, 1, $createdAt)
            ON CONFLICT(chat_name)
            DO UPDATE SET is_active = 1;
            """;
        command.Parameters.AddWithValue("$chatName", normalized);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 回读最新状态
        command.Parameters.Clear();
        command.CommandText = "SELECT id, chat_name, is_active, created_at FROM followed_chats WHERE chat_name = $chatName;";
        command.Parameters.AddWithValue("$chatName", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadChat(reader);
        }

        return new FollowedChat(0, normalized, true, now);
    }

    /// <summary>按 ID 物理删除关注群。</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM followed_chats WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>读取关注群过滤模式（默认 Include）。</summary>
    public async Task<FollowedChatFilterMode> GetFilterModeAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = 'followed_chat_filter_mode';";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is string s && int.TryParse(s, out var v) && Enum.IsDefined(typeof(FollowedChatFilterMode), v))
        {
            return (FollowedChatFilterMode)v;
        }
        return FollowedChatFilterMode.Include;
    }

    /// <summary>保存关注群过滤模式。</summary>
    public async Task SetFilterModeAsync(FollowedChatFilterMode mode, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ('followed_chat_filter_mode', $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$value", ((int)mode).ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>从 DataReader 映射为 FollowedChat 实体。</summary>
    private static FollowedChat ReadChat(SqliteDataReader reader)
    {
        return new FollowedChat(
            Id: reader.GetInt64(0),
            ChatName: reader.GetString(1),
            IsActive: reader.GetInt32(2) == 1,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(3)));
    }
}
