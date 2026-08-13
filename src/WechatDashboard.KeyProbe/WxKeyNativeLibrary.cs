using System.Runtime.InteropServices;
using System.Text;

namespace WechatDashboard.KeyProbe;

/// <summary>动态加载 wx_key.dll，确保第三方原生代码只存在于 KeyProbe 进程。</summary>
internal sealed class WxKeyNativeLibrary : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool InitializeHookDelegate(uint targetPid);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool PollKeyDataDelegate(StringBuilder keyBuffer, int bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool GetStatusMessageDelegate(StringBuilder statusBuffer, int bufferSize, out int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool CleanupHookDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetLastErrorMsgDelegate();

    private readonly IntPtr _libraryHandle;
    private readonly InitializeHookDelegate _initializeHook;
    private readonly PollKeyDataDelegate _pollKeyData;
    private readonly GetStatusMessageDelegate _getStatusMessage;
    private readonly CleanupHookDelegate _cleanupHook;
    private readonly GetLastErrorMsgDelegate _getLastErrorMsg;
    private bool _initialized;
    private bool _disposed;

    public WxKeyNativeLibrary(string dllPath)
    {
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("wx_key.dll was not found.", dllPath);
        }

        _libraryHandle = NativeLibrary.Load(dllPath);
        try
        {
            _initializeHook = LoadDelegate<InitializeHookDelegate>("InitializeHook");
            _pollKeyData = LoadDelegate<PollKeyDataDelegate>("PollKeyData");
            _getStatusMessage = LoadDelegate<GetStatusMessageDelegate>("GetStatusMessage");
            _cleanupHook = LoadDelegate<CleanupHookDelegate>("CleanupHook");
            _getLastErrorMsg = LoadDelegate<GetLastErrorMsgDelegate>("GetLastErrorMsg");
        }
        catch
        {
            NativeLibrary.Free(_libraryHandle);
            throw;
        }
    }

    public void Initialize(int processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initializeHook(checked((uint)processId)))
        {
            var pointer = _getLastErrorMsg();
            var detail = pointer == IntPtr.Zero ? "unknown native error" : Marshal.PtrToStringUTF8(pointer);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "unknown native error" : detail);
        }

        _initialized = true;
    }

    public bool TryPollKey(out string keyText)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var keyBuffer = new StringBuilder(128);
        try
        {
            if (!_pollKeyData(keyBuffer, keyBuffer.Capacity))
            {
                keyText = "";
                return false;
            }

            keyText = keyBuffer.ToString();
            return true;
        }
        finally
        {
            keyBuffer.Clear();
        }
    }

    public void DrainStatusMessages()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var statusBuffer = new StringBuilder(1024);
        for (var count = 0; count < 1000; count++)
        {
            statusBuffer.Clear();
            if (!_getStatusMessage(statusBuffer, statusBuffer.Capacity, out _))
            {
                break;
            }
        }

        statusBuffer.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_initialized)
            {
                _cleanupHook();
            }
        }
        finally
        {
            NativeLibrary.Free(_libraryHandle);
            _disposed = true;
        }
    }

    private TDelegate LoadDelegate<TDelegate>(string exportName)
        where TDelegate : Delegate
    {
        var address = NativeLibrary.GetExport(_libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }
}
