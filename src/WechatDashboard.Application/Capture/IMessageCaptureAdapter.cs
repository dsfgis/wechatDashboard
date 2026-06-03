namespace WechatDashboard.Application.Capture;

public interface IMessageCaptureAdapter
{
    string Name { get; }

    Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken);
}
