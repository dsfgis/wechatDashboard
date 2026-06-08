namespace WechatDashboard.Infrastructure.Capture;

public interface IExternalCommandRunner
{
    Task<ExternalCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken);
}

public sealed record ExternalCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
