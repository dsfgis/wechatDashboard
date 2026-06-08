using Microsoft.Data.Sqlite;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Infrastructure.Persistence;

public sealed class SqliteCaptureSourceSettingsRepository : ICaptureSourceSettingsRepository
{
    private readonly string _databasePath;

    public SqliteCaptureSourceSettingsRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<CaptureSourceSettings>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source, display_name, kind, location, is_enabled, created_at, updated_at FROM capture_source_settings ORDER BY source, kind;";

        var settings = new List<CaptureSourceSettings>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            settings.Add(ReadSettings(reader));
        }

        return settings;
    }

    public async Task SaveAsync(CaptureSourceSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO capture_source_settings (source, display_name, kind, location, is_enabled, created_at, updated_at)
            VALUES ($source, $displayName, $kind, $location, $isEnabled, $createdAt, $updatedAt)
            ON CONFLICT(source, kind)
            DO UPDATE SET display_name = excluded.display_name, location = excluded.location, is_enabled = excluded.is_enabled, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$source", settings.Source);
        command.Parameters.AddWithValue("$displayName", settings.DisplayName);
        command.Parameters.AddWithValue("$kind", settings.Kind);
        command.Parameters.AddWithValue("$location", settings.Location);
        command.Parameters.AddWithValue("$isEnabled", settings.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", settings.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAllAsync(IReadOnlyList<CaptureSourceSettings> settings, CancellationToken cancellationToken)
    {
        foreach (var setting in settings)
        {
            await SaveAsync(setting, cancellationToken);
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = SqliteConnectionFactory.Open(_databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM capture_source_settings;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CaptureSourceSettings ReadSettings(SqliteDataReader reader)
    {
        return new CaptureSourceSettings(
            Id: reader.GetInt64(0),
            Source: reader.GetString(1),
            DisplayName: reader.GetString(2),
            Kind: reader.GetString(3),
            Location: reader.GetString(4),
            IsEnabled: reader.GetInt32(5) == 1,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(6)),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(7)));
    }
}
