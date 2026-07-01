using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Mentions;

public interface IUserAliasRepository
{
    Task<IReadOnlyList<UserAlias>> GetAllAsync(CancellationToken cancellationToken);

    Task<UserAlias> SaveAsync(string alias, CancellationToken cancellationToken);

    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
