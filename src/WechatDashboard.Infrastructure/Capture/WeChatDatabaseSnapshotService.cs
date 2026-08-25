using System.IO;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

public sealed record WeChatDatabaseSnapshot(
    string RootPath,
    IReadOnlyList<WeChatDatabaseDescriptor> Databases);

/// <summary>只读复制微信数据库及 WAL/SHM 到当前用户的稳定工作快照。</summary>
public sealed class WeChatDatabaseSnapshotService
{
    private readonly string _snapshotRoot;

    public WeChatDatabaseSnapshotService(string? snapshotRoot = null)
    {
        _snapshotRoot = Path.GetFullPath(
            snapshotRoot ?? ProjectToolPaths.WeChatNativeSnapshotDirectory);
    }

    public async Task<WeChatDatabaseSnapshot> CreateAsync(
        IReadOnlyList<WeChatDatabaseDescriptor> sourceDatabases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDatabases);
        Directory.CreateDirectory(_snapshotRoot);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var partialRoot = Path.Combine(_snapshotRoot, id + ".partial");
        var readyRoot = Path.Combine(_snapshotRoot, id);

        try
        {
            Directory.CreateDirectory(partialRoot);
            foreach (var database in sourceDatabases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CopyDatabaseGroupAsync(database, partialRoot, cancellationToken);
            }

            Directory.Move(partialRoot, readyRoot);
            var snapshotDatabases = sourceDatabases
                .Select(source =>
                {
                    var target = Path.Combine(
                        readyRoot,
                        source.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    var info = new FileInfo(target);
                    return new WeChatDatabaseDescriptor(
                        source.RelativePath,
                        target,
                        info.Length,
                        info.LastWriteTimeUtc);
                })
                .ToArray();
            return new WeChatDatabaseSnapshot(readyRoot, snapshotDatabases);
        }
        catch (OperationCanceledException)
        {
            DeletePartialDirectory(partialRoot);
            throw;
        }
        catch (Exception exception)
        {
            DeletePartialDirectory(partialRoot);
            throw new WeChatReaderException(
                WeChatReaderStage.Snapshot,
                WeChatReaderErrorCode.SnapshotFailed,
                "Unable to create a stable WeChat database snapshot.",
                exception);
        }
    }

    /// <summary>删除一次读取完成后的稳定快照；只允许删除本服务快照根目录内的单级目录。</summary>
    public void Delete(WeChatDatabaseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalizedRoot = _snapshotRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedSnapshot = Path.GetFullPath(snapshot.RootPath).TrimEnd(Path.DirectorySeparatorChar);
        if (!normalizedSnapshot.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(normalizedSnapshot)?.TrimEnd(Path.DirectorySeparatorChar),
                _snapshotRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the native reader snapshot root.");
        }

        if (Directory.Exists(normalizedSnapshot))
        {
            Directory.Delete(normalizedSnapshot, recursive: true);
        }
    }

    private static async Task CopyDatabaseGroupAsync(
        WeChatDatabaseDescriptor database,
        string partialRoot,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(
            partialRoot,
            database.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await CopyStableFileAsync(database.FullPath, destination, required: true, cancellationToken);
        await CopyStableFileAsync(database.FullPath + "-wal", destination + "-wal", required: false, cancellationToken);
        await CopyStableFileAsync(database.FullPath + "-shm", destination + "-shm", required: false, cancellationToken);
    }

    private static async Task CopyStableFileAsync(
        string source,
        string destination,
        bool required,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                if (required)
                {
                    throw new FileNotFoundException("A required WeChat database disappeared during snapshot creation.", source);
                }

                return;
            }

            var before = new FileInfo(source);
            var beforeLength = before.Length;
            var beforeWrite = before.LastWriteTimeUtc;
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             destination,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            var after = new FileInfo(source);
            if (beforeLength == after.Length && beforeWrite == after.LastWriteTimeUtc)
            {
                return;
            }
        }

        throw new IOException("A WeChat database kept changing while its snapshot was being created.");
    }

    private void DeletePartialDirectory(string partialRoot)
    {
        var normalizedRoot = _snapshotRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPartial = Path.GetFullPath(partialRoot);
        if (!normalizedPartial.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            !normalizedPartial.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(normalizedPartial))
        {
            Directory.Delete(normalizedPartial, recursive: true);
        }
    }
}
