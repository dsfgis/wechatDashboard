using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Capture;

public interface IMessageRepository
{
    Task<bool> ExistsAsync(string source, string sourceMessageKey, CancellationToken cancellationToken);

    Task<Message> SaveAsync(Message message, CancellationToken cancellationToken);

    Task<IReadOnlyList<Message>> GetRecentAsync(int limit, CancellationToken cancellationToken);

    Task<MessagePage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken);
}

public sealed record MessagePage(
    IReadOnlyList<Message> Messages,
    int TotalCount,
    int PageNumber,
    int PageSize);
