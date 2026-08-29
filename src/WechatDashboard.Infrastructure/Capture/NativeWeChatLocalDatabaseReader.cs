using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private readonly IWeChatDatabaseKeyStore? _keyStore;
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
        WeChatReaderStateStore? stateStore = null,
        IWeChatDatabaseKeyStore? keyStore = null)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _discovery = discovery ?? new WeChatDatabaseDiscovery();
        _snapshotService = snapshotService ?? new WeChatDatabaseSnapshotService();
        _keyDeriver = keyDeriver ?? new WeChatDatabaseKeyDeriver();
        _queryService = queryService ?? new WeChatV4MessageQueryService();
        _stateStore = stateStore ?? new WeChatReaderStateStore();
        _keyStore = keyStore;
    }

    public bool IsAvailable => !_disposed;

    public bool IsInitialized => !_disposed && _masterKeyLease is not null && !string.IsNullOrWhiteSpace(_databaseRoot);

    public IReadOnlyList<WeChatReaderStageResult> Diagnostics => _diagnostics;

    public async Task<WeChatInitializationResult> InitializeAsync(
        WeChatInitializationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await InitializeCoreAsync(
            request.DatabaseRoot,
            request.BootstrapRange,
            WeChatReaderStage.KeyProbe,
            token => _keyProvider.AcquireAsync(
                new WeChatDatabaseKeyRequest(request.ProcessId, request.KeyTimeout),
                token),
            persistValidatedKey: _keyStore is not null,
            performFullValidation: true,
            cancellationToken: cancellationToken);
    }

    public async Task<WeChatInitializationResult> RestoreProtectedKeyAsync(
        WeChatProtectedKeyRestoreRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await InitializeCoreAsync(
            request.DatabaseRoot,
            request.BootstrapRange,
            WeChatReaderStage.ProtectedKey,
            async token =>
            {
                var lease = _keyStore is null ? null : await _keyStore.LoadAsync(token);
                return lease ?? throw new WeChatReaderException(
                    WeChatReaderStage.ProtectedKey,
                    WeChatReaderErrorCode.ProtectedKeyUnavailable,
                    "未找到已保存的微信 Key，请点击“自动提取 Key”。");
            },
            persistValidatedKey: false,
            performFullValidation: false,
            cancellationToken: cancellationToken);

        if (!result.Succeeded && _keyStore is not null && result.Stages.Any(stage =>
                !stage.Succeeded &&
                (stage.ErrorCode == WeChatReaderErrorCode.ProtectedKeyInvalid ||
                 stage.ErrorCode == WeChatReaderErrorCode.KeyInvalid)))
        {
            try { await _keyStore.ClearAsync(cancellationToken); } catch { }
        }

        return result;
    }

    public async Task ClearProtectedKeyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DisposeSession();
            if (_keyStore is not null)
            {
                await _keyStore.ClearAsync(cancellationToken);
            }
            _diagnostics = Array.Empty<WeChatReaderStageResult>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WeChatInitializationResult> InitializeCoreAsync(
        string databaseRoot,
        string bootstrapRange,
        WeChatReaderStage keyStage,
        Func<CancellationToken, Task<WeChatDatabaseKeyLease>> acquireKey,
        bool persistValidatedKey,
        bool performFullValidation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                () => Task.FromResult(WeChatDatabaseDiscovery.NormalizeRoot(databaseRoot)),
                cancellationToken);
            var databases = await RunStageAsync(
                stages,
                WeChatReaderStage.PathDiscovery,
                () => Task.FromResult(_discovery.Discover(normalizedRoot)),
                cancellationToken,
                result => new Dictionary<string, long> { ["databases"] = result.Count });
            newMasterLease = await RunStageAsync(
                stages,
                keyStage,
                () => acquireKey(cancellationToken),
                cancellationToken);
            var validationProof = CreateValidationProof(newMasterLease, databases);
            var existingState = performFullValidation
                ? null
                : await _stateStore.LoadAsync(cancellationToken);
            var canReuseValidation = !performFullValidation &&
                                     existingState is not null &&
                                     string.Equals(existingState.ReaderVersion, ReaderVersion, StringComparison.Ordinal) &&
                                     string.Equals(existingState.DatabaseRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                                     ProofsMatch(existingState.SchemaFingerprint, validationProof);
            var validationDatabases = performFullValidation
                ? databases
                : SelectProtectedKeyValidationDatabase(databases);
            if (!canReuseValidation)
            {
                snapshot = await RunStageAsync(
                    stages,
                    WeChatReaderStage.Snapshot,
                    () => _snapshotService.CreateAsync(validationDatabases, cancellationToken),
                    cancellationToken,
                    result => new Dictionary<string, long> { ["databases"] = result.Databases.Count });
                newDerivedKeys = await RunStageAsync(
                    stages,
                    WeChatReaderStage.KeyValidation,
                    () => _keyDeriver.DeriveAsync(
                        newMasterLease,
                        snapshot.Databases,
                        cancellationToken,
                        validateRequiredDatabaseSet: performFullValidation),
                    cancellationToken,
                    result => new Dictionary<string, long> { ["validated_databases"] = result.RelativePaths.Count });
            }

            if (performFullValidation)
            {
                var schemaProbeStart = DateTimeOffset.UtcNow;
                await RunStageAsync(
                    stages,
                    WeChatReaderStage.Schema,
                    () => Task.FromResult(_queryService.Read(
                        snapshot!,
                        newMasterLease,
                        new WeChatReadRequest(schemaProbeStart, schemaProbeStart, 0, 1),
                        cancellationToken)),
                    cancellationToken,
                    result => result.Counts);
            }

            await RunStageAsync(
                stages,
                WeChatReaderStage.PipelineCommit,
                async () =>
                {
                    await _stateStore.SaveAsync(
                        new WeChatReaderState(
                            ReaderVersion,
                            normalizedRoot,
                            NormalizeBootstrapRange(bootstrapRange),
                            SchemaFingerprint: validationProof,
                            LastValidatedAt: DateTimeOffset.UtcNow),
                        cancellationToken);
                    return true;
                },
                cancellationToken);

            // 最后才替换正式 Key 缓存；此前任一阶段失败都不会覆盖旧缓存。
            if (persistValidatedKey && _keyStore is not null)
            {
                await RunStageAsync(
                    stages,
                    WeChatReaderStage.ProtectedKey,
                    async () =>
                    {
                        await _keyStore.SaveAsync(newMasterLease!, cancellationToken);
                        return true;
                    },
                    cancellationToken);
            }

            _masterKeyLease = newMasterLease;
            _derivedKeys = newDerivedKeys;
            _databaseRoot = normalizedRoot;
            newMasterLease = null;
            newDerivedKeys = null;
            _diagnostics = stages;
            return new WeChatInitializationResult(true, stages);
        }
        catch (OperationCanceledException)
        {
            DisposeSession();
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DisposeSession();
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
            if (_masterKeyLease is null || string.IsNullOrWhiteSpace(_databaseRoot))
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
            var sourceFingerprint = CreateMessageSourceFingerprint(databases);
            if (!string.IsNullOrWhiteSpace(request.CommittedHighWatermark) &&
                !string.IsNullOrWhiteSpace(request.CommittedSourceFingerprint) &&
                string.Equals(
                    request.CommittedSourceFingerprint,
                    sourceFingerprint,
                    StringComparison.Ordinal))
            {
                stages.Add(new WeChatReaderStageResult(
                    WeChatReaderStage.Query,
                    true,
                    WeChatReaderErrorCode.None,
                    "No durable WeChat message database changes were detected.",
                    DateTimeOffset.UtcNow,
                    TimeSpan.Zero,
                    new Dictionary<string, long> { ["shards_scanned"] = 0, ["rows_read"] = 0 }));
                _diagnostics = stages;
                return new WeChatReadResult(
                    Array.Empty<CapturedMessage>(),
                    0,
                    request.CommittedHighWatermark,
                    stages,
                    sourceFingerprint);
            }

            snapshot = await RunStageAsync(
                stages,
                WeChatReaderStage.Snapshot,
                () => _snapshotService.CreateAsync(databases, cancellationToken),
                cancellationToken,
                result => new Dictionary<string, long> { ["databases"] = result.Databases.Count });
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
                stages,
                CreateMessageSourceFingerprint(databases));
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

    internal static string CreateMessageSourceFingerprint(
        IReadOnlyList<WeChatDatabaseDescriptor> databases)
    {
        var builder = new StringBuilder();
        foreach (var database in databases
                     .Where(item => WeChatDatabaseDiscovery.IsMessageDatabase(item.RelativePath))
                     .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(database.RelativePath).Append('|');
            AppendDatabaseIdentityStamp(builder, database.FullPath);
            AppendWalContentStamp(builder, database.FullPath + "-wal");
        }

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IReadOnlyList<WeChatDatabaseDescriptor> SelectProtectedKeyValidationDatabase(
        IReadOnlyList<WeChatDatabaseDescriptor> databases)
    {
        var sessionDatabase = databases.FirstOrDefault(database =>
            string.Equals(database.RelativePath, "session/session.db", StringComparison.OrdinalIgnoreCase));
        return sessionDatabase is null
            ? Array.Empty<WeChatDatabaseDescriptor>()
            : new[] { sessionDatabase };
    }

    private static string CreateValidationProof(
        WeChatDatabaseKeyLease keyLease,
        IReadOnlyList<WeChatDatabaseDescriptor> databases)
    {
        var sessionDatabase = databases.FirstOrDefault(database =>
            string.Equals(database.RelativePath, "session/session.db", StringComparison.OrdinalIgnoreCase))
            ?? throw new WeChatReaderException(
                WeChatReaderStage.PathDiscovery,
                WeChatReaderErrorCode.RequiredDatabaseMissing,
                "The required WeChat session database was not found.");
        var salt = new byte[WeChatDatabaseKeyDeriver.SaltSize];
        using (var input = new FileStream(
                   sessionDatabase.FullPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            input.ReadExactly(salt);
        }
        var prefix = Encoding.UTF8.GetBytes($"{ReaderVersion}|{sessionDatabase.RelativePath}|");
        var material = new byte[prefix.Length + salt.Length];
        prefix.CopyTo(material, 0);
        salt.CopyTo(material, prefix.Length);
        var key = new byte[WeChatDatabaseKeyLease.KeyLength];
        try
        {
            keyLease.CopyTo(key);
            return Convert.ToBase64String(HMACSHA256.HashData(key, material));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(prefix);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static bool ProofsMatch(string? storedProof, string currentProof)
    {
        if (string.IsNullOrWhiteSpace(storedProof))
        {
            return false;
        }

        try
        {
            var stored = Convert.FromBase64String(storedProof);
            var current = Convert.FromBase64String(currentProof);
            return CryptographicOperations.FixedTimeEquals(stored, current);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void AppendDatabaseIdentityStamp(StringBuilder builder, string path)
    {
        if (!File.Exists(path))
        {
            builder.Append("missing|");
            return;
        }

        const int identityLength = 64;
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var identity = new byte[Math.Min(identityLength, checked((int)Math.Min(input.Length, identityLength)))];
        input.ReadExactly(identity);
        try
        {
            builder.Append(input.Length)
                .Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(identity)))
                .Append('|');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identity);
        }
    }

    private static void AppendWalContentStamp(StringBuilder builder, string path)
    {
        if (!File.Exists(path))
        {
            builder.Append("missing|");
            return;
        }

        const int headerLength = 64;
        const int tailLength = 64 * 1024;
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            tailLength,
            FileOptions.SequentialScan);
        var length = input.Length;
        var header = new byte[Math.Min(headerLength, checked((int)Math.Min(length, headerLength)))];
        input.ReadExactly(header);
        var tailSize = checked((int)Math.Min(length, tailLength));
        var tail = new byte[tailSize];
        input.Position = Math.Max(0, length - tailSize);
        input.ReadExactly(tail);
        try
        {
            var combined = new byte[header.Length + tail.Length];
            header.CopyTo(combined, 0);
            tail.CopyTo(combined, header.Length);
            try
            {
                builder.Append(length)
                    .Append(':')
                    .Append(Convert.ToHexString(SHA256.HashData(combined)))
                    .Append('|');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(combined);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(tail);
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
            WeChatReaderStage.ProtectedKey => WeChatReaderErrorCode.ProtectedKeyUnavailable,
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
