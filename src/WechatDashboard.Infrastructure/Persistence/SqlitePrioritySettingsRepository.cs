using System.Text.Json;
using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Common;

namespace WechatDashboard.Infrastructure.Persistence;

/// <summary>
/// 优先级层数/颜色配置的 SQLite 实现，复用 app_settings 表存放一段 JSON 颜色数组。
/// 读取时做完整校验：缺键、坏 JSON、层数越界或颜色非法都回退到默认 4 级色板，
/// 保证配色问题永远不会阻塞待办与日程的显示。
/// </summary>
public sealed class SqlitePrioritySettingsRepository : IPrioritySettingsRepository
{
    private const string ColorsKey = "priority_level_colors";
    private readonly string _databasePath;

    /// <summary>使用指定 SQLite 数据库创建优先级设置仓储。</summary>
    public SqlitePrioritySettingsRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>读取颜色数组；任何异常配置都返回默认色板。</summary>
    public async Task<IReadOnlyList<string>> GetColorsAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", ColorsKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string json || !TryParseColors(json, out var colors))
        {
            return PriorityPalette.DefaultColors.Take(PriorityPalette.DefaultLevelCount).ToArray();
        }

        return colors;
    }

    /// <summary>以 UPSERT 保存颜色数组，长度与颜色格式在写入前校验。</summary>
    public async Task SaveColorsAsync(IReadOnlyList<string> colors, CancellationToken cancellationToken)
    {
        if (PriorityPalette.Create(colors) is not { } palette)
        {
            throw new ArgumentException(
                $"优先级颜色配置无效：需要 {PriorityPalette.MinLevelCount}–{PriorityPalette.MaxLevelCount} 个 #RRGGBB 颜色。",
                nameof(colors));
        }

        var json = JsonSerializer.Serialize(palette.Colors);
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = $value;
            """;
        command.Parameters.AddWithValue("$key", ColorsKey);
        command.Parameters.AddWithValue("$value", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>解析 JSON 数组并按色板规则校验。</summary>
    private static bool TryParseColors(string json, out IReadOnlyList<string> colors)
    {
        colors = Array.Empty<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(json);
            if (PriorityPalette.Create(parsed) is not { } palette)
            {
                return false;
            }

            colors = palette.Colors;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
