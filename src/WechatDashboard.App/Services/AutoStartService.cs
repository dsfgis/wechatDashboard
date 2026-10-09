using System.IO;
using Microsoft.Win32;

namespace WechatDashboard.App.Services;

/// <summary>
/// 开机启动项的读写抽象（Windows 上为 Run 注册表项）。
/// 抽象出来是为了让注册表访问可替换，从而可在回归测试中不触碰真实用户配置。
/// </summary>
public interface IStartupRunKey
{
    /// <summary>读取指定名称的启动命令；不存在时返回 null。</summary>
    string? GetCommand(string valueName);

    /// <summary>写入或覆盖指定名称的启动命令。</summary>
    void SetCommand(string valueName, string command);

    /// <summary>删除指定名称的启动命令；不存在时不做任何事。</summary>
    void DeleteCommand(string valueName);
}

/// <summary>
/// 当前用户的 Run 注册表项实现：HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 只影响当前用户，不需要管理员权限，也不写入其他用户的配置。
/// </summary>
public sealed class CurrentUserStartupRunKey : IStartupRunKey
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetCommand(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void SetCommand(string valueName, string command)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void DeleteCommand(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null || key.GetValue(valueName) is null)
        {
            return;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>内存实现，仅用于回归测试与诊断，不写入真实注册表。</summary>
public sealed class InMemoryStartupRunKey : IStartupRunKey
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? GetCommand(string valueName) =>
        _values.TryGetValue(valueName, out var command) ? command : null;

    public void SetCommand(string valueName, string command) => _values[valueName] = command;

    public void DeleteCommand(string valueName) => _values.Remove(valueName);
}

/// <summary>开机启动设置的结果：成功与否、最新启用状态以及可选的失败原因。</summary>
public sealed record AutoStartUpdateResult(bool Success, bool IsEnabled, string? ErrorMessage)
{
    public static AutoStartUpdateResult Ok(bool isEnabled) => new(true, isEnabled, null);

    public static AutoStartUpdateResult Failed(bool isEnabled, string errorMessage) =>
        new(false, isEnabled, errorMessage);
}

/// <summary>
/// 管理"开机自动启动"开关：在当前用户的 Run 注册表项中写入或删除本应用的启动命令。
/// 状态以注册表本身为准，不额外保存副本，避免两处状态不一致。
/// </summary>
public sealed class AutoStartService
{
    /// <summary>注册表中使用的启动项名称。</summary>
    public const string ValueName = "WechatDashboard";

    private readonly IStartupRunKey _runKey;
    private readonly Func<string?> _executablePathProvider;

    public AutoStartService(IStartupRunKey runKey, Func<string?> executablePathProvider)
    {
        _runKey = runKey;
        _executablePathProvider = executablePathProvider;
    }

    /// <summary>使用当前进程路径创建面向真实注册表的服务实例。</summary>
    public static AutoStartService CreateDefault() =>
        new(new CurrentUserStartupRunKey(), ResolveExecutablePath);

    /// <summary>读取当前是否已开启开机启动；读取失败时按未开启处理。</summary>
    public bool IsEnabled()
    {
        try
        {
            var command = _runKey.GetCommand(ValueName);
            return !string.IsNullOrWhiteSpace(command);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>开启或关闭开机启动。失败时不改变现有注册表项，并返回失败原因。</summary>
    public AutoStartUpdateResult SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            try
            {
                _runKey.DeleteCommand(ValueName);
                return AutoStartUpdateResult.Ok(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException)
            {
                return AutoStartUpdateResult.Failed(IsEnabled(), $"关闭开机启动失败：{ex.Message}");
            }
        }

        var executablePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return AutoStartUpdateResult.Failed(
                IsEnabled(),
                "无法定位应用程序可执行文件，开机启动未开启。");
        }

        try
        {
            _runKey.SetCommand(ValueName, BuildCommand(executablePath));
            return AutoStartUpdateResult.Ok(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return AutoStartUpdateResult.Failed(IsEnabled(), $"开启开机启动失败：{ex.Message}");
        }
    }

    /// <summary>生成启动命令：用引号包裹路径，保证含空格的路径仍可执行。</summary>
    private static string BuildCommand(string executablePath) => $"\"{executablePath.Trim()}\"";

    /// <summary>
    /// 解析用于开机启动的可执行文件路径。
    /// 以 <c>dotnet run</c> 启动时进程路径是 dotnet.exe，此时改用输出目录中的同名 exe。
    /// </summary>
    public static string? ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            File.Exists(processPath) &&
            !Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return null;
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
