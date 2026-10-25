using System.IO;
using System.Security.Cryptography;
using System.Text;
using WechatDashboard.Infrastructure.Capture;

namespace WechatDashboard.Infrastructure.Agent;

/// <summary>使用当前 Windows 用户的 DPAPI 凭据保护 DeepSeek API Key。</summary>
public sealed class ProtectedDeepSeekApiKeyStore
{
    // 限制输入和密文尺寸，避免把异常大文件读入内存。
    private const int MaxApiKeyLength = 512;
    private const int MaxProtectedBytes = 16 * 1024;
    // 独立 entropy 防止与其他业务的 DPAPI 密文混用。
    private static readonly byte[] Entropy = "WechatDashboard.DeepSeek.ApiKey.v1"u8.ToArray();
    private readonly string _path;

    /// <summary>创建密钥存储；测试可传入独立的临时文件路径。</summary>
    public ProtectedDeepSeekApiKeyStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? ProjectToolPaths.DeepSeekProtectedApiKeyPath);
    }

    /// <summary>读取并解密密钥；文件不存在时返回 null。</summary>
    public string? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[]? plaintext = null;
        try
        {
            // 仅当前 Windows 用户可以解开保存的密文。
            var protectedBytes = File.ReadAllBytes(_path);
            if (protectedBytes.Length == 0 || protectedBytes.Length > MaxProtectedBytes)
            {
                throw new InvalidDataException("受保护的 API Key 文件长度无效。");
            }

            plaintext = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            // 读取时再次校验，拒绝空内容或被替换的异常密文。
            return ValidateApiKey(Encoding.UTF8.GetString(plaintext));
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("无法读取本机 DeepSeek API Key，请重新保存。", exception);
        }
        finally
        {
            // 解密后的字节不再使用时立即清零。
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <summary>加密并原子保存密钥，失败时保留已有正式文件。</summary>
    public void Save(string apiKey)
    {
        var normalizedApiKey = ValidateApiKey(apiKey);
        byte[]? plaintext = null;
        string? temporaryPath = null;
        try
        {
            plaintext = Encoding.UTF8.GetBytes(normalizedApiKey);
            var protectedBytes = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            var directory = Path.GetDirectoryName(_path)
                ?? throw new IOException("DeepSeek API Key 保存路径缺少父目录。");
            Directory.CreateDirectory(directory);

            // 先写临时文件并落盘，再替换正式文件，避免中途失败损坏旧密钥。
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法保存本机 DeepSeek API Key。", exception);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            // 正式文件替换失败时清理残留临时文件。
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>删除本机密钥文件；文件本就不存在时直接返回。</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("无法清除本机 DeepSeek API Key。", exception);
        }
    }

    /// <summary>统一校验并去除密钥首尾空白，不向错误信息写入密钥内容。</summary>
    private static string ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("请输入 DeepSeek API Key。", nameof(apiKey));
        }

        var normalizedApiKey = apiKey.Trim();
        if (normalizedApiKey.Length > MaxApiKeyLength)
        {
            throw new ArgumentException("DeepSeek API Key 长度无效。", nameof(apiKey));
        }

        return normalizedApiKey;
    }
}
