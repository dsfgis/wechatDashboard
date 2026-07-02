using System.Diagnostics;

namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 外部命令执行器实现：启动子进程运行外部命令（如 Python 脚本），
/// 捕获标准输出/错误，超时 6 分钟后强制终止进程树。
/// </summary>
public sealed class ProcessExternalCommandRunner : IExternalCommandRunner
{
    /// <summary>
    /// 运行外部命令并返回结果。
    /// </summary>
    /// <param name="executablePath">可执行文件路径。</param>
    /// <param name="arguments">参数列表。</param>
    /// <param name="workingDirectory">工作目录，为空则用当前目录。</param>
    /// <param name="environment">环境变量。</param>
    public async Task<ExternalCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            // 工作目录兜底
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            // 不使用 Shell 启动，便于重定向
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // 逐个添加参数（避免拼接引号问题）
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 注入环境变量
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start external command '{executablePath}'.");
        }

        // 并发读取标准输出与标准错误，避免管道死锁
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // 6 分钟超时保护
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // 超时：强制杀掉整个进程树
            try { process.Kill(entireProcessTree: true); } catch { }
            return new ExternalCommandResult(-1, "", "Command timed out after 6 minutes.");
        }

        return new ExternalCommandResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }
}
