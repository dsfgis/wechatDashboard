using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 以最小只读权限获取石化通进程内的数据库目录和解密密钥。
/// 密钥只保存在短生命周期 byte[] 中，不写日志、不写磁盘，并在释放时清零。
/// </summary>
internal sealed class ShihuatongProcessDatabaseKeyProvider
{
    private const string ProcessName = "LxMainNew";
    private const string DatabaseModuleName = "imcore.dll";
    private const long DatabaseManagerPointerOffset = 0x0505500C;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const int ExpectedKeyLength = 32;
    private const int MaximumDataRootLength = 1024;

    public bool IsProcessRunning
    {
        get
        {
            var candidate = FindDatabaseProcess();
            candidate.Process?.Dispose();
            return candidate.Process is not null;
        }
    }

    public ShihuatongDatabaseSecret Acquire()
    {
        var candidate = FindDatabaseProcess();
        using var process = candidate.Process
            ?? throw new InvalidOperationException("石化通数据库模块尚未加载，请登录后重试。");
        var module = candidate.Module!;

        if (module.ModuleMemorySize <= DatabaseManagerPointerOffset + sizeof(uint))
        {
            throw new InvalidOperationException("当前石化通版本与本地数据库读取器不兼容。");
        }

        var processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法以只读权限访问石化通进程。");
        }

        byte[]? keyBytes = null;
        try
        {
            var managerPointerAddress = checked(module.BaseAddress.ToInt64() + DatabaseManagerPointerOffset);
            var managerPointer = ReadUInt32(processHandle, managerPointerAddress);
            if (managerPointer == 0)
            {
                throw new InvalidOperationException("石化通数据库尚未初始化，请登录后重试。");
            }

            var fields = ReadBytes(processHandle, managerPointer, 16);
            var dataRootPointer = BitConverter.ToUInt32(fields, 0);
            var dataRootLength = checked((int)BitConverter.ToUInt32(fields, 4));
            var keyPointer = BitConverter.ToUInt32(fields, 8);
            var keyLength = checked((int)BitConverter.ToUInt32(fields, 12));

            if (dataRootPointer == 0 || dataRootLength is < 10 or > MaximumDataRootLength ||
                keyPointer == 0 || keyLength != ExpectedKeyLength)
            {
                throw new InvalidOperationException("当前石化通数据库元数据格式不受支持。");
            }

            var dataRoot = Encoding.UTF8.GetString(ReadBytes(processHandle, dataRootPointer, dataRootLength));
            if (!Directory.Exists(dataRoot))
            {
                throw new InvalidOperationException("未找到石化通本地数据目录。");
            }

            keyBytes = ReadBytes(processHandle, keyPointer, keyLength);
            if (!IsValidKey(keyBytes))
            {
                throw new InvalidOperationException("石化通数据库密钥格式校验失败，未尝试解密。");
            }

            var accountDirectory = ShihuatongDataDirectoryLocator.FindActiveAccountDirectory(dataRoot)
                ?? throw new InvalidOperationException("未找到当前石化通账户的消息数据库。");
            var secret = new ShihuatongDatabaseSecret(accountDirectory, keyBytes);
            keyBytes = null;
            return secret;
        }
        finally
        {
            if (keyBytes is not null)
            {
                Array.Clear(keyBytes, 0, keyBytes.Length);
            }
            CloseHandle(processHandle);
        }
    }

    private static bool IsValidKey(byte[] key)
    {
        return key.Length == ExpectedKeyLength && key.All(value =>
            value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F');
    }

    private static uint ReadUInt32(IntPtr processHandle, long address)
    {
        return BitConverter.ToUInt32(ReadBytes(processHandle, address, sizeof(uint)), 0);
    }

    private static (Process? Process, ProcessModule? Module) FindDatabaseProcess()
    {
        foreach (var candidate in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                var module = candidate.Modules.Cast<ProcessModule>()
                    .FirstOrDefault(item => string.Equals(
                        item.ModuleName,
                        DatabaseModuleName,
                        StringComparison.OrdinalIgnoreCase));
                if (module is not null)
                {
                    return (candidate, module);
                }
            }
            catch (Win32Exception)
            {
            }
            candidate.Dispose();
        }
        return (null, null);
    }
    private static byte[] ReadBytes(IntPtr processHandle, long address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(processHandle, new IntPtr(address), buffer, length, out var bytesRead) ||
            bytesRead.ToInt64() != length)
        {
            Array.Clear(buffer, 0, buffer.Length);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取石化通数据库元数据失败。");
        }
        return buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal static partial class ShihuatongDataDirectoryLocator
{
    [GeneratedRegex("^\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountDirectoryPattern();

    public static string? FindActiveAccountDirectory(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot) || !Directory.Exists(dataRoot)) return null;

        return Directory.EnumerateDirectories(dataRoot)
            .Where(path => AccountDirectoryPattern().IsMatch(Path.GetFileName(path)))
            .Where(path => File.Exists(Path.Combine(path, "mdb.db")) &&
                           Directory.EnumerateFiles(path, "msg_*.db").Any())
            .Select(path => new
            {
                Path = path,
                LastActivity = Directory.EnumerateFiles(path, "*.db*")
                    .Select(File.GetLastWriteTimeUtc)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max()
            })
            .OrderByDescending(candidate => candidate.LastActivity)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }
}

internal sealed class ShihuatongDatabaseSecret : IDisposable
{
    private byte[]? _keyBytes;

    public ShihuatongDatabaseSecret(string databaseDirectory, byte[] keyBytes)
    {
        DatabaseDirectory = databaseDirectory;
        _keyBytes = keyBytes;
    }

    public string DatabaseDirectory { get; }

    public string CreateKeyText()
    {
        var keyBytes = _keyBytes ?? throw new ObjectDisposedException(nameof(ShihuatongDatabaseSecret));
        return Encoding.ASCII.GetString(keyBytes);
    }

    public void Dispose()
    {
        if (_keyBytes is null) return;
        Array.Clear(_keyBytes, 0, _keyBytes.Length);
        _keyBytes = null;
    }
}
