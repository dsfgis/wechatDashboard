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
    private const int MetadataFieldLength = 16;
    private const int MemoryScanChunkLength = 1024 * 1024;
    private const long Maximum32BitAddress = 0x80000000L;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private int? _cachedProcessId;
    private long? _cachedMetadataAddress;

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

        try
        {
            if (_cachedProcessId == process.Id && _cachedMetadataAddress is { } cachedAddress &&
                TryCreateSecret(processHandle, cachedAddress, out var cachedSecret))
            {
                return cachedSecret;
            }

            var managerPointerAddress = checked(module.BaseAddress.ToInt64() + DatabaseManagerPointerOffset);
            var managerPointer = ReadUInt32(processHandle, managerPointerAddress);
            if (managerPointer != 0 && TryCreateSecret(processHandle, managerPointer, out var fixedOffsetSecret))
            {
                CacheMetadataAddress(process.Id, managerPointer);
                return fixedOffsetSecret;
            }

            if (TryFindSecret(processHandle, out var discoveredAddress, out var discoveredSecret))
            {
                CacheMetadataAddress(process.Id, discoveredAddress);
                return discoveredSecret;
            }

            throw new InvalidOperationException("当前石化通数据库元数据格式不受支持。");
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private void CacheMetadataAddress(int processId, long metadataAddress)
    {
        _cachedProcessId = processId;
        _cachedMetadataAddress = metadataAddress;
    }

    private static bool TryFindSecret(
        IntPtr processHandle,
        out long metadataAddress,
        out ShihuatongDatabaseSecret secret)
    {
        metadataAddress = 0;
        secret = null!;
        var address = 0L;
        var informationLength = Marshal.SizeOf<MemoryBasicInformation>();
        while (address < Maximum32BitAddress &&
               VirtualQueryEx(
                   processHandle,
                   new IntPtr(address),
                   out var information,
                   informationLength) != 0)
        {
            var regionStart = information.BaseAddress.ToInt64();
            var regionLength = checked((long)information.RegionSize.ToUInt64());
            if (regionLength <= 0)
            {
                break;
            }

            if (information.State == MemCommit &&
                (information.Protect & (PageNoAccess | PageGuard)) == 0)
            {
                var regionEnd = Math.Min(Maximum32BitAddress, regionStart + regionLength);
                for (var chunkStart = regionStart;
                     chunkStart < regionEnd;
                     chunkStart += MemoryScanChunkLength - MetadataFieldLength)
                {
                    var requestedLength = checked((int)Math.Min(MemoryScanChunkLength, regionEnd - chunkStart));
                    if (requestedLength < MetadataFieldLength)
                    {
                        break;
                    }

                    var buffer = new byte[requestedLength];
                    if (!ReadProcessMemory(
                            processHandle,
                            new IntPtr(chunkStart),
                            buffer,
                            requestedLength,
                            out var bytesRead) ||
                        bytesRead.ToInt64() < MetadataFieldLength)
                    {
                        continue;
                    }

                    var usableLength = checked((int)bytesRead.ToInt64());
                    for (var offset = 0; offset <= usableLength - MetadataFieldLength; offset += sizeof(uint))
                    {
                        if (!LooksLikeMetadataHeader(buffer, offset))
                        {
                            continue;
                        }

                        var candidateAddress = chunkStart + offset;
                        if (TryCreateSecret(processHandle, candidateAddress, out secret))
                        {
                            metadataAddress = candidateAddress;
                            return true;
                        }
                    }
                }
            }

            address = checked(regionStart + regionLength);
        }

        return false;
    }

    private static bool LooksLikeMetadataHeader(byte[] buffer, int offset)
    {
        var dataRootPointer = BitConverter.ToUInt32(buffer, offset);
        var dataRootLength = BitConverter.ToUInt32(buffer, offset + sizeof(uint));
        var keyPointer = BitConverter.ToUInt32(buffer, offset + sizeof(uint) * 2);
        var keyLength = BitConverter.ToUInt32(buffer, offset + sizeof(uint) * 3);
        return dataRootPointer >= 0x10000 && dataRootPointer < Maximum32BitAddress &&
               dataRootLength is >= 10 and <= MaximumDataRootLength &&
               keyPointer >= 0x10000 && keyPointer < Maximum32BitAddress &&
               keyLength == ExpectedKeyLength;
    }

    private static bool TryCreateSecret(
        IntPtr processHandle,
        long metadataAddress,
        out ShihuatongDatabaseSecret secret)
    {
        secret = null!;
        byte[]? keyBytes = null;
        try
        {
            var fields = ReadBytes(processHandle, metadataAddress, MetadataFieldLength);
            if (!LooksLikeMetadataHeader(fields, 0))
            {
                return false;
            }

            var dataRootPointer = BitConverter.ToUInt32(fields, 0);
            var dataRootLength = checked((int)BitConverter.ToUInt32(fields, sizeof(uint)));
            var keyPointer = BitConverter.ToUInt32(fields, sizeof(uint) * 2);
            var dataRoot = new UTF8Encoding(false, true).GetString(
                ReadBytes(processHandle, dataRootPointer, dataRootLength));
            if (!Directory.Exists(dataRoot))
            {
                return false;
            }

            var accountDirectory = ShihuatongDataDirectoryLocator.FindActiveAccountDirectory(dataRoot);
            if (accountDirectory is null)
            {
                return false;
            }

            keyBytes = ReadBytes(processHandle, keyPointer, ExpectedKeyLength);
            if (!IsValidKey(keyBytes))
            {
                return false;
            }

            secret = new ShihuatongDatabaseSecret(accountDirectory, keyBytes);
            keyBytes = null;
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or DecoderFallbackException or IOException)
        {
            return false;
        }
        finally
        {
            if (keyBytes is not null)
            {
                Array.Clear(keyBytes, 0, keyBytes.Length);
            }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(
        IntPtr process,
        IntPtr address,
        out MemoryBasicInformation information,
        int informationLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }
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
