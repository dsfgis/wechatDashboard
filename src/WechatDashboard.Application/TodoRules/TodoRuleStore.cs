namespace WechatDashboard.Application.TodoRules;

/// <summary>
/// 规则仓储的内存快照门面：采集线程只读 <see cref="Current"/>，
/// 任何界面入口（模型配置页、待办详情）写入后都立即刷新同一份快照，
/// 因此新增规则无需重启即可同时影响本地拦截与模型提示词。
/// </summary>
public sealed class TodoRuleStore
{
    private readonly ITodoRuleRepository _repository;
    // 引用赋值是原子的：读取方要么看到旧快照，要么看到新快照，不需要加锁。
    private volatile IReadOnlyList<TodoRule> _current = Array.Empty<TodoRule>();

    public TodoRuleStore(ITodoRuleRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>当前生效的规则快照（含已停用项，匹配时再按 IsEnabled 过滤）。</summary>
    public IReadOnlyList<TodoRule> Current => _current;

    /// <summary>从数据库重新加载快照；读取失败时保留原快照，避免规则突然全部失效。</summary>
    public async Task<IReadOnlyList<TodoRule>> LoadAsync(CancellationToken cancellationToken)
    {
        _current = await _repository.GetAllAsync(cancellationToken);
        return _current;
    }

    /// <summary>新增规则并刷新快照。</summary>
    public async Task AddAsync(string text, CancellationToken cancellationToken)
    {
        await _repository.AddAsync(text, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>启用或停用规则并刷新快照。</summary>
    public async Task SetEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken)
    {
        await _repository.SetEnabledAsync(id, isEnabled, cancellationToken);
        await LoadAsync(cancellationToken);
    }

    /// <summary>删除规则并刷新快照。</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(id, cancellationToken);
        await LoadAsync(cancellationToken);
    }
}
