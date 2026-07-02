namespace WechatDashboard.Infrastructure.Capture;

/// <summary>
/// 外部命令执行器接口：抽象子进程调用，便于测试时替换为桩实现。
/// </summary>
public interface IExternalCommandRunner
{
    /// <summary>运行外部命令并返回退出码、标准输出与标准错误。</summary>
    Task<ExternalCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

/// <summary>
/// 外部命令执行结果。
/// </summary>
/// <param name="ExitCode">进程退出码，0 表示成功。</param>
/// <param name="StandardOutput">标准输出内容。</param>
/// <param name="StandardError">标准错误内容。</param>
public sealed record ExternalCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
