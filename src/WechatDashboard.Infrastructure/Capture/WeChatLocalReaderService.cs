using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

public sealed class WeChatLocalReaderService
{
    private readonly string _configPath;
    private readonly string _keysPath;
    private readonly string _readerToolDir;
    private readonly string _readerResultDir;
    private readonly string _defaultExternalKeyFilePath;
    private readonly string _defaultExternalKeyLogPath;
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

        _readerToolDir = ProjectToolPaths.WeChatLocalReaderToolDirectory;
        _readerResultDir = ProjectToolPaths.WeChatLocalReaderResultDirectory;
        _configPath = Path.Combine(_readerResultDir, "config.json");
        _keysPath = Path.Combine(_readerResultDir, "all_keys.json");
        _defaultExternalKeyFilePath = Path.Combine(_readerResultDir, "wx-key-found.txt");
        _defaultExternalKeyLogPath = Path.Combine(_readerResultDir, "wx-key-probe.log");

        // Prefer the newest Python script when running from source; it is
        // easier to update than a packaged exe and avoids stale local copies.
        _readerScriptPath = FindReaderScript(_readerToolDir);

        var exePath = Path.Combine(_readerToolDir, "wechat-local-reader.exe");
        var newExePath = Path.Combine(_readerToolDir, "wechat-local-reader-new.exe");
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

    public string DefaultExternalKeyFilePath => _defaultExternalKeyFilePath;

