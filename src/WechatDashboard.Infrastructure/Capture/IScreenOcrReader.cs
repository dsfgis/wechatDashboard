namespace WechatDashboard.Infrastructure.Capture;

public interface IScreenOcrReader
{
    Task<string> ReadWindowTextAsync(int nativeWindowHandle, CancellationToken cancellationToken);
}
