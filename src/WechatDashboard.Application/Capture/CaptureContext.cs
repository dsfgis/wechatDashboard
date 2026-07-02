namespace WechatDashboard.Application.Capture;

/// <summary>
/// 采集上下文：携带各适配器的处理偏移量（offset）。
/// 适配器据此实现增量采集，避免重复读取已处理的消息。
/// </summary>
/// <param name="Offsets">适配器名称到偏移量的映射。</param>
public sealed record CaptureContext(IReadOnlyDictionary<string, string> Offsets)
{
    /// <summary>
    /// 获取指定适配器上次保存的偏移量，不存在则返回 null。
    /// </summary>
    public string? GetOffset(string adapterName)
    {
        return Offsets.TryGetValue(adapterName, out var offset) ? offset : null;
    }
}
