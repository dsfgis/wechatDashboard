using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>IMAP 登录凭据。密码只在本机内存与 DPAPI 密文中存在，不写入配置、日志或数据库。</summary>
public sealed record ImapCredential(string Account, string Password)
{
    /// <summary>账号与密码是否都非空。</summary>
    public bool IsComplete
        => !string.IsNullOrWhiteSpace(Account) && !string.IsNullOrEmpty(Password);
}

/// <summary>DPAPI 加解密抽象，便于在测试中替换为可逆的假实现。</summary>
public interface IImapCredentialProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>绑定当前 Windows 用户登录凭据的 IMAP 凭据保护实现。</summary>
public sealed class WindowsCurrentUserImapCredentialProtector : IImapCredentialProtector
{
    private static readonly byte[] Entropy = "WechatDashboard.ImapCredential.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(
        plaintext,
        Entropy,
        DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(
        protectedBytes,
        Entropy,
        DataProtectionScope.CurrentUser);
}

/// <summary>
/// 使用 Windows DPAPI CurrentUser 保护 IMAP 账号密码的本地密文存储。
/// 文件格式：魔数(4) + 版本(1) + 密文长度(4, LE) + 密文；明文为 <c>账号\n密码</c> 的 UTF-8 字节。
/// </summary>
public sealed class ProtectedImapCredentialStore
{
    private static readonly byte[] Magic = "SMCP"u8.ToArray();
    private const byte FormatVersion = 1;
    private const int HeaderLength = 9;
    private const int MaximumProtectedLength = 16 * 1024;
    private readonly string _path;
    private readonly IImapCredentialProtector _protector;

    public ProtectedImapCredentialStore(string? path = null, IImapCredentialProtector? protector = null)
    {
        _path = System.IO.Path.GetFullPath(path ?? ProjectToolPaths.MailProtectedCredentialPath);
        _protector = protector ?? new WindowsCurrentUserImapCredentialProtector();
    }

    /// <summary>凭据文件的绝对路径。</summary>
    public string FilePath => _path;

    /// <summary>是否已保存过凭据。</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>读取凭据；文件不存在或内容无效时返回 null，不抛异常。</summary>
    public ImapCredential? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            var payload = File.ReadAllBytes(_path);
            if (payload.Length < HeaderLength ||
                !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                payload[4] != FormatVersion)
            {
                return null;
            }

            var protectedLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4));
            if (protectedLength <= 0 || protectedLength > MaximumProtectedLength ||
                payload.Length != HeaderLength + protectedLength)
            {
                return null;
            }

            plaintext = _protector.Unprotect(payload.AsSpan(HeaderLength, protectedLength).ToArray());
            var text = Encoding.UTF8.GetString(plaintext);
            var separator = text.IndexOf('\n');
            if (separator <= 0)
            {
                return null;
            }

            var credential = new ImapCredential(text[..separator], text[(separator + 1)..]);
            return credential.IsComplete ? credential : null;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // 密文损坏或不属于当前用户时按“未配置”处理，由界面提示重新填写。
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <summary>原子写入凭据密文；明文缓冲区在处理后清零。</summary>
    public void Save(ImapCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!credential.IsComplete)
        {
            throw new ArgumentException("IMAP 账号与密码不能为空。", nameof(credential));
        }

        var plaintext = Encoding.UTF8.GetBytes($"{credential.Account}\n{credential.Password}");
        string? temporaryPath = null;
        try
        {
            var protectedBytes = _protector.Protect(plaintext);
            var payload = new byte[HeaderLength + protectedBytes.Length];
            Magic.CopyTo(payload, 0);
            payload[4] = FormatVersion;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), protectedBytes.Length);
            protectedBytes.CopyTo(payload, HeaderLength);

            var directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("IMAP 凭据路径缺少父目录。");
            Directory.CreateDirectory(directory);
            temporaryPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
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

    /// <summary>删除已保存的凭据。</summary>
    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
