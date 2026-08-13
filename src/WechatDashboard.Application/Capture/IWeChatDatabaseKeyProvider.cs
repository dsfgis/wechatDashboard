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

    public string ToHexString()
    {
        var keyBytes = _keyBytes ?? throw new ObjectDisposedException(nameof(WeChatDatabaseKeyLease));
        return Convert.ToHexStringLower(keyBytes);
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
