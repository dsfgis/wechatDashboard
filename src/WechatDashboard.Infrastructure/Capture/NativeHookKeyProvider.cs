using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WechatDashboard.Application.Capture;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 通过独立 x64 KeyProbe 调用 wx_key.dll，并从当前用户限定的随机命名管道读取主密钥。
/// </summary>
public sealed class NativeHookKeyProvider : IWeChatDatabaseKeyProvider
{
    private readonly string _keyProbePath;
    private readonly string _dllPath;

    public NativeHookKeyProvider(string keyProbePath, string dllPath)
    {
        _keyProbePath = Path.GetFullPath(keyProbePath);
        _dllPath = Path.GetFullPath(dllPath);
    }

    public string Name => "native-hook";

    public async Task<WeChatDatabaseKeyLease> AcquireAsync(
        WeChatDatabaseKeyRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "A positive Weixin process id is required.");
        }
        if (!File.Exists(_keyProbePath))
        {
            throw new FileNotFoundException("WechatDashboard.KeyProbe.exe was not found.", _keyProbePath);
        }
        if (!File.Exists(_dllPath))
        {
            throw new FileNotFoundException("wx_key.dll was not found.", _dllPath);
        }

        var timeout = request.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : request.Timeout;
        var pipeName = $"WechatDashboard.KeyProbe.{Environment.ProcessId}.{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo
        {
            FileName = _keyProbePath,
            WorkingDirectory = Path.GetDirectoryName(_dllPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "--pid", request.ProcessId.ToString(CultureInfo.InvariantCulture),
                     "--pipe-name", pipeName,
                     "--dll-path", _dllPath,
                     "--timeout-seconds", Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds), 1, 600)
                         .ToString(CultureInfo.InvariantCulture)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start WechatDashboard.KeyProbe.exe.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutCts = new CancellationTokenSource(timeout + TimeSpan.FromSeconds(15));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var keyBytes = new byte[WeChatDatabaseKeyLease.KeyLength];

        try
        {
            var connectionTask = pipe.WaitForConnectionAsync(linkedCts.Token);
            var exitTask = process.WaitForExitAsync(linkedCts.Token);
            var firstCompleted = await Task.WhenAny(connectionTask, exitTask);
            if (firstCompleted == exitTask && !pipe.IsConnected)
            {
                await exitTask;
                throw new InvalidOperationException(await BuildFailureMessageAsync(process, standardOutputTask, standardErrorTask));
            }

            await connectionTask;
            await pipe.ReadExactlyAsync(keyBytes, linkedCts.Token);
            await exitTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(await BuildFailureMessageAsync(process, standardOutputTask, standardErrorTask));
            }
            if (keyBytes.All(value => value == 0))
            {
                throw new InvalidOperationException("KeyProbe returned an empty database key.");
            }

            return new WeChatDatabaseKeyLease(keyBytes);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("KeyProbe timed out before a database key was delivered.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static async Task<string> BuildFailureMessageAsync(
        Process process,
        Task<string> standardOutputTask,
        Task<string> standardErrorTask)
    {
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = $"exit code {process.ExitCode}";
        }

        var redacted = Regex.Replace(
            detail.Trim(),
            "(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
            "<REDACTED_KEY>",
            RegexOptions.CultureInvariant);
        return redacted.Length <= 500 ? redacted : redacted[..500];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
