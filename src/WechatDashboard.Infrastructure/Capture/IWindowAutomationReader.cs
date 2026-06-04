namespace WechatDashboard.Infrastructure.Capture;

public interface IWindowAutomationReader
{
    Task<WindowAutomationReadResult> ReadTopLevelWindowsAsync(CancellationToken cancellationToken);
}
