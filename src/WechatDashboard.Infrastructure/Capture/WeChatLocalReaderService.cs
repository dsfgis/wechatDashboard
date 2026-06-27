using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WeChatLocalReaderService
{
    private readonly string _configPath;
    private readonly string _keysPath;
    private readonly string? _readerScriptPath;
    private readonly string? _readerExePath;
    private readonly IExternalCommandRunner _commandRunner;
    private string _bootstrapRange = "30d";
    private string? _importedDatabaseKey;
    private string? _externalKeyCommand;
    private string? _externalKeyFile;

    private bool? _isInitialized;
    private string? _lastError;

    public WeChatLocalReaderService(IExternalCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new ProcessExternalCommandRunner();

        var readerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WechatDashboard",
            "tools",
            "wechat-local-reader");

        _configPath = Path.Combine(readerDir, "config.json");
        _keysPath = Path.Combine(readerDir, "all_keys.json");

        // Prefer the newest Python script when running from source; it is
        // easier to update than a packaged exe and avoids stale local copies.
        _readerScriptPath = FindReaderScript(readerDir);

        var exePath = Path.Combine(readerDir, "wechat-local-reader.exe");
        var newExePath = Path.Combine(readerDir, "wechat-local-reader-new.exe");
        _readerExePath = null;
        if (_readerScriptPath is null && File.Exists(newExePath))
        {
            _readerExePath = newExePath;
        }
        else if (_readerScriptPath is null && File.Exists(exePath))
        {
            _readerExePath = exePath;
        }
    }

    public string ConfigPath => _configPath;

    public string KeysPath => _keysPath;

    public string? ImportedDatabaseKey
    {
        get => _importedDatabaseKey;
        set
        {
            var trimmed = value?.Trim();
            _importedDatabaseKey = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    public string? ExternalKeyCommand
    {
        get => _externalKeyCommand;
        set
        {
            var trimmed = value?.Trim();
            _externalKeyCommand = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    public string? ExternalKeyFile
    {
        get => _externalKeyFile;
        set
        {
            var trimmed = value?.Trim();
            _externalKeyFile = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    public string BootstrapRange
    {
        get => _bootstrapRange;
        set
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? "30d";
            _bootstrapRange = normalized is "7d" or "30d" or "all" ? normalized : "30d";
        }
    }

    public string? ReaderExecutablePath => _readerScriptPath ?? _readerExePath;

    public bool IsAvailable => _readerExePath is not null || _readerScriptPath is not null;

    public bool IsInitialized
    {
        get
        {
            if (_isInitialized.HasValue)
            {
                return _isInitialized.Value;
            }

            _isInitialized = CheckIfInitialized();
            return _isInitialized.Value;
        }
    }

    public string? LastError => _lastError;

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WechatDashboard",
        "tools",
        "wechat-local-reader",
        "config.json");

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsInitialized)
        {
            return true;
        }

        if (!IsAvailable)
        {
            _lastError = "WeChat local reader is not installed. Run the setup first.";
            return false;
        }

        var dbDir = WeChatDataDirectoryLocator.Locate();
        if (dbDir is null)
        {
            _lastError = "Could not find WeChat data directory (db_storage). Ensure WeChat is running.";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);

        var (executable, args) = BuildInitCommand(dbDir, _configPath);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var environment = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(_importedDatabaseKey))
            {
                environment["WECHAT_DASHBOARD_DB_KEY"] = _importedDatabaseKey!;
            }
            if (!string.IsNullOrWhiteSpace(_externalKeyCommand))
            {
                environment["WECHAT_DASHBOARD_KEY_COMMAND"] = _externalKeyCommand!;
            }
            if (!string.IsNullOrWhiteSpace(_externalKeyFile))
            {
                environment["WECHAT_DASHBOARD_KEY_FILE"] = _externalKeyFile!;
            }

            var result = await _commandRunner.RunAsync(
                executable,
                args,
                Path.GetDirectoryName(ReaderExecutablePath ?? executable),
                environment,
                linkedCts.Token);

            if (result.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"exit code {result.ExitCode}"
                    : result.StandardError.Trim();

                if (detail.Contains("Access is denied") || detail.Contains("拒绝访问") || detail.Contains("0x5"))
                {
                    _lastError = "需要管理员权限才能读取微信进程内存。请以管理员身份运行本程序后重试。";
                    return false;
                }

                _lastError = $"WeChat reader init failed: {detail}";
                _isInitialized = false;
                return false;
            }

            _isInitialized = CheckIfInitialized();
            if (!_isInitialized.Value)
            {
                _lastError = "Init completed but config.json was not created. Key extraction may have failed.";
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _lastError = "初始化超时（8分钟）。请确认微信正在运行，并以管理员身份运行本程序。";
            _isInitialized = false;
            return false;
        }
        catch (Exception ex)
        {
            _lastError = $"Failed to run reader init: {ex.Message}";
            _isInitialized = false;
            return false;
        }
    }

    public void ResetInitializationState()
    {
        _isInitialized = null;
        _lastError = null;
    }

    private bool CheckIfInitialized()
    {
        return File.Exists(_configPath) && File.Exists(_keysPath);
    }

    private (string Executable, IReadOnlyList<string> Arguments) BuildInitCommand(
        string dbDir,
        string configPath)
    {
        if (_readerScriptPath is not null)
        {
            var pythonExe = FindPythonExecutable();
            return (
                pythonExe,
                new[]
                {
                    _readerScriptPath,
                    "init",
                    "--db-dir", dbDir,
                    "--config", configPath,
                    "--bootstrap-range", _bootstrapRange
                });
        }

        var exe = _readerExePath ?? "wechat-local-reader.exe";
        return (
            exe,
            new[]
            {
                "init",
                "--db-dir", dbDir,
                "--config", configPath,
                "--bootstrap-range", _bootstrapRange
            });
    }

    private static string? FindReaderScript(string readerDir)
    {
        var candidates = new[]
        {
            Path.Combine(readerDir, "wechat_local_reader.py"),
            Path.Combine(AppContext.BaseDirectory, "wechat_local_reader.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "wechat-local-reader", "wechat_local_reader.py"),
            Path.Combine(Environment.CurrentDirectory, "tools", "wechat-local-reader", "wechat_local_reader.py")
        }
            .Concat(FindReaderScriptUpwards(AppContext.BaseDirectory))
            .Concat(FindReaderScriptUpwards(Environment.CurrentDirectory))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        return candidates.FirstOrDefault()?.FullName;
    }

    private static IEnumerable<string> FindReaderScriptUpwards(string startDirectory)
    {
        DirectoryInfo? directory;
        try
        {
            directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        }
        catch
        {
            yield break;
        }

        while (directory is not null)
        {
            yield return Path.Combine(
                directory.FullName,
                "tools",
                "wechat-local-reader",
                "wechat_local_reader.py");
            directory = directory.Parent;
        }
    }

    private static string FindPythonExecutable()
    {
        var pythonNames = new[] { "python", "python3", "py" };

        foreach (var name in pythonNames)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                if (process is not null)
                {
                    process.WaitForExit(3000);
                    if (process.ExitCode == 0)
                    {
                        return name;
                    }
                }
            }
            catch
            {
                // Try next
            }
        }

        return "python";
    }
}
