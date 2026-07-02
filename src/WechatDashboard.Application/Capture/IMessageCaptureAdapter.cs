namespace WechatDashboard.Application.Capture;

/// <summary>
/// 消息采集适配器接口：每个来源实现各自的采集逻辑。
/// </summary>
public interface IMessageCaptureAdapter
{
    /// <summary>适配器名称，用于偏移量存储的键。</summary>
    string Name { get; }

    /// <summary>
    /// 执行一次采集，返回本批次消息与下次偏移量。
    /// </summary>
    /// <param name="context">采集上下文（含上次偏移量）。</param>
    Task<CaptureBatch> CaptureAsync(CaptureContext context, CancellationToken cancellationToken);
}
