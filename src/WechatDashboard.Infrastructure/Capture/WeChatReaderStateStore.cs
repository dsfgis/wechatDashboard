using System.IO;
using System.Text.Json;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>不含数据库密钥和消息正文的原生 reader 状态。</summary>
public sealed record WeChatReaderState(
    string ReaderVersion,
    string DatabaseRoot,
    string BootstrapRange,
    string? SchemaFingerprint,
    DateTimeOffset? LastValidatedAt,
    IReadOnlyDictionary<string, string>? ShardHighWatermarks = null);

/// <summary>在 LocalAppData 下原子读写无密钥 reader 状态。</summary>
public sealed class WeChatReaderStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath;

    public WeChatReaderStateStore(string? statePath = null)
    {
        _statePath = Path.GetFullPath(
            statePath ?? Path.Combine(ProjectToolPaths.WeChatNativeReaderDirectory, "state.json"));
    }

    public string StatePath => _statePath;

    public async Task<WeChatReaderState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        await using var input = new FileStream(
            _statePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<WeChatReaderState>(
            input,
            JsonOptions,
            cancellationToken);
    }

    public async Task SaveAsync(WeChatReaderState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = _statePath + ".tmp";
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, state, JsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
