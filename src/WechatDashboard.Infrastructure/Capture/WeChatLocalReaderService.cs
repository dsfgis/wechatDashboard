using System.Diagnostics;
using System.IO;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 微信本地数据库读取服务。生产路径只编排原生 C# reader；唯一允许启动的子进程是
/// 隔离的 WechatDashboard.KeyProbe，用于调用 wx_key.dll 并通过受限命名管道交付密钥。
/// </summary>
public sealed class WeChatLocalReaderService : IAsyncDisposable
{
    private readonly IWeChatLocalDatabaseReader? _reader;
    private readonly Func<string?> _databaseRootLocator;
    private readonly Func<int?> _processIdLocator;
    private readonly SemaphoreSlim _protectedRestoreGate = new(1, 1);
    private string _bootstrapRange = "30d";
    private string? _lastError;
    private bool _protectedRestoreAttempted;

    public WeChatLocalReaderService(
        IWeChatLocalDatabaseReader? reader = null,
        Func<string?>? databaseRootLocator = null,
        Func<int?>? processIdLocator = null)
    {
        _reader = reader ?? CreateDefaultNativeReader();
        _databaseRootLocator = databaseRootLocator ?? WeChatDataDirectoryLocator.Locate;
        _processIdLocator = processIdLocator ?? FindWeixinTargetProcessId;
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

    public bool IsAvailable => _reader?.IsAvailable == true;

    public bool IsInitialized => _reader?.IsInitialized == true;

    public string? LastError => _lastError;

    public IReadOnlyList<WeChatReaderStageResult> Diagnostics =>
        _reader?.Diagnostics ?? Array.Empty<WeChatReaderStageResult>();

    public IWeChatLocalDatabaseReader? NativeReader => _reader;

    /// <summary>兼容设置页展示；该文件只保存不含密钥的 reader 状态。</summary>
    public static string DefaultConfigPath => Path.Combine(
        ProjectToolPaths.WeChatNativeReaderDirectory,
        "state.json");

    public Task<WeChatDatabaseKeyExtractionResult?> ExtractDatabaseKeyAsync(
        CancellationToken cancellationToken) =>
        ExtractDatabaseKeyAsync(new WeChatDatabaseKeyExtractionOptions(), cancellationToken);

    /// <summary>
    /// 执行受控 KeyProbe，并立即用密钥验证数据库。密钥只存在于租约内存中；
    /// 成功结果不会包含 Key 文件或日志文件路径。
    /// </summary>
    public async Task<WeChatDatabaseKeyExtractionResult?> ExtractDatabaseKeyAsync(
        WeChatDatabaseKeyExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var targetProcessId = options.TargetProcessId ?? _processIdLocator();
        if (targetProcessId is null)
        {
            _lastError = "未找到 Weixin 进程，请先登录并打开微信。";
            return null;
        }

        var succeeded = await InitializeCoreAsync(
            targetProcessId.Value,
            TimeSpan.FromSeconds(Math.Clamp(options.Seconds, 1, 600)),
            cancellationToken);
        return succeeded
            ? new WeChatDatabaseKeyExtractionResult(targetProcessId.Value, "native-csharp")
            : null;
    }

    /// <summary>从 Windows DPAPI CurrentUser 密文缓存恢复并验证 Key；不会启动 KeyProbe。</summary>
    public async Task<bool> TryRestoreProtectedKeyAsync(CancellationToken cancellationToken)
    {
        await _protectedRestoreGate.WaitAsync(cancellationToken);
        try
        {
            if (IsInitialized)
            {
                _lastError = null;
                return true;
            }

            if (_protectedRestoreAttempted)
            {
                _lastError ??= "微信本地数据库尚未初始化，请点击“自动提取 Key”。";
                return false;
            }

            _protectedRestoreAttempted = true;

            if (_reader is null || !_reader.IsAvailable)
            {
                _lastError = "原生 C# 微信读取器不可用，请检查 KeyProbe 和 wx_key.dll。";
                return false;
            }

            var databaseRoot = _databaseRootLocator();
            if (string.IsNullOrWhiteSpace(databaseRoot))
            {
                _lastError = "未找到微信 db_storage 数据目录，请确认微信已登录。";
                return false;
            }

            var result = await _reader.RestoreProtectedKeyAsync(
                new WeChatProtectedKeyRestoreRequest(databaseRoot, _bootstrapRange),
                cancellationToken);
            if (!result.Succeeded)
            {
                _lastError = FormatFailedStage(result.Stages);
                return false;
            }

            _lastError = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _lastError = FormatError(
                exception,
                _reader?.Diagnostics ?? Array.Empty<WeChatReaderStageResult>());
            return false;
        }
        finally
        {
            _protectedRestoreGate.Release();
        }
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsInitialized)
        {
            _lastError = null;
            return true;
        }

        var targetProcessId = _processIdLocator();
        if (targetProcessId is null)
        {
            _lastError = "未找到 Weixin 进程，请先登录并打开微信。";
            return false;
        }

        return await InitializeCoreAsync(
            targetProcessId.Value,
            TimeSpan.FromMinutes(5),
            cancellationToken);
    }

