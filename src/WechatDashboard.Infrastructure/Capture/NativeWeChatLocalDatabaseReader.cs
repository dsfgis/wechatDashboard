using System.Diagnostics;
using System.IO;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>完全由 C# 编排的微信本地数据库读取器；仅 KeyProbe 作为隔离原生进程。</summary>
public sealed class NativeWeChatLocalDatabaseReader : IWeChatLocalDatabaseReader
{
    private const string ReaderVersion = "1";
    private readonly IWeChatDatabaseKeyProvider _keyProvider;
    private readonly WeChatDatabaseDiscovery _discovery;
    private readonly WeChatDatabaseSnapshotService _snapshotService;
    private readonly WeChatDatabaseKeyDeriver _keyDeriver;
    private readonly WeChatV4MessageQueryService _queryService;
    private readonly WeChatReaderStateStore _stateStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<WeChatReaderStageResult> _diagnostics = Array.Empty<WeChatReaderStageResult>();
    private WeChatDatabaseKeyLease? _masterKeyLease;
    private WeChatDatabaseKeySet? _derivedKeys;
    private string? _databaseRoot;
    private bool _disposed;

    public NativeWeChatLocalDatabaseReader(
        IWeChatDatabaseKeyProvider keyProvider,
        WeChatDatabaseDiscovery? discovery = null,
        WeChatDatabaseSnapshotService? snapshotService = null,
        WeChatDatabaseKeyDeriver? keyDeriver = null,
        WeChatV4MessageQueryService? queryService = null,
        WeChatReaderStateStore? stateStore = null)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _discovery = discovery ?? new WeChatDatabaseDiscovery();
        _snapshotService = snapshotService ?? new WeChatDatabaseSnapshotService();
        _keyDeriver = keyDeriver ?? new WeChatDatabaseKeyDeriver();
        _queryService = queryService ?? new WeChatV4MessageQueryService();
        _stateStore = stateStore ?? new WeChatReaderStateStore();
    }

    public bool IsAvailable => !_disposed;

    public bool IsInitialized => !_disposed && _masterKeyLease is not null && _derivedKeys is not null;

    public IReadOnlyList<WeChatReaderStageResult> Diagnostics => _diagnostics;

    public async Task<WeChatInitializationResult> InitializeAsync(
        WeChatInitializationRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        var stages = new List<WeChatReaderStageResult>();
        WeChatDatabaseKeyLease? newMasterLease = null;
        WeChatDatabaseKeySet? newDerivedKeys = null;
        WeChatDatabaseSnapshot? snapshot = null;
        try
        {
            DisposeSession();
            var normalizedRoot = await RunStageAsync(
                stages,
                WeChatReaderStage.PathDiscovery,
                () => Task.FromResult(WeChatDatabaseDiscovery.NormalizeRoot(request.DatabaseRoot)),
                cancellationToken);
            var databases = await RunStageAsync(
                stages,
                WeChatReaderStage.PathDiscovery,
                () => Task.FromResult(_discovery.Discover(normalizedRoot)),
                cancellationToken,
                result => new Dictionary<string, long> { ["databases"] = result.Count });
            newMasterLease = await RunStageAsync(
                stages,
                WeChatReaderStage.KeyProbe,
                () => _keyProvider.AcquireAsync(
                    new WeChatDatabaseKeyRequest(request.ProcessId, request.KeyTimeout),
                    cancellationToken),
                cancellationToken);
            snapshot = await RunStageAsync(
                stages,
                WeChatReaderStage.Snapshot,
                () => _snapshotService.CreateAsync(databases, cancellationToken),
                cancellationToken,
                result => new Dictionary<string, long> { ["databases"] = result.Databases.Count });
            newDerivedKeys = await RunStageAsync(
                stages,
                WeChatReaderStage.KeyValidation,
                () => _keyDeriver.DeriveAsync(newMasterLease, snapshot.Databases, cancellationToken),
                cancellationToken,
                result => new Dictionary<string, long> { ["validated_databases"] = result.RelativePaths.Count });

            var schemaProbeStart = DateTimeOffset.UtcNow;
            await RunStageAsync(
                stages,
                WeChatReaderStage.Schema,
                () => Task.FromResult(_queryService.Read(
                    snapshot,
                    newMasterLease,
                    new WeChatReadRequest(schemaProbeStart, schemaProbeStart, 0, 1),
                    cancellationToken)),
                cancellationToken,
                result => result.Counts);

            _masterKeyLease = newMasterLease;
            _derivedKeys = newDerivedKeys;
            _databaseRoot = normalizedRoot;
            newMasterLease = null;
            newDerivedKeys = null;
            await _stateStore.SaveAsync(
                new WeChatReaderState(
                    ReaderVersion,
                    normalizedRoot,
                    NormalizeBootstrapRange(request.BootstrapRange),
                    SchemaFingerprint: null,
                    LastValidatedAt: DateTimeOffset.UtcNow),
                cancellationToken);
            _diagnostics = stages;
            return new WeChatInitializationResult(true, stages);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stages.Add(CreateFailureStage(exception));
            _diagnostics = stages;
            return new WeChatInitializationResult(false, stages);
        }
        finally
        {
            if (snapshot is not null)
            {
                _snapshotService.Delete(snapshot);
            }
            newDerivedKeys?.Dispose();
            newMasterLease?.Dispose();
            _gate.Release();
        }
    }

    public async Task<WeChatReadResult> ReadAsync(
        WeChatReadRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        var stages = new List<WeChatReaderStageResult>();
        WeChatDatabaseSnapshot? snapshot = null;
        try
        {
            if (_masterKeyLease is null || _derivedKeys is null || string.IsNullOrWhiteSpace(_databaseRoot))
            {
                throw new WeChatReaderException(
                    WeChatReaderStage.KeyValidation,
                    WeChatReaderErrorCode.KeyInvalid,
                    "The native WeChat reader has not been initialized.");
            }

            var databases = await RunStageAsync(
                stages,
                WeChatReaderStage.PathDiscovery,
                () => Task.FromResult(_discovery.Discover(_databaseRoot)),
                cancellationToken,
                result => new Dictionary<string, long> { ["databases"] = result.Count });
            snapshot = await RunStageAsync(
                stages,
                WeChatReaderStage.Snapshot,
                () => _snapshotService.CreateAsync(databases, cancellationToken),
                cancellationToken,
                result => new Dictionary<string, long> { ["databases"] = result.Databases.Count });
            using var currentKeys = await RunStageAsync(
                stages,
                WeChatReaderStage.KeyValidation,
                () => _keyDeriver.DeriveAsync(_masterKeyLease, snapshot.Databases, cancellationToken),
                cancellationToken,
                result => new Dictionary<string, long> { ["validated_databases"] = result.RelativePaths.Count });
            var queryResult = await RunStageAsync(
                stages,
                WeChatReaderStage.Query,
                () => Task.FromResult(_queryService.Read(snapshot, _masterKeyLease, request, cancellationToken)),
                cancellationToken,
                result => result.Counts);
            _diagnostics = stages;
            return new WeChatReadResult(
                queryResult.Messages,
                queryResult.TotalCount,
                queryResult.ProposedNextOffset,
                stages);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stages.Add(CreateFailureStage(exception));
            _diagnostics = stages;
            throw exception is WeChatReaderException
                ? exception
                : new WeChatReaderException(
                    WeChatReaderStage.Query,
                    WeChatReaderErrorCode.QueryFailed,
                    WeChatReaderDiagnosticSanitizer.Sanitize(exception.Message),
                    exception);
        }
        finally
        {
            if (snapshot is not null)
            {
                _snapshotService.Delete(snapshot);
            }
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            DisposeSession();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void DisposeSession()
    {
        _derivedKeys?.Dispose();
        _derivedKeys = null;
        _masterKeyLease?.Dispose();
        _masterKeyLease = null;
        _databaseRoot = null;
    }

    private static async Task<T> RunStageAsync<T>(
        ICollection<WeChatReaderStageResult> stages,
        WeChatReaderStage stage,
        Func<Task<T>> operation,
        CancellationToken cancellationToken,
        Func<T, IReadOnlyDictionary<string, long>?>? counts = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await operation();
            stopwatch.Stop();
            stages.Add(new WeChatReaderStageResult(
                stage,
                true,
                WeChatReaderErrorCode.None,
                "ok",
                startedAt,
                stopwatch.Elapsed,
                counts?.Invoke(result)));
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            throw MapStageException(stage, exception);
        }
    }

    private static WeChatReaderException MapStageException(
        WeChatReaderStage stage,
        Exception exception)
    {
        if (exception is WeChatReaderException readerException)
        {
            return readerException;
        }

        var errorCode = stage switch
        {
            WeChatReaderStage.PathDiscovery => exception is FileNotFoundException
                ? WeChatReaderErrorCode.RequiredDatabaseMissing
                : WeChatReaderErrorCode.DatabaseDirectoryMissing,
            WeChatReaderStage.KeyProbe when exception is TimeoutException => WeChatReaderErrorCode.KeyTimeout,
            WeChatReaderStage.KeyProbe when exception is FileNotFoundException => WeChatReaderErrorCode.DllMissing,
            WeChatReaderStage.KeyProbe when exception.Message.Contains("export", StringComparison.OrdinalIgnoreCase) =>
                WeChatReaderErrorCode.ExportMissing,
            WeChatReaderStage.KeyProbe => WeChatReaderErrorCode.Unknown,
            WeChatReaderStage.KeyValidation => WeChatReaderErrorCode.KeyInvalid,
            WeChatReaderStage.Snapshot => WeChatReaderErrorCode.SnapshotFailed,
            WeChatReaderStage.DatabaseOpen => WeChatReaderErrorCode.CipherUnsupported,
            WeChatReaderStage.Schema => WeChatReaderErrorCode.SchemaUnsupported,
            WeChatReaderStage.ContentDecode => WeChatReaderErrorCode.ContentCorrupt,
            WeChatReaderStage.Query => WeChatReaderErrorCode.QueryFailed,
            _ => WeChatReaderErrorCode.Unknown
        };
        return new WeChatReaderException(
            stage,
            errorCode,
            WeChatReaderDiagnosticSanitizer.Sanitize(exception.Message),
            exception);
    }

    private static WeChatReaderStageResult CreateFailureStage(Exception exception)
    {
        var readerException = exception as WeChatReaderException;
        return new WeChatReaderStageResult(
            readerException?.Stage ?? WeChatReaderStage.Query,
            false,
            readerException?.ErrorCode ?? WeChatReaderErrorCode.Unknown,
            WeChatReaderDiagnosticSanitizer.Sanitize(exception.Message),
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);
    }

    private static string NormalizeBootstrapRange(string value) =>
        value.Trim().ToLowerInvariant() is "7d" or "all" ? value.Trim().ToLowerInvariant() : "30d";
}
