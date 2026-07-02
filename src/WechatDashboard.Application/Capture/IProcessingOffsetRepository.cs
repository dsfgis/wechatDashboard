namespace WechatDashboard.Application.Capture;

/// <summary>
/// 处理偏移量仓储接口：记录各适配器的增量采集进度。
/// 实现见 SqliteProcessingOffsetRepository。
/// </summary>
public interface IProcessingOffsetRepository
{
    /// <summary>读取所有适配器的偏移量。</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>保存（覆盖）指定适配器的偏移量。</summary>
    Task SaveAsync(string adapterName, string offsetValue, CancellationToken cancellationToken);
}
