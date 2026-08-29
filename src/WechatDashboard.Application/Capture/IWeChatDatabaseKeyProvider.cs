using System.Security.Cryptography;

namespace WechatDashboard.Application.Capture;

/// <summary>为当前登录的微信进程获取数据库主密钥。</summary>
public interface IWeChatDatabaseKeyProvider
{
    string Name { get; }

    Task<WeChatDatabaseKeyLease> AcquireAsync(
        WeChatDatabaseKeyRequest request,
        CancellationToken cancellationToken);
}

/// <summary>持久化受操作系统保护的微信数据库密钥；实现不得落盘明文。</summary>
public interface IWeChatDatabaseKeyStore
{
    Task<WeChatDatabaseKeyLease?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(WeChatDatabaseKeyLease keyLease, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>密钥获取请求。密钥提供器不得把密钥写入参数、日志或普通文件。</summary>
public sealed record WeChatDatabaseKeyRequest(int ProcessId, TimeSpan Timeout);

/// <summary>短生命周期数据库密钥；释放时清零托管缓冲区。</summary>
public sealed class WeChatDatabaseKeyLease : IDisposable
{
    public const int KeyLength = 32;
    private byte[]? _keyBytes;

    public WeChatDatabaseKeyLease(ReadOnlySpan<byte> keyBytes)
    {
        if (keyBytes.Length != KeyLength)
        {
            throw new ArgumentException($"WeChat database keys must contain exactly {KeyLength} bytes.", nameof(keyBytes));
        }

        _keyBytes = keyBytes.ToArray();
    }

    public bool IsDisposed => _keyBytes is null;

    /// <summary>
    /// 将密钥复制到调用方提供的固定长度缓冲区。
    /// 调用方必须在使用后通过 CryptographicOperations.ZeroMemory 清理目标缓冲区。
    /// </summary>
    public void CopyTo(Span<byte> destination)
    {
        var keyBytes = _keyBytes ?? throw new ObjectDisposedException(nameof(WeChatDatabaseKeyLease));
        if (destination.Length != KeyLength)
        {
            throw new ArgumentException($"Destination must contain exactly {KeyLength} bytes.", nameof(destination));
        }

        keyBytes.CopyTo(destination);
    }

    public void Dispose()
    {
        if (_keyBytes is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_keyBytes);
        _keyBytes = null;
        GC.SuppressFinalize(this);
    }

    ~WeChatDatabaseKeyLease()
    {
        if (_keyBytes is not null)
        {
            CryptographicOperations.ZeroMemory(_keyBytes);
        }
    }
}