    public async Task<WeChatLocalMessagePage?> ReadMessagesAsync(
        WeChatLocalMessageReadOptions options,
        CancellationToken cancellationToken)
    {
        if (_reader is null || !_reader.IsAvailable)
        {
            _lastError = "原生 C# 微信读取器不可用，请检查 KeyProbe 和 wx_key.dll。";
            return null;
        }
        if (!_reader.IsInitialized)
        {
            _lastError = "微信本地数据库尚未初始化。";
            return null;
        }

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Clamp(options.PageSize, 1, 200);
        var localDate = DateTime.SpecifyKind(options.Date.Date, DateTimeKind.Unspecified);
        var dayStart = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
        var nextDate = localDate.AddDays(1);
        var dayEnd = new DateTimeOffset(nextDate, TimeZoneInfo.Local.GetUtcOffset(nextDate));

        try
        {
            var result = await _reader.ReadAsync(
                new WeChatReadRequest(
                    dayStart,
                    dayEnd,
                    checked((pageNumber - 1) * pageSize),
                    pageSize),
                cancellationToken);
            _lastError = null;
            return new WeChatLocalMessagePage(
                result.Messages,
                result.TotalCount,
                pageNumber,
                pageSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _lastError = FormatError(exception, _reader.Diagnostics);
            return null;
        }
    }

    /// <summary>清除上一条 UI 错误；不会销毁仍在使用的内存密钥会话。</summary>
    public void ResetInitializationState() => _lastError = null;

    /// <summary>显式重置 reader：清除内存会话和 DPAPI Key 缓存，但不删除消息数据。</summary>
    public async Task ClearProtectedKeyAsync(CancellationToken cancellationToken)
    {
        await _protectedRestoreGate.WaitAsync(cancellationToken);
        try
        {
            if (_reader is not null)
            {
                await _reader.ClearProtectedKeyAsync(cancellationToken);
            }
            _protectedRestoreAttempted = false;
            _lastError = "微信 Reader 已重置，请点击“自动提取 Key”重新初始化。";
        }
        finally
        {
            _protectedRestoreGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_reader is not null)
        {
            await _reader.DisposeAsync();
        }
    }

    private async Task<bool> InitializeCoreAsync(
        int processId,
        TimeSpan keyTimeout,
        CancellationToken cancellationToken)
    {
        if (_reader is null || !_reader.IsAvailable)
        {
            _lastError = "原生 C# 微信读取器不可用，请检查 KeyProbe 和 wx_key.dll。";
            return false;
        }

        var databaseRoot = _databaseRootLocator();
        if (string.IsNullOrWhiteSpace(databaseRoot))
        {
            _lastError = "未找到微信 db_storage 数据目录，请确认微信已登录。";
            return false;
        }

        try
        {
            var result = await _reader.InitializeAsync(
                new WeChatInitializationRequest(
                    databaseRoot,
                    processId,
                    keyTimeout,
                    _bootstrapRange),
                cancellationToken);
            if (!result.Succeeded)
            {
                _lastError = FormatFailedStage(result.Stages);
                return false;
            }

            _lastError = null;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _lastError = FormatError(exception, _reader.Diagnostics);
            return false;
        }
    }

    private static string FormatFailedStage(IReadOnlyList<WeChatReaderStageResult> stages)
    {
        var failed = stages.LastOrDefault(stage => !stage.Succeeded);
        return failed is null
            ? "原生 C# 微信读取器初始化失败。"
            : $"{failed.Stage}/{failed.ErrorCode}：{WeChatReaderDiagnosticSanitizer.Sanitize(failed.SanitizedMessage)}";
    }

    private static string FormatError(Exception exception, IReadOnlyList<WeChatReaderStageResult> stages)
    {
        if (exception is WeChatReaderException readerException)
        {
            return $"{readerException.Stage}/{readerException.ErrorCode}：{WeChatReaderDiagnosticSanitizer.Sanitize(readerException.Message)}";
        }

        var failed = stages.LastOrDefault(stage => !stage.Succeeded);
        return failed is not null
            ? $"{failed.Stage}/{failed.ErrorCode}：{WeChatReaderDiagnosticSanitizer.Sanitize(failed.SanitizedMessage)}"
            : WeChatReaderDiagnosticSanitizer.Sanitize(exception.Message);
    }

    private static IWeChatLocalDatabaseReader? CreateDefaultNativeReader()
    {
        var keyProbePath = FindKeyProbeExecutable();
        var dllPath = FindWxKeyDllPath();
        return keyProbePath is null || dllPath is null
            ? null
            : new NativeWeChatLocalDatabaseReader(
                new NativeHookKeyProvider(keyProbePath, dllPath),
                keyStore: new ProtectedWeChatDatabaseKeyStore());
    }

    private static string? FindKeyProbeExecutable()
    {
        var directCandidates = new[]
        {
            Path.Combine(ProjectToolPaths.InstallRoot, "WechatDashboard.KeyProbe.exe"),
            Path.Combine(ProjectToolPaths.WxKeyToolsDirectory, "WechatDashboard.KeyProbe.exe"),
            Path.Combine(ProjectToolPaths.WxKeyToolsDirectory, "key-probe", "WechatDashboard.KeyProbe.exe")
        };
        var direct = directCandidates.FirstOrDefault(File.Exists);
        if (direct is not null)
        {
            return Path.GetFullPath(direct);
        }

        var buildRoot = Path.Combine(
            ProjectToolPaths.ProjectRoot,
            "src",
            "WechatDashboard.KeyProbe",
            "bin");
        return !Directory.Exists(buildRoot)
            ? null
            : Directory.EnumerateFiles(buildRoot, "WechatDashboard.KeyProbe.exe", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;
    }

    private static string? FindWxKeyDllPath()
    {
        var candidates = new[]
        {
            Path.Combine(ProjectToolPaths.InstallRoot, "wx_key.dll"),
            Path.Combine(ProjectToolPaths.WxKeyToolsDirectory, "wx-key", "wx_key.dll"),
            Path.Combine(
                ProjectToolPaths.WxKeyToolsDirectory,
                "wx_key-windows-v2.1.8",
                "data",
                "flutter_assets",
                "assets",
                "dll",
                "wx_key.dll")
        };
        return candidates.FirstOrDefault(File.Exists) is { } path ? Path.GetFullPath(path) : null;
    }

    private static int? FindWeixinTargetProcessId()
    {
        var candidates = Process.GetProcessesByName("Weixin")
            .Select(process =>
            {
                using (process)
                {
                    string title;
                    long workingSet;
                    try { title = process.MainWindowTitle; } catch { title = ""; }
                    try { workingSet = process.WorkingSet64; } catch { workingSet = 0; }
                    return new WeixinProcessCandidate(process.Id, title, workingSet);
                }
            })
            .ToArray();

        return candidates
            .OrderByDescending(candidate => string.Equals(candidate.MainWindowTitle, "微信", StringComparison.Ordinal))
            .ThenByDescending(candidate => candidate.WorkingSet64)
            .Select(candidate => (int?)candidate.Id)
            .FirstOrDefault();
    }

    private sealed record WeixinProcessCandidate(int Id, string MainWindowTitle, long WorkingSet64);
}

public sealed record WeChatLocalMessageReadOptions(
    DateTime Date,
    int PageNumber = 1,
    int PageSize = 50);

public sealed record WeChatLocalMessagePage(
    IReadOnlyList<CapturedMessage> Messages,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed record WeChatDatabaseKeyExtractionOptions(
    int Seconds = 300,
    int? TargetProcessId = null);

public sealed record WeChatDatabaseKeyExtractionResult(
    int TargetProcessId,
    string Provider);