    public string DefaultExternalKeyLogPath => _defaultExternalKeyLogPath;

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
        ProjectToolPaths.WeChatLocalReaderResultDirectory,
        "config.json");

    public async Task<WeChatDatabaseKeyExtractionResult?> ExtractDatabaseKeyAsync(
        CancellationToken cancellationToken)
    {
        return await ExtractDatabaseKeyAsync(new WeChatDatabaseKeyExtractionOptions(), cancellationToken);
    }

    public async Task<WeChatDatabaseKeyExtractionResult?> ExtractDatabaseKeyAsync(
        WeChatDatabaseKeyExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var scriptPath = options.ScriptPath ?? FindWxKeyProbeScript();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            _lastError = $"未找到 wx_key PowerShell 探测脚本。请确认 {Path.Combine(ProjectToolPaths.WxKeyToolsDirectory, "run-wx-key-probe.ps1")} 存在。";
            return null;
        }

        var dllDirectory = options.DllDirectory ?? FindWxKeyDllDirectory();
        if (string.IsNullOrWhiteSpace(dllDirectory) || !Directory.Exists(dllDirectory))
        {
            _lastError = $"未找到 wx_key.dll 目录。请确认 {ProjectToolPaths.WxKeyToolsDirectory} 下的 wx_key-windows-v2.1.8 已解压。";
            return null;
        }

        var targetPid = options.TargetProcessId ?? FindWeixinTargetProcessId();
        if (targetPid is null)
        {
            _lastError = "未找到 Weixin 进程，请先登录并打开微信。";
            return null;
        }

        Directory.CreateDirectory(_readerResultDir);
        var keyPath = options.KeyPath ?? _defaultExternalKeyFilePath;
        var logPath = options.LogPath ?? _defaultExternalKeyLogPath;
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var powerShell = FindPowerShellExecutable();
        var result = await _commandRunner.RunAsync(
            powerShell,
            new[]
            {
                "-NoProfile",
                "-ExecutionPolicy", "Bypass",
                "-File", scriptPath,
                "-TargetPid", targetPid.Value.ToString(CultureInfo.InvariantCulture),
                "-DllDir", dllDirectory,
                "-KeyPath", keyPath,
                "-LogPath", logPath,
                "-Seconds", Math.Max(1, options.Seconds).ToString(CultureInfo.InvariantCulture)
            },
            Path.GetDirectoryName(scriptPath),
            new Dictionary<string, string>(),
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : result.StandardError.Trim();
            _lastError = $"DB Key 提取失败：{detail}";
            return null;
        }

        if (!File.Exists(keyPath) || !ContainsUsableDbKey(keyPath))
        {
            _lastError = $"DB Key 提取完成，但未生成有效 key 文件：{keyPath}";
            return null;
        }

        ExternalKeyFile = keyPath;
        _lastError = null;
        _isInitialized = null;
        return new WeChatDatabaseKeyExtractionResult(targetPid.Value, keyPath, logPath);
    }

    public async Task<WeChatLocalMessagePage?> ReadMessagesAsync(
        WeChatLocalMessageReadOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            _lastError = "微信本地读取器未安装。";
            return null;
        }

        var configPath = string.IsNullOrWhiteSpace(options.ConfigPath)
            ? _configPath
            : options.ConfigPath;
        if (!File.Exists(configPath))
        {
            _lastError = $"微信本地读取器尚未初始化，未找到配置文件：{configPath}";
            return null;
        }

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Clamp(options.PageSize, 1, 200);
        var localDate = DateTime.SpecifyKind(options.Date.Date, DateTimeKind.Unspecified);
        var dayStart = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
        var dayEnd = dayStart.AddDays(1);
        var offset = (pageNumber - 1) * pageSize;
        var (executable, arguments) = BuildReadMessagesCommand(
            configPath,
            dayStart.ToUnixTimeSeconds(),
            dayEnd.ToUnixTimeSeconds(),
            offset,
            pageSize);

        var temporaryDirectory = Path.Combine(ProjectToolPaths.ResultDirectory, "temp", "wechat-local-reader");
        Directory.CreateDirectory(temporaryDirectory);
        var environment = new Dictionary<string, string>
        {
            ["TEMP"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory,
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8:backslashreplace"
        };

        var result = await _commandRunner.RunAsync(
            executable,
            arguments,
            Path.GetDirectoryName(ReaderExecutablePath ?? executable),
            environment,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : result.StandardError.Trim();
            _lastError = $"读取微信消息失败：{detail}";
            return null;
        }

        try
        {
            var page = ParseMessagePage(result.StandardOutput, pageNumber, pageSize);
            _lastError = null;
            return page;
        }
        catch (JsonException ex)
        {
            _lastError = $"微信本地读取器返回了无效 JSON：{ex.Message}";
            return null;
        }
    }

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
            var environment = new Dictionary<string, string>
            {
                ["PYTHONUTF8"] = "1",
                ["PYTHONIOENCODING"] = "utf-8:backslashreplace"
            };
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

    private (string Executable, IReadOnlyList<string> Arguments) BuildReadMessagesCommand(
        string configPath,
        long startTimestamp,
        long endTimestamp,
        int offset,
        int limit)
    {
        var arguments = new List<string>();
        if (_readerScriptPath is not null)
        {
            arguments.Add(_readerScriptPath);
        }

        arguments.AddRange(new[]
        {
            "capture",
            "--config", configPath,
            "--format", "json",
            "--start-timestamp", startTimestamp.ToString(CultureInfo.InvariantCulture),
            "--end-timestamp", endTimestamp.ToString(CultureInfo.InvariantCulture),
            "--offset", offset.ToString(CultureInfo.InvariantCulture),
            "--limit", limit.ToString(CultureInfo.InvariantCulture)
        });

        if (_readerScriptPath is not null)
        {
            return (FindPythonExecutable(), arguments);
        }

        return (_readerExePath ?? "wechat-local-reader.exe", arguments);
    }

    private static string? FindReaderScript(string readerDir)
    {
        var candidates = new[]
        {
            Path.Combine(readerDir, "wechat_local_reader.py"),
            Path.Combine(AppContext.BaseDirectory, "wechat_local_reader.py"),
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

    private static WeChatLocalMessagePage ParseMessagePage(
        string json,
        int pageNumber,
        int pageSize)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var messages = ReadCapturedMessages(root);
        var totalCount = ReadInt(root, "totalMessages", "total") ?? messages.Count;
        return new WeChatLocalMessagePage(messages, totalCount, pageNumber, pageSize);
    }

    private static IReadOnlyList<CapturedMessage> ReadCapturedMessages(JsonElement root)
    {
        if (!TryGetProperty(root, out var messagesElement, "messages") ||
            messagesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CapturedMessage>();
        }

        var messages = new List<CapturedMessage>();
        foreach (var element in messagesElement.EnumerateArray())
        {
            var id = ReadString(element, "id", "msgId", "messageId", "localId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var chatId = ReadString(element, "chatId", "username", "talker") ?? "unknown-chat";
            var chatName = ReadString(element, "chatName", "chat", "roomName") ?? chatId;
            var senderName = ReadString(element, "senderName", "sender") ?? "未知发送人";
            var content = ReadString(element, "content", "message", "last_message") ?? "";
            var sentAt = ReadDateTimeOffset(element, "sentAt", "timestamp", "createTime") ?? DateTimeOffset.Now;
            var messageType = ParseMessageType(ReadString(element, "messageType", "msgType", "type"));

            messages.Add(new CapturedMessage(
                Source: "WeChat",
                SourceMessageKey: $"WeChat:local:{id}",
                ChatId: chatId,
                ChatName: chatName,
                SenderName: senderName,
                Content: content,
                SentAt: sentAt,
                MessageType: messageType));
        }

        return messages;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, out var value, name))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixTime))
            {
                return unixTime > 9_999_999_999
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unixTime)
                    : DateTimeOffset.FromUnixTimeSeconds(unixTime);
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, out var value, name) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, out var value, name))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static MessageType ParseMessageType(string? value)
    {
        if (Enum.TryParse<MessageType>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return value switch
        {
            "图片" => MessageType.Image,
            "链接/文件" or "文件" or "语音" or "视频" => MessageType.File,
            "链接" => MessageType.Link,
            "系统" or "撤回" => MessageType.System,
            _ => MessageType.Text
        };
    }

    private static string? FindWxKeyProbeScript()
    {
        var candidate = Path.Combine(ProjectToolPaths.WxKeyToolsDirectory, "run-wx-key-probe.ps1");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindWxKeyDllDirectory()
    {
        var candidate = Path.Combine(
            ProjectToolPaths.WxKeyToolsDirectory,
            "wx_key-windows-v2.1.8",
            "data",
            "flutter_assets",
            "assets",
            "dll");

        return Directory.Exists(candidate) ? candidate : null;
    }

    private static int? FindWeixinTargetProcessId()
    {
        var candidates = Process.GetProcessesByName("Weixin")
            .Select(process =>
            {
                using (process)
                {
                    return new WeixinProcessCandidate(
                        process.Id,
                        SafeMainWindowTitle(process),
                        SafeWorkingSet(process));
                }
            })
            .ToArray();

        return candidates
            .Where(candidate => string.Equals(candidate.MainWindowTitle, "微信", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.WorkingSet64)
            .Select(candidate => (int?)candidate.Id)
            .FirstOrDefault()
            ?? candidates
                .OrderByDescending(candidate => candidate.WorkingSet64)
                .Select(candidate => (int?)candidate.Id)
                .FirstOrDefault();
    }

    private static string SafeMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return "";
        }
    }

    private static long SafeWorkingSet(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    private static bool ContainsUsableDbKey(string keyPath)
    {
        var text = File.ReadAllText(keyPath);
        return Regex.IsMatch(
            text,
            @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
            RegexOptions.CultureInvariant);
    }

    private static string FindPowerShellExecutable()
    {
        foreach (var name in new[] { "pwsh", "powershell.exe", "powershell" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = "-NoProfile -Command \"$PSVersionTable.PSVersion.ToString()\"",
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

        return "powershell.exe";
    }

    private sealed record WeixinProcessCandidate(
        int Id,
        string MainWindowTitle,
        long WorkingSet64);
}

public sealed record WeChatLocalMessageReadOptions(
    DateTime Date,
    int PageNumber = 1,
    int PageSize = 50,
    string? ConfigPath = null);

public sealed record WeChatLocalMessagePage(
    IReadOnlyList<CapturedMessage> Messages,
    int TotalCount,
    int PageNumber,
    int PageSize);
public sealed record WeChatDatabaseKeyExtractionOptions(
    string? ScriptPath = null,
    string? DllDirectory = null,
    string? KeyPath = null,
    string? LogPath = null,
    int Seconds = 300,
    int? TargetProcessId = null);

public sealed record WeChatDatabaseKeyExtractionResult(
    int TargetProcessId,
    string KeyPath,
    string LogPath);
