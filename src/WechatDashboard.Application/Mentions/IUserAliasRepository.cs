using WechatDashboard.Domain.Entities;

namespace WechatDashboard.Application.Mentions;

/// <summary>
/// 用户别名仓储接口：持久化 @我 检测所用的别名列表。
/// 实现见 SqliteUserAliasRepository。
/// </summary>
public interface IUserAliasRepository
{
    /// <summary>读取所有启用状态的别名。</summary>
    Task<IReadOnlyList<UserAlias>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>保存别名（重复别名不会新增）。</summary>
    Task<UserAlias> SaveAsync(string alias, CancellationToken cancellationToken);

    /// <summary>按 ID 软删除别名。</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);
}
