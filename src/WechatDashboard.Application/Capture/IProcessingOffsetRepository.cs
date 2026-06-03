namespace WechatDashboard.Application.Capture;

public interface IProcessingOffsetRepository
{
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(string adapterName, string offsetValue, CancellationToken cancellationToken);
}
