using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

public interface IWeChatKeyProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>绑定当前 Windows 用户登录凭据的数据保护实现。</summary>
public sealed class WindowsCurrentUserWeChatKeyProtector : IWeChatKeyProtector
{
    private static readonly byte[] Entropy = "WechatDashboard.WeChatDatabaseKey.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(
        plaintext,
        Entropy,
        DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(
        protectedBytes,
        Entropy,
        DataProtectionScope.CurrentUser);
}

/// <summary>使用 Windows DPAPI CurrentUser 保护微信数据库密钥的本地密文存储。</summary>
public sealed class ProtectedWeChatDatabaseKeyStore : IWeChatDatabaseKeyStore
{
    private static readonly byte[] Magic = "WDPK"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int HeaderLength = 9;
    private const int MaximumProtectedLength = 16 * 1024;
    private readonly string _path;
    private readonly IWeChatKeyProtector _protector;

    public ProtectedWeChatDatabaseKeyStore(
        string? path = null,
        IWeChatKeyProtector? protector = null)
    {
        _path = Path.GetFullPath(path ?? ProjectToolPaths.WeChatProtectedKeyPath);
        _protector = protector ?? new WindowsCurrentUserWeChatKeyProtector();
    }

    public async Task<WeChatDatabaseKeyLease?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            var payload = await File.ReadAllBytesAsync(_path, cancellationToken);
            if (payload.Length < HeaderLength ||
                !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                payload[4] != FormatVersion)
            {
                throw InvalidCache("受保护的微信 Key 文件格式无效。");
            }

            var protectedLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4));
            if (protectedLength <= 0 || protectedLength > MaximumProtectedLength ||
                payload.Length != HeaderLength + protectedLength)
            {
                throw InvalidCache("受保护的微信 Key 文件长度无效。");
            }

            var protectedBytes = payload.AsSpan(HeaderLength, protectedLength).ToArray();
            plaintext = _protector.Unprotect(protectedBytes);
            if (plaintext.Length != WeChatDatabaseKeyLease.KeyLength)
            {
                throw InvalidCache("受保护的微信 Key 内容无效。");
            }

            return new WeChatDatabaseKeyLease(plaintext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WeChatReaderException)
        {
            throw;
        }
        catch (CryptographicException exception)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.ProtectedKey,
                WeChatReaderErrorCode.ProtectedKeyUnavailable,
                "受保护的微信 Key 无法由当前 Windows 用户解密，请重新提取 Key。",
                exception);
        }
        catch (IOException exception)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.ProtectedKey,
                WeChatReaderErrorCode.ProtectedKeyUnavailable,
                "无法读取受保护的微信 Key 文件，请重新提取 Key。",
                exception);
        }
        catch (Exception exception)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.ProtectedKey,
                WeChatReaderErrorCode.ProtectedKeyUnavailable,
                "无法读取受保护的微信 Key 文件，请重新提取 Key。",
                exception);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public async Task SaveAsync(WeChatDatabaseKeyLease keyLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyLease);
        var plaintext = new byte[WeChatDatabaseKeyLease.KeyLength];
        string? temporaryPath = null;
        try
        {
            keyLease.CopyTo(plaintext);
            var protectedBytes = _protector.Protect(plaintext);
            var payload = new byte[HeaderLength + protectedBytes.Length];
            Magic.CopyTo(payload, 0);
            payload[4] = FormatVersion;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), protectedBytes.Length);
            protectedBytes.CopyTo(payload, HeaderLength);

            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("受保护的微信 Key 路径缺少父目录。");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not WeChatReaderException)
        {
            throw new WeChatReaderException(
                WeChatReaderStage.ProtectedKey,
                WeChatReaderErrorCode.ProtectedKeyUnavailable,
                "无法保存受保护的微信 Key 文件。",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    private static WeChatReaderException InvalidCache(string message) => new(
        WeChatReaderStage.ProtectedKey,
        WeChatReaderErrorCode.ProtectedKeyInvalid,
        message);
}
