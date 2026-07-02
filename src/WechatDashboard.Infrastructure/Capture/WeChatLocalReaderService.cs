using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using WechatDashboard.Application.Capture;
using WechatDashboard.Domain.Enums;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 微信本地数据库读取器服务：封装 wechat-local-reader 工具的调用。
/// 职责包括：定位 Python 脚本/exe、初始化本地库（提取 DB Key 并解密）、
/// 读取当天微信消息、管理外部 Key 命令/文件、维护初始化状态与最近错误。
/// 上层通过本服务间接调用底层 Python 脚本，避免直接处理进程启动与 JSON 解析。
/// </summary>
public sealed class WeChatLocalReaderService
{
    // 配置文件路径（config.json），由 init 子命令生成
    private readonly string _configPath;
    // 密钥文件路径（all_keys.json），由 init 子命令生成
    private readonly string _keysPath;
    // wechat-local-reader 工具所在目录
    private readonly string _readerToolDir;
    // 读取器结果输出目录（tools/result 下）
    private readonly string _readerResultDir;
    // 默认外部 Key 输出文件路径（wx-key-found.txt）
    private readonly string _defaultExternalKeyFilePath;
    // 默认外部 Key 探测日志路径（wx-key-probe.log）
    private readonly string _defaultExternalKeyLogPath;
    // Python 脚本路径（优先使用，便于更新）
    private readonly string? _readerScriptPath;
    // 打包 exe 路径（脚本不可用时回退使用）
    private readonly string? _readerExePath;
    // 外部命令执行器（用于启动 Python/PowerShell 进程）
    private readonly IExternalCommandRunner _commandRunner;
    // 引导历史范围：7d/30d/all，控制初始化时回溯多少天的消息
    private string _bootstrapRange = "30d";
    // 用户导入的 DB Key（可选）
    private string? _importedDatabaseKey;
    // 外部 Key 提取命令（可选，注入到子进程环境变量）
    private string? _externalKeyCommand;
    // 外部 Key 文件路径（可选，由外部工具写入）
    private string? _externalKeyFile;

    // 初始化状态缓存：null=未检测，true=已初始化，false=未初始化
    private bool? _isInitialized;
    // 最近一次错误信息（供 UI 展示）
    private string? _lastError;

    /// <summary>
    /// 构造函数：定位工具目录、结果目录、配置/密钥路径，
    /// 优先查找 Python 脚本（便于热更新），找不到时回退到打包 exe。
    /// </summary>
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

    /// <summary>配置文件路径（config.json）。</summary>
    public string ConfigPath => _configPath;

    /// <summary>密钥文件路径（all_keys.json）。</summary>
    public string KeysPath => _keysPath;

    /// <summary>默认外部 Key 输出文件路径。</summary>
    public string DefaultExternalKeyFilePath => _defaultExternalKeyFilePath;

    /// <summary>默认外部 Key 探测日志路径。</summary>
    public string DefaultExternalKeyLogPath => _defaultExternalKeyLogPath;

