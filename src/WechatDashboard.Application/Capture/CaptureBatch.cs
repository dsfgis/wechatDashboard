namespace WechatDashboard.Application.Capture;

/// <summary>
/// 单个适配器一次采集返回的批次。
/// </summary>
/// <param name="AdapterName">适配器名称。</param>
/// <param name="Messages">本次采集到的消息列表。</param>
/// <param name="NextOffset">下次采集应使用的偏移量。</param>
public sealed record CaptureBatch(
    string AdapterName,
    IReadOnlyList<CapturedMessage> Messages,
    string NextOffset);
