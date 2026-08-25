using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WeChatDerivedDatabaseKey : IDisposable
{
    private byte[]? _keyBytes;

    internal WeChatDerivedDatabaseKey(string relativePath, ReadOnlySpan<byte> keyBytes)
    {
        RelativePath = relativePath;
        _keyBytes = keyBytes.ToArray();
    }

    public string RelativePath { get; }

    public bool IsDisposed => _keyBytes is null;

    public void CopyTo(Span<byte> destination)
    {
        var keyBytes = _keyBytes ?? throw new ObjectDisposedException(nameof(WeChatDerivedDatabaseKey));
        if (destination.Length != keyBytes.Length)
        {
            throw new ArgumentException("Destination length does not match the derived database key.", nameof(destination));
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

    ~WeChatDerivedDatabaseKey()
    {
        if (_keyBytes is not null)
        {
            CryptographicOperations.ZeroMemory(_keyBytes);
        }
    }
}

public sealed class WeChatDatabaseKeySet : IDisposable
{
    private IReadOnlyDictionary<string, WeChatDerivedDatabaseKey>? _keys;

    internal WeChatDatabaseKeySet(IReadOnlyDictionary<string, WeChatDerivedDatabaseKey> keys)
    {
        _keys = keys;
    }

    public IReadOnlyCollection<string> RelativePaths =>
        (_keys ?? throw new ObjectDisposedException(nameof(WeChatDatabaseKeySet))).Keys.ToArray();

    public WeChatDerivedDatabaseKey GetRequired(string relativePath)
    {
        var keys = _keys ?? throw new ObjectDisposedException(nameof(WeChatDatabaseKeySet));
        return keys.TryGetValue(relativePath, out var key)
            ? key
            : throw new KeyNotFoundException($"No validated key exists for {relativePath}.");
    }

    public void Dispose()
    {
        if (_keys is null)
        {
            return;
        }

        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }

        _keys = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>按每个数据库首页 salt 派生并验证微信 SQLCipher V4 数据库密钥。</summary>
public sealed class WeChatDatabaseKeyDeriver
{
    public const int PageSize = 4096;
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int RoundCount = 256_000;

    public async Task<WeChatDatabaseKeySet> DeriveAsync(
        WeChatDatabaseKeyLease masterKeyLease,
        IReadOnlyList<WeChatDatabaseDescriptor> databases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(masterKeyLease);
        ArgumentNullException.ThrowIfNull(databases);
        var masterKey = new byte[KeySize];
        var derived = new Dictionary<string, WeChatDerivedDatabaseKey>(StringComparer.OrdinalIgnoreCase);
        masterKeyLease.CopyTo(masterKey);
        try
        {
            foreach (var database in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await ReadFirstPageAsync(database.FullPath, cancellationToken);
                try
                {
                    var key = Rfc2898DeriveBytes.Pbkdf2(
                        masterKey,
                        page.AsSpan(0, SaltSize),
                        RoundCount,
                        HashAlgorithmName.SHA512,
                        KeySize);
                    if (!VerifyPage(key, page))
                    {
                        CryptographicOperations.ZeroMemory(key);
                        continue;
                    }

                    derived[database.RelativePath] = new WeChatDerivedDatabaseKey(database.RelativePath, key);
                    CryptographicOperations.ZeroMemory(key);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(page);
                }
            }

            ValidateRequiredKeys(derived);
            return new WeChatDatabaseKeySet(derived);
        }
        catch
        {
            foreach (var key in derived.Values)
            {
                key.Dispose();
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public static bool VerifyPage(ReadOnlySpan<byte> databaseKey, ReadOnlySpan<byte> page)
    {
        if (databaseKey.Length != KeySize || page.Length < PageSize)
        {
            return false;
        }

        return VerifyLegacyPage(databaseKey, page) ||
               VerifyV4Page(databaseKey, page, 64) ||
               VerifyV4Page(databaseKey, page, 80);
    }

    private static bool VerifyLegacyPage(ReadOnlySpan<byte> databaseKey, ReadOnlySpan<byte> page)
    {
        var macKey = DeriveMacKey(databaseKey, page[..SaltSize]);
        try
        {
            using var hmac = new HMACSHA512(macKey);
            hmac.TransformBlock(page.Slice(SaltSize, 4016).ToArray(), 0, 4016, null, 0);
            Span<byte> pageNumber = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(pageNumber, 1);
            hmac.TransformFinalBlock(pageNumber.ToArray(), 0, pageNumber.Length);
            return CryptographicOperations.FixedTimeEquals(hmac.Hash!, page.Slice(PageSize - 64, 64));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static bool VerifyV4Page(
        ReadOnlySpan<byte> databaseKey,
        ReadOnlySpan<byte> page,
        int reserveSize)
    {
        var contentEnd = PageSize - reserveSize;
        if (contentEnd <= SaltSize)
        {
            return false;
        }

        var macKey = DeriveMacKey(databaseKey, page[..SaltSize]);
        try
        {
            using var hmac = new HMACSHA512(macKey);
            var salt = page[..SaltSize].ToArray();
            hmac.TransformBlock(salt, 0, salt.Length, null, 0);
            Span<byte> pageNumber = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(pageNumber, 1);
            var number = pageNumber.ToArray();
            hmac.TransformBlock(number, 0, number.Length, null, 0);
            var ciphertext = page.Slice(SaltSize, contentEnd - SaltSize).ToArray();
            hmac.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            CryptographicOperations.ZeroMemory(ciphertext);
            return CryptographicOperations.FixedTimeEquals(hmac.Hash!, page.Slice(PageSize - 64, 64));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static byte[] DeriveMacKey(ReadOnlySpan<byte> databaseKey, ReadOnlySpan<byte> salt)
    {
        var macSalt = salt.ToArray();
        for (var index = 0; index < macSalt.Length; index++)
        {
            macSalt[index] ^= 0x3A;
        }

        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                databaseKey,
                macSalt,
                2,
                HashAlgorithmName.SHA512,
                KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macSalt);
        }
    }

    private static async Task<byte[]> ReadFirstPageAsync(string path, CancellationToken cancellationToken)
    {
        var page = new byte[PageSize];
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            PageSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await input.ReadExactlyAsync(page, cancellationToken);
            return page;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(page);
            throw;
        }
    }

    private static void ValidateRequiredKeys(
        IReadOnlyDictionary<string, WeChatDerivedDatabaseKey> keys)
    {
        var hasSession = keys.ContainsKey("session/session.db");
        var hasContact = keys.ContainsKey("contact/contact.db");
        var hasMessage = keys.Keys.Any(WeChatDatabaseDiscovery.IsMessageDatabase);
        if (hasSession && hasContact && hasMessage)
        {
            return;
        }

        throw new WeChatReaderException(
            WeChatReaderStage.KeyValidation,
            WeChatReaderErrorCode.KeyInvalid,
            "The database key did not validate all required WeChat databases.");
    }
}