    /// <summary>用户导入的 DB Key（trim 后存储，空值转为 null）。</summary>
    public string? ImportedDatabaseKey
    {
        get => _importedDatabaseKey;
        set
        {
            var trimmed = value?.Trim();
            _importedDatabaseKey = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    /// <summary>外部 Key 提取命令（注入子进程环境变量，供脚本调用）。</summary>
    public string? ExternalKeyCommand
    {
        get => _externalKeyCommand;
        set
        {
            var trimmed = value?.Trim();
            _externalKeyCommand = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    /// <summary>外部 Key 文件路径（由外部工具写入，脚本读取）。</summary>
    public string? ExternalKeyFile
    {
        get => _externalKeyFile;
        set
        {
            var trimmed = value?.Trim();
            _externalKeyFile = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
    }

    /// <summary>引导历史范围：仅接受 7d/30d/all，非法值回退为 30d。</summary>
    public string BootstrapRange
    {
        get => _bootstrapRange;
        set
        {
            var normalized = value?.Trim().ToLowerInvariant() ?? "30d";
            _bootstrapRange = normalized is "7d" or "30d" or "all" ? normalized : "30d";
        }
    }

    /// <summary>读取器可执行路径：优先返回 Python 脚本路径，其次返回 exe 路径。</summary>
    public string? ReaderExecutablePath => _readerScriptPath ?? _readerExePath;

    /// <summary>读取器是否可用：脚本或 exe 至少存在一个即视为可用。</summary>
    public bool IsAvailable => _readerExePath is not null || _readerScriptPath is not null;

    /// <summary>是否已初始化：首次访问时检测配置/密钥文件是否存在并缓存结果。</summary>
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

    /// <summary>最近一次错误信息（供 UI 展示）。</summary>
    public string? LastError => _lastError;

    /// <summary>默认配置文件路径（静态访问）。</summary>
    public static string DefaultConfigPath => Path.Combine(
        ProjectToolPaths.WeChatLocalReaderResultDirectory,
        "config.json");

    /// <summary>提取微信 DB Key（使用默认选项）。</summary>
    public async Task<WeChatDatabaseKeyExtractionResult?> ExtractDatabaseKeyAsync(
        CancellationToken cancellationToken)
    {
        return await ExtractDatabaseKeyAsync(new WeChatDatabaseKeyExtractionOptions(), cancellationToken);
    }

    /// <summary>
    /// 提取微信 DB Key：调用 wx_key PowerShell 探测脚本从微信进程内存扫描密钥。
    /// 前置条件：探测脚本存在、wx_key.dll 已解压、微信进程正在运行。
    /// 成功后更新 ExternalKeyFile，失败时填充 _lastError 并返回 null。
    /// </summary>
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

    /// <summary>
    /// 读取微信消息分页：根据指定日期构建当天时间窗口，调用 capture 子命令采集，
    /// 解析 JSON 输出为 CapturedMessage 列表与总数。失败时填充 _lastError 并返回 null。
    /// </summary>
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

    /// <summary>
    /// 初始化微信本地数据库读取器：定位微信数据目录，调用 init 子命令提取密钥并解密所有数据库。
    /// 支持外部 Key 注入（环境变量）、8 分钟超时、管理员权限错误识别。成功后更新初始化状态。
    /// </summary>
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

    /// <summary>重置初始化状态缓存，强制下次访问 IsInitialized 时重新检测。</summary>
    public void ResetInitializationState()
    {
        _isInitialized = null;
        _lastError = null;
    }

    /// <summary>检测是否已初始化：配置文件与密钥文件同时存在即视为就绪。</summary>
    private bool CheckIfInitialized()
    {
        return File.Exists(_configPath) && File.Exists(_keysPath);
    }

    /// <summary>构造 init 子命令：根据是否使用 Python 脚本选择可执行文件与参数。</summary>
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

    /// <summary>构造 capture 子命令：指定配置、时间窗口、offset 与 limit 参数。</summary>
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

    /// <summary>
    /// 查找 wechat_local_reader.py 脚本：搜索工具目录、应用基目录、当前工作目录，
    /// 并向上递归查找父目录。多个候选时按最后修改时间取最新。
    /// </summary>
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

    /// <summary>从起始目录向上递归，生成每级目录下 tools/wechat-local-reader/wechat_local_reader.py 的候选路径。</summary>
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

    /// <summary>查找可用 Python 解释器：依次尝试 python/python3/py，验证 --version 可执行。</summary>
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

    /// <summary>解析 capture 子命令输出的 JSON 为消息分页对象。</summary>
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

    /// <summary>从 JSON 根对象读取消息数组并映射为 CapturedMessage 列表，兼容多种字段别名。</summary>
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

    /// <summary>从 JSON 元素读取时间戳：兼容字符串与数字（Unix 秒/毫秒）两种形式。</summary>
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

    /// <summary>从 JSON 元素读取整数值，按候选字段名依次尝试。</summary>
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

    /// <summary>从 JSON 元素读取字符串，兼容字符串与数字两种形式，自动 trim。</summary>
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

    /// <summary>按候选字段名（忽略大小写）查找 JSON 属性，找到则输出值并返回 true。</summary>
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

    /// <summary>将消息类型字符串映射为 MessageType 枚举，兼容中文与枚举名。</summary>
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

    /// <summary>定位 wx_key 探测脚本（run-wx-key-probe.ps1）。</summary>
    private static string? FindWxKeyProbeScript()
    {
        var candidate = Path.Combine(ProjectToolPaths.WxKeyToolsDirectory, "run-wx-key-probe.ps1");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>定位 wx_key.dll 目录（wx_key-windows-v2.1.8 解压后的 dll 路径）。</summary>
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

    /// <summary>
    /// 查找微信目标进程：优先选择主窗口标题为"微信"的进程，其次按内存占用降序选取。
    /// </summary>
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

    /// <summary>安全读取进程主窗口标题，异常时返回空字符串。</summary>
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

    /// <summary>安全读取进程工作集内存大小，异常时返回 0。</summary>
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

    /// <summary>检查 Key 文件是否包含有效的 64 位十六进制 DB Key。</summary>
    private static bool ContainsUsableDbKey(string keyPath)
    {
        var text = File.ReadAllText(keyPath);
        return Regex.IsMatch(
            text,
            @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
            RegexOptions.CultureInvariant);
    }

    /// <summary>查找可用 PowerShell：依次尝试 pwsh/powershell.exe/powershell，验证可执行。</summary>
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

    /// <summary>微信进程候选项：用于排序选取目标进程。</summary>
    private sealed record WeixinProcessCandidate(
        int Id,
        string MainWindowTitle,
        long WorkingSet64);
}

/// <summary>读取微信消息的查询选项：日期、页码、每页条数、可选配置路径。</summary>
public sealed record WeChatLocalMessageReadOptions(
    DateTime Date,
    int PageNumber = 1,
    int PageSize = 50,
    string? ConfigPath = null);

/// <summary>微信消息分页结果：消息列表、总条数、当前页码、每页条数。</summary>
public sealed record WeChatLocalMessagePage(
    IReadOnlyList<CapturedMessage> Messages,
    int TotalCount,
    int PageNumber,
    int PageSize);

/// <summary>DB Key 提取选项：脚本路径、DLL 目录、输出路径、超时秒数、目标进程 ID。</summary>
public sealed record WeChatDatabaseKeyExtractionOptions(
    string? ScriptPath = null,
    string? DllDirectory = null,
    string? KeyPath = null,
    string? LogPath = null,
    int Seconds = 300,
    int? TargetProcessId = null);

/// <summary>DB Key 提取结果：目标进程 ID、Key 文件路径、日志路径。</summary>
public sealed record WeChatDatabaseKeyExtractionResult(
    int TargetProcessId,
    string KeyPath,
    string LogPath);
